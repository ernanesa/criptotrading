using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Execution;

/// <summary>Venue statuses understood by the cancel lifecycle boundary.</summary>
public enum CancelVenueStatus
{
    New,
    PartiallyFilled,
    PendingCancel,
    Filled,
    Canceled,
    Expired,
    Rejected,
    Unknown,
}

/// <summary>Action selected after an order cancel observation.</summary>
public enum CancelResolutionAction
{
    AwaitReconciliation,
    Completed,
    ReplanResidual,
}

/// <summary>Immutable state needed to decide whether an expired passive leg may be cancelled.</summary>
public sealed record CancelableExecutionSnapshot
{
    public CancelableExecutionSnapshot(
        OrderId orderId,
        ClientOrderId clientOrderId,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        Quantity originalQuantity,
        Quantity filledQuantity,
        Price? limitPrice,
        CorrelationId correlationId,
        IdempotencyKey idempotencyKey,
        OrderStatus status,
        int attempt,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset observedAtUtc,
        string? exchangeOrderId = null)
    {
        if (expiresAtUtc.Offset != TimeSpan.Zero || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Cancel lifecycle timestamps must be UTC.");
        }

        if (originalQuantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(originalQuantity));
        }

        if (filledQuantity.Value < 0m || filledQuantity.Value > originalQuantity.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(filledQuantity));
        }

        if (type == OrderType.Limit && limitPrice is not { Value: > 0m })
        {
            throw new ArgumentException("Limit execution snapshots require a positive limit price.", nameof(limitPrice));
        }

        if (type == OrderType.Market && limitPrice is not null)
        {
            throw new ArgumentException("Market execution snapshots cannot have a limit price.", nameof(limitPrice));
        }

        if (attempt is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (!string.IsNullOrWhiteSpace(exchangeOrderId) &&
            (!long.TryParse(exchangeOrderId, out var parsedOrderId) || parsedOrderId < 0))
        {
            throw new ArgumentException("Exchange order ID must be a non-negative integer.", nameof(exchangeOrderId));
        }

        OrderId = orderId;
        ClientOrderId = clientOrderId;
        Symbol = symbol;
        Side = side;
        Type = type;
        OriginalQuantity = originalQuantity;
        FilledQuantity = filledQuantity;
        LimitPrice = limitPrice;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        Status = status;
        Attempt = attempt;
        ExpiresAtUtc = expiresAtUtc;
        ObservedAtUtc = observedAtUtc;
        ExchangeOrderId = string.IsNullOrWhiteSpace(exchangeOrderId) ? null : exchangeOrderId.Trim();
    }

    public OrderId OrderId { get; }
    public ClientOrderId ClientOrderId { get; }
    public Symbol Symbol { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public Quantity OriginalQuantity { get; }
    public Quantity FilledQuantity { get; }
    public Price? LimitPrice { get; }
    public CorrelationId CorrelationId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public OrderStatus Status { get; }
    public int Attempt { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string? ExchangeOrderId { get; }

    public Quantity ResidualQuantity => new(OriginalQuantity.Value - FilledQuantity.Value);
}

/// <summary>Exchange command created only after the TTL and lifecycle gates pass.</summary>
public sealed record CancelOrderCommand
{
    public CancelOrderCommand(
        OrderId orderId,
        ClientOrderId clientOrderId,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        Quantity originalQuantity,
        Quantity filledQuantity,
        Price? limitPrice,
        CorrelationId correlationId,
        IdempotencyKey idempotencyKey,
        int attempt,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        string? exchangeOrderId = null)
    {
        if (requestedAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Cancel command timestamps must be UTC.");
        }

        if (filledQuantity.Value < 0m || filledQuantity.Value > originalQuantity.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(filledQuantity));
        }

        if (originalQuantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(originalQuantity));
        }

        if (attempt is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        OrderId = orderId;
        ClientOrderId = clientOrderId;
        Symbol = symbol;
        Side = side;
        Type = type;
        OriginalQuantity = originalQuantity;
        FilledQuantity = filledQuantity;
        LimitPrice = limitPrice;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        Attempt = attempt;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ExchangeOrderId = string.IsNullOrWhiteSpace(exchangeOrderId) ? null : exchangeOrderId.Trim();
    }

    public OrderId OrderId { get; }
    public ClientOrderId ClientOrderId { get; }
    public Symbol Symbol { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public Quantity OriginalQuantity { get; }
    public Quantity FilledQuantity { get; }
    public Price? LimitPrice { get; }
    public CorrelationId CorrelationId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public int Attempt { get; }
    public DateTimeOffset RequestedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string? ExchangeOrderId { get; }

    public Quantity ResidualQuantity => new(OriginalQuantity.Value - FilledQuantity.Value);
}

/// <summary>Durable-boundary envelope for a cancel command.</summary>
public sealed record CancelOutboxRecord(
    Guid MessageId,
    string MessageType,
    CorrelationId CorrelationId,
    IdempotencyKey IdempotencyKey,
    CancelOrderCommand Command,
    DateTimeOffset CreatedAtUtc);

public sealed record CancelPlanResult(
    CancelOrderCommand? Command,
    CancelOutboxRecord? OutboxRecord,
    string Reason)
{
    public bool IsDispatchable => Command is not null && OutboxRecord is not null;
}

public sealed record CancelResolution(
    CancelResolutionAction Action,
    OrderStatus ProjectedStatus,
    Quantity ExecutedQuantity,
    Quantity ResidualQuantity,
    string Reason)
{
    public bool CanReplan => Action == CancelResolutionAction.ReplanResidual;
}

/// <summary>Pure TTL/cancel/replan policy. It never performs exchange I/O.</summary>
public static class CancelLifecyclePlanner
{
    public const string CancelOrderMessageType = "orders.cancel.v1";
    public const string TtlNotReached = "CANCEL_TTL_NOT_REACHED";
    public const string TerminalOrder = "CANCEL_ORDER_TERMINAL";
    public const string AwaitingReconciliation = "CANCEL_AWAITING_RECONCILIATION";
    public const string NotCancelable = "CANCEL_ORDER_NOT_RECONCILABLE";
    public const string CancelRequired = "CANCEL_REQUIRED";
    public const string ResidualReadyForReplan = "CANCEL_RESIDUAL_READY_FOR_REPLAN";

    public static CancelPlanResult Plan(CancelableExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired)
        {
            return new(null, null, TerminalOrder);
        }

        if (snapshot.Status is OrderStatus.Unknown or OrderStatus.CancelPending)
        {
            return new(null, null, AwaitingReconciliation);
        }

        if (snapshot.ObservedAtUtc < snapshot.ExpiresAtUtc)
        {
            return new(null, null, TtlNotReached);
        }

        // Submitted is not enough evidence that the venue accepted an order;
        // cancelling it here could race a delayed submit acknowledgement.
        if (snapshot.Status is not (OrderStatus.Acknowledged or OrderStatus.PartiallyFilled))
        {
            return new(null, null, NotCancelable);
        }

        var command = new CancelOrderCommand(
            snapshot.OrderId,
            snapshot.ClientOrderId,
            snapshot.Symbol,
            snapshot.Side,
            snapshot.Type,
            snapshot.OriginalQuantity,
            snapshot.FilledQuantity,
            snapshot.LimitPrice,
            snapshot.CorrelationId,
            new IdempotencyKey($"cancel:{snapshot.IdempotencyKey.Value}:{snapshot.Attempt}"),
            snapshot.Attempt,
            snapshot.ObservedAtUtc,
            snapshot.ExpiresAtUtc,
            snapshot.ExchangeOrderId);
        var messageId = DeterministicMessageId(command);
        var outbox = new CancelOutboxRecord(
            messageId,
            CancelOrderMessageType,
            command.CorrelationId,
            command.IdempotencyKey,
            command,
            command.RequestedAtUtc);
        return new(command, outbox, CancelRequired);
    }

    public static CancelResolution Resolve(
        CancelOrderCommand command,
        CancelVenueStatus status,
        Quantity executedQuantity)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (executedQuantity.Value < 0m || executedQuantity.Value > command.OriginalQuantity.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(executedQuantity));
        }

        var residual = new Quantity(command.OriginalQuantity.Value - executedQuantity.Value);
        return status switch
        {
            CancelVenueStatus.Canceled or CancelVenueStatus.Expired when residual.Value > 0m =>
                new(CancelResolutionAction.ReplanResidual,
                    status == CancelVenueStatus.Canceled ? OrderStatus.Cancelled : OrderStatus.Expired,
                    executedQuantity,
                    residual,
                    ResidualReadyForReplan),
            CancelVenueStatus.Canceled => new(CancelResolutionAction.Completed, OrderStatus.Cancelled, executedQuantity, residual, "CANCELLED_NO_RESIDUAL"),
            CancelVenueStatus.Expired => new(CancelResolutionAction.Completed, OrderStatus.Expired, executedQuantity, residual, "EXPIRED_NO_RESIDUAL"),
            CancelVenueStatus.Filled when executedQuantity.Value == command.OriginalQuantity.Value =>
                new(CancelResolutionAction.Completed, OrderStatus.Filled, executedQuantity, residual, "FILLED"),
            CancelVenueStatus.New => new(CancelResolutionAction.AwaitReconciliation, OrderStatus.Acknowledged, executedQuantity, residual, "CANCEL_REMAINS_OPEN"),
            CancelVenueStatus.PartiallyFilled => new(CancelResolutionAction.AwaitReconciliation, OrderStatus.PartiallyFilled, executedQuantity, residual, "CANCEL_PARTIAL_REMAINS_OPEN"),
            CancelVenueStatus.PendingCancel => new(CancelResolutionAction.AwaitReconciliation, OrderStatus.CancelPending, executedQuantity, residual, "CANCEL_PENDING"),
            _ => new(CancelResolutionAction.AwaitReconciliation, OrderStatus.Unknown, executedQuantity, residual, "CANCEL_OUTCOME_UNKNOWN"),
        };
    }

    private static Guid DeterministicMessageId(CancelOrderCommand command)
    {
        var canonical = string.Join(
            '|',
            command.OrderId,
            command.ClientOrderId.Value,
            command.IdempotencyKey.Value,
            command.Attempt);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(bytes[..16]);
    }
}
