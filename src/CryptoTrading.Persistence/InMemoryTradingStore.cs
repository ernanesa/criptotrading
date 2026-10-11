namespace CryptoTrading.Persistence;

/// <summary>
/// Thread-safe in-memory trading store implementing capital reservation lifecycle,
/// multi-worker fencing, budget ceiling validation, and authoritative LockedUnknown reconciliation.
/// </summary>
public class InMemoryTradingStore : ICapitalReservationStore
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, (string WorkerInstanceId, long FencingToken)> fences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapitalReservationRecord> reservationsByClientOrderId = new(StringComparer.Ordinal);

    public Task<CapitalReservationResult> TryReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            var fenceKey = $"{request.AccountId}:{request.Environment}";
            if (fences.TryGetValue(fenceKey, out var fence))
            {
                if (request.FenceToken < fence.FencingToken ||
                    (request.FenceToken == fence.FencingToken &&
                     !string.Equals(request.WorkerInstanceId, fence.WorkerInstanceId, StringComparison.Ordinal)))
                {
                    var currentActive = SumActiveReserved(request.AccountId, request.Asset);
                    return Task.FromResult(new CapitalReservationResult(
                        false,
                        null,
                        currentActive,
                        "STALE_WORKER_FENCING_TOKEN"));
                }
            }

            fences[fenceKey] = (request.WorkerInstanceId, request.FenceToken);

            var idempotencyKey = $"{request.AccountId}:{request.Environment}:{request.ClientOrderId}";
            if (reservationsByClientOrderId.TryGetValue(idempotencyKey, out var existing))
            {
                if (!string.Equals(existing.ReservationId, request.ReservationId, StringComparison.Ordinal) ||
                    !string.Equals(existing.AccountId, request.AccountId, StringComparison.Ordinal) ||
                    !string.Equals(existing.Symbol, request.Symbol, StringComparison.Ordinal) ||
                    !string.Equals(existing.Asset, request.Asset, StringComparison.Ordinal) ||
                    existing.ReservedAmount != request.RequestedAmount)
                {
                    throw new InvalidOperationException("Capital reservation clientOrderId is already associated with different parameters.");
                }

                return Task.FromResult(new CapitalReservationResult(
                    true,
                    existing,
                    SumActiveReserved(request.AccountId, request.Asset),
                    "IDEMPOTENT_REPLAY"));
            }

            if (HasLockedUnknownReservation(request.AccountId, request.Asset))
            {
                var currentActive = SumActiveReserved(request.AccountId, request.Asset);
                return Task.FromResult(new CapitalReservationResult(
                    false,
                    null,
                    currentActive,
                    "LOCKED_UNKNOWN_CAPITAL_ACTIVE"));
            }

            var activeReserved = SumActiveReserved(request.AccountId, request.Asset);
            var projectedCommitted = request.OpenPositionsCommittedAmount
                + activeReserved
                + request.RequestedAmount
                + request.NewOrderFeeReserveAmount
                + request.RealizedLossesAmount;

            if (projectedCommitted > request.EffectiveBudgetCeilingAmount)
            {
                return Task.FromResult(new CapitalReservationResult(
                    false,
                    null,
                    activeReserved,
                    "BUDGET_CEILING_EXCEEDED"));
            }

            if (request.OpenPositionsCount + 1 > request.MaxOpenPositions)
            {
                return Task.FromResult(new CapitalReservationResult(
                    false,
                    null,
                    activeReserved,
                    "MAX_OPEN_POSITIONS_EXCEEDED"));
            }

            var record = new CapitalReservationRecord(
                request.ReservationId,
                request.AccountId,
                request.Symbol,
                request.ClientOrderId,
                request.Asset,
                request.RequestedAmount,
                0m,
                CapitalReservationState.Pending,
                request.WorkerInstanceId,
                request.FenceToken,
                request.OccurredAtUtc,
                request.OccurredAtUtc,
                "INITIAL_RESERVATION",
                request.Environment);

            reservationsByClientOrderId[idempotencyKey] = record;
            return Task.FromResult(new CapitalReservationResult(
                true,
                record,
                activeReserved + request.RequestedAmount,
                "RESERVED"));
        }
    }

    public async Task<CapitalReservationRecord> ReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await TryReserveCapitalAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Reservation is null)
        {
            throw new InvalidOperationException($"Failed to reserve capital: {result.ReasonCode}");
        }

        return result.Reservation;
    }

    public Task<CapitalReservationRecord> TransitionReservationStateAsync(
        string clientOrderId,
        CapitalReservationState targetState,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerInstanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(consumedAmount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fenceToken, 0L);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition timestamp must be UTC.", nameof(occurredAtUtc));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            var normalizedId = clientOrderId.Trim();
            var entry = reservationsByClientOrderId.FirstOrDefault(pair =>
                string.Equals(pair.Value.ClientOrderId, normalizedId, StringComparison.Ordinal));
            if (entry.Value is null)
            {
                throw new InvalidOperationException($"Capital reservation '{normalizedId}' was not found.");
            }

            var existing = entry.Value;
            var fenceKey = $"{existing.AccountId}:{existing.Environment}";
            if (fences.TryGetValue(fenceKey, out var fence))
            {
                if (fenceToken < fence.FencingToken ||
                    (fenceToken == fence.FencingToken &&
                     !string.Equals(workerInstanceId.Trim(), fence.WorkerInstanceId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("STALE_WORKER_FENCING_TOKEN");
                }
            }

            if (existing.State == CapitalReservationState.LockedUnknown)
            {
                throw new InvalidOperationException("LOCKED_UNKNOWN_REQUIRES_RECONCILIATION");
            }

            if (existing.State == CapitalReservationState.Released && targetState != CapitalReservationState.Released)
            {
                throw new InvalidOperationException("Cannot transition a released capital reservation.");
            }

            if (consumedAmount > existing.ReservedAmount)
            {
                throw new ArgumentOutOfRangeException(nameof(consumedAmount), "Consumed amount cannot exceed reserved amount.");
            }

            fences[fenceKey] = (workerInstanceId.Trim(), fenceToken);
            var updated = existing with
            {
                State = targetState,
                ConsumedAmount = consumedAmount,
                WorkerInstanceId = workerInstanceId.Trim(),
                FenceToken = fenceToken,
                UpdatedAtUtc = occurredAtUtc,
                Reason = reason,
            };
            reservationsByClientOrderId[entry.Key] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<CapitalReservationRecord> TransitionReservationAsync(
        string clientOrderId,
        CapitalReservationStatus targetStatus,
        decimal consumedAmount,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var targetState = targetStatus switch
        {
            CapitalReservationStatus.Reserved => CapitalReservationState.Pending,
            CapitalReservationStatus.CommittedToExchange => CapitalReservationState.Active,
            CapitalReservationStatus.PartiallyFilled => CapitalReservationState.PartiallyFilled,
            CapitalReservationStatus.Released => CapitalReservationState.Released,
            CapitalReservationStatus.LockedUnknown => CapitalReservationState.LockedUnknown,
            _ => CapitalReservationState.Pending,
        };

        return TransitionReservationStateAsync(
            clientOrderId,
            targetState,
            consumedAmount,
            workerInstanceId,
            fencingToken,
            occurredAtUtc,
            reason,
            cancellationToken);
    }

    public Task<CapitalReservationRecord> ReleaseReservationOnTerminalOrderAsync(
        string clientOrderId,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionReservationStateAsync(
            clientOrderId,
            CapitalReservationState.Released,
            consumedAmount,
            workerInstanceId,
            fenceToken,
            occurredAtUtc,
            reason ?? "TERMINAL_ORDER_RELEASE",
            cancellationToken);

    public Task<CapitalReservationRecord> MarkReservationLockedUnknownAsync(
        string clientOrderId,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionReservationStateAsync(
            clientOrderId,
            CapitalReservationState.LockedUnknown,
            0m,
            workerInstanceId,
            fenceToken,
            occurredAtUtc,
            reason ?? "ORDER_STATE_LOCKED_UNKNOWN",
            cancellationToken);

    public Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationState resolvedState,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliationCorrelationId);
        ArgumentOutOfRangeException.ThrowIfNegative(consumedAmount);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Reconciliation timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (resolvedState is CapitalReservationState.Pending or CapitalReservationState.LockedUnknown)
        {
            throw new ArgumentException("Reconciliation must resolve LockedUnknown to Active, PartiallyFilled, or Released.", nameof(resolvedState));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            var normalizedId = clientOrderId.Trim();
            var entry = reservationsByClientOrderId.FirstOrDefault(pair =>
                string.Equals(pair.Value.ClientOrderId, normalizedId, StringComparison.Ordinal));
            if (entry.Value is null)
            {
                throw new InvalidOperationException($"Capital reservation '{normalizedId}' was not found.");
            }

            var existing = entry.Value;
            if (consumedAmount > existing.ReservedAmount)
            {
                throw new ArgumentOutOfRangeException(nameof(consumedAmount), "Consumed amount cannot exceed reserved amount.");
            }

            var updated = existing with
            {
                State = resolvedState,
                ConsumedAmount = consumedAmount,
                UpdatedAtUtc = occurredAtUtc,
                Reason = $"RECONCILED:{reconciliationCorrelationId.Trim()}",
            };
            reservationsByClientOrderId[entry.Key] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationStatus resolvedStatus,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var resolvedState = resolvedStatus switch
        {
            CapitalReservationStatus.CommittedToExchange => CapitalReservationState.Active,
            CapitalReservationStatus.PartiallyFilled => CapitalReservationState.PartiallyFilled,
            CapitalReservationStatus.Released => CapitalReservationState.Released,
            _ => throw new ArgumentException("Reconciliation must resolve LockedUnknown to CommittedToExchange, PartiallyFilled, or Released.", nameof(resolvedStatus)),
        };

        return ReconcileLockedUnknownAsync(
            clientOrderId,
            resolvedState,
            consumedAmount,
            reconciliationCorrelationId,
            occurredAtUtc,
            cancellationToken);
    }

    public Task<IReadOnlyList<CapitalReservationRecord>> GetActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        ReadActiveReservationsAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<CapitalReservationRecord>> ReadActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            var normalizedAccount = accountId.Trim();
            var result = reservationsByClientOrderId.Values
                .Where(item => string.Equals(item.AccountId, normalizedAccount, StringComparison.Ordinal) && item.IsActive)
                .OrderBy(static item => item.CreatedAtUtc)
                .ThenBy(static item => item.ReservationId, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult<IReadOnlyList<CapitalReservationRecord>>(result);
        }
    }

    private decimal SumActiveReserved(string accountId, string asset) =>
        reservationsByClientOrderId.Values
            .Where(item => string.Equals(item.AccountId, accountId, StringComparison.Ordinal) &&
                           string.Equals(item.Asset, asset, StringComparison.Ordinal) &&
                           item.IsActive)
            .Sum(static item => item.RemainingLockedAmount);

    private bool HasLockedUnknownReservation(string accountId, string asset) =>
        reservationsByClientOrderId.Values
            .Any(item => string.Equals(item.AccountId, accountId, StringComparison.Ordinal) &&
                         string.Equals(item.Asset, asset, StringComparison.Ordinal) &&
                         item.State == CapitalReservationState.LockedUnknown);
}