#pragma warning disable CA1700 // 'Reserved' is the canonical domain state name for a pre-submission capital reservation.

namespace CryptoTrading.Persistence;

/// <summary>Lifecycle state of a durable capital reservation.</summary>
public enum CapitalReservationState
{
    Pending,
    Active,
    PartiallyFilled,
    Released,
    LockedUnknown,
}

/// <summary>Lifecycle status matching PostgreSQL check constraint.</summary>
public enum CapitalReservationStatus
{
    Reserved,
    CommittedToExchange,
    PartiallyFilled,
    Released,
    LockedUnknown,
}

/// <summary>Durable capital reservation record persisted before any order submission.</summary>
public sealed record CapitalReservationRecord(
    string ReservationId,
    string AccountId,
    string Symbol,
    string ClientOrderId,
    string Asset,
    decimal ReservedAmount,
    decimal ConsumedAmount,
    CapitalReservationState State,
    string WorkerInstanceId,
    long FenceToken,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Reason = null,
    string Environment = "TESTNET")
{
    public CapitalReservationRecord(
        string reservationId,
        string accountId,
        string symbol,
        string clientOrderId,
        string asset,
        decimal reservedAmount,
        decimal consumedAmount,
        CapitalReservationStatus status,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string? reason = null,
        string environment = "TESTNET")
        : this(
            reservationId,
            accountId,
            symbol,
            clientOrderId,
            asset,
            reservedAmount,
            consumedAmount,
            status switch
            {
                CapitalReservationStatus.Reserved => CapitalReservationState.Pending,
                CapitalReservationStatus.CommittedToExchange => CapitalReservationState.Active,
                CapitalReservationStatus.PartiallyFilled => CapitalReservationState.PartiallyFilled,
                CapitalReservationStatus.Released => CapitalReservationState.Released,
                CapitalReservationStatus.LockedUnknown => CapitalReservationState.LockedUnknown,
                _ => CapitalReservationState.Pending,
            },
            workerInstanceId,
            fencingToken,
            createdAtUtc,
            updatedAtUtc,
            reason,
            environment)
    {
    }

    public CapitalReservationStatus Status => State switch
    {
        CapitalReservationState.Pending => CapitalReservationStatus.Reserved,
        CapitalReservationState.Active => CapitalReservationStatus.CommittedToExchange,
        CapitalReservationState.PartiallyFilled => CapitalReservationStatus.PartiallyFilled,
        CapitalReservationState.Released => CapitalReservationStatus.Released,
        CapitalReservationState.LockedUnknown => CapitalReservationStatus.LockedUnknown,
        _ => CapitalReservationStatus.Reserved,
    };

    public long FencingToken => FenceToken;

    public decimal RemainingLockedAmount =>
        State is CapitalReservationState.Released
            ? 0m
            : Math.Max(0m, ReservedAmount - ConsumedAmount);

    public bool IsActive =>
        State is CapitalReservationState.Pending
            or CapitalReservationState.Active
            or CapitalReservationState.PartiallyFilled
            or CapitalReservationState.LockedUnknown;
}

/// <summary>Atomic request to reserve capital under an operator budget ceiling and worker fencing token.</summary>
public sealed record CapitalReservationRequest
{
    public CapitalReservationRequest(
        string reservationId,
        string accountId,
        string symbol,
        string clientOrderId,
        string asset,
        decimal requestedAmount,
        decimal effectiveBudgetCeilingAmount,
        decimal openPositionsCommittedAmount,
        decimal realizedLossesAmount,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset occurredAtUtc,
        string environment = "TESTNET",
        int openPositionsCount = 0,
        int maxOpenPositions = 1,
        decimal newOrderFeeReserveAmount = 0m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestedAmount, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveBudgetCeilingAmount, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(openPositionsCommittedAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(openPositionsCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOpenPositions, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(realizedLossesAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(newOrderFeeReserveAmount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fencingToken, 0L);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Reservation timestamp must be UTC.", nameof(occurredAtUtc));
        }

        ReservationId = reservationId.Trim();
        AccountId = accountId.Trim();
        Environment = environment.Trim();
        Symbol = symbol.Trim().ToUpperInvariant();
        ClientOrderId = clientOrderId.Trim();
        Asset = asset.Trim().ToUpperInvariant();
        RequestedAmount = requestedAmount;
        EffectiveBudgetCeilingAmount = effectiveBudgetCeilingAmount;
        OpenPositionsCommittedAmount = openPositionsCommittedAmount;
        OpenPositionsCount = openPositionsCount;
        MaxOpenPositions = maxOpenPositions;
        RealizedLossesAmount = realizedLossesAmount;
        NewOrderFeeReserveAmount = newOrderFeeReserveAmount;
        WorkerInstanceId = workerInstanceId.Trim();
        FenceToken = fencingToken;
        OccurredAtUtc = occurredAtUtc;
    }

    public string ReservationId { get; }
    public string AccountId { get; }
    public string Environment { get; }
    public string Symbol { get; }
    public string ClientOrderId { get; }
    public string Asset { get; }
    public decimal RequestedAmount { get; }
    public decimal EffectiveBudgetCeilingAmount { get; }
    public decimal OpenPositionsCommittedAmount { get; }
    public int OpenPositionsCount { get; }
    public int MaxOpenPositions { get; }
    public decimal RealizedLossesAmount { get; }
    public decimal NewOrderFeeReserveAmount { get; }
    public string WorkerInstanceId { get; }
    public long FenceToken { get; }
    public long FencingToken => FenceToken;
    public DateTimeOffset OccurredAtUtc { get; }
}

/// <summary>Outcome of an atomic capital reservation attempt.</summary>
public sealed record CapitalReservationResult(
    bool Succeeded,
    CapitalReservationRecord? Reservation,
    decimal TotalActiveReservedAmount,
    string ReasonCode);

/// <summary>
/// Durable capital reservation store enforcing atomic budget ceilings,
/// single-writer fencing tokens, and sticky LockedUnknown capital protection.
/// </summary>
public interface ICapitalReservationStore
{
    Task<CapitalReservationResult> TryReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> ReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> TransitionReservationStateAsync(
        string clientOrderId,
        CapitalReservationState targetState,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> TransitionReservationAsync(
        string clientOrderId,
        CapitalReservationStatus targetStatus,
        decimal consumedAmount,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> ReleaseReservationOnTerminalOrderAsync(
        string clientOrderId,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> MarkReservationLockedUnknownAsync(
        string clientOrderId,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationState resolvedState,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);

    Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationStatus resolvedStatus,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CapitalReservationRecord>> GetActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CapitalReservationRecord>> ReadActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe, serializable in-memory implementation of <see cref="ICapitalReservationStore"/>
/// for deterministic unit and concurrency testing.
/// </summary>
public sealed class InMemoryCapitalReservationStore : InMemoryTradingStore
{
}


