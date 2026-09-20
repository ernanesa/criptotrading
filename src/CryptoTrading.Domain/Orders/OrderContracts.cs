using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Orders;

/// <summary>Lifecycle state of an order owned by the OMS.</summary>
public enum OrderStatus
{
    Created,
    Validated,
    Submitted,
    Acknowledged,
    PartiallyFilled,
    /// <summary>The venue outcome is not yet provable; new exposure must remain blocked.</summary>
    Unknown,
    /// <summary>A cancel request was accepted locally but its venue outcome is pending.</summary>
    CancelPending,
    Filled,
    Cancelled,
    Rejected,
    Expired,
}

public enum OrderSide
{
    Buy,
    Sell,
}

public enum OrderType
{
    Market,
    Limit,
}

/// <summary>Raised when an OMS command attempts an illegal lifecycle transition.</summary>
public sealed class InvalidOrderTransitionException : InvalidOperationException
{
    public InvalidOrderTransitionException()
    {
    }

    public InvalidOrderTransitionException(string message)
        : base(message)
    {
    }

    public InvalidOrderTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InvalidOrderTransitionException(OrderStatus current, OrderStatus requested)
        : base($"Order cannot transition from {current} to {requested}.")
    {
        Current = current;
        Requested = requested;
    }

    public OrderStatus Current { get; }

    public OrderStatus Requested { get; }
}

/// <summary>Immutable audit information for one accepted lifecycle command.</summary>
public sealed record OrderTransition(
    OrderStatus From,
    OrderStatus To,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string? ExchangeOrderId = null);

/// <summary>One venue execution applied to an OMS order.</summary>
public sealed record OrderFill
{
    public OrderFill(
        string executionId,
        Quantity quantity,
        Price price,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (executionId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(executionId));
        }

        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Fill quantity must be greater than zero.");
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fill timestamp must be UTC.", nameof(occurredAtUtc));
        }

        ExecutionId = executionId.Trim();
        Quantity = quantity;
        Price = price;
        OccurredAtUtc = occurredAtUtc;
        IdempotencyKey = idempotencyKey;
    }

    public string ExecutionId { get; }

    public Quantity Quantity { get; }

    public Price Price { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public IdempotencyKey IdempotencyKey { get; }
}

/// <summary>Result of applying a fill, including whether it was a replay.</summary>
public sealed record OrderFillResult(OrderFill Fill, bool IsDuplicate, OrderTransition Transition);

/// <summary>
/// Aggregate root for OMS order lifecycle. State changes are explicit and idempotent
/// by command key; adapters and persistence must persist the transition before sending
/// an exchange command.
/// </summary>
public sealed class Order
{
    private readonly Dictionary<IdempotencyKey, OrderTransition> transitions = [];
    private readonly Dictionary<string, OrderFill> fills = new(StringComparer.Ordinal);

    private Order(
        OrderId id,
        ClientOrderId clientOrderId,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        Quantity quantity,
        Price? limitPrice,
        IdempotencyKey creationIdempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Order quantity must be greater than zero.");
        }

        if (type is OrderType.Limit && limitPrice is null)
        {
            throw new ArgumentException("Limit orders require a limit price.", nameof(limitPrice));
        }

        if (type is OrderType.Market && limitPrice is not null)
        {
            throw new ArgumentException("Market orders cannot have a limit price.", nameof(limitPrice));
        }

        Id = id;
        ClientOrderId = clientOrderId;
        Symbol = symbol;
        Side = side;
        Type = type;
        Quantity = quantity;
        LimitPrice = limitPrice;
        Status = OrderStatus.Created;
        CreatedAtUtc = createdAtUtc;
        transitions.Add(creationIdempotencyKey, new OrderTransition(
            OrderStatus.Created,
            OrderStatus.Created,
            creationIdempotencyKey,
            createdAtUtc));
    }

    public OrderId Id { get; }

    public ClientOrderId ClientOrderId { get; }

    public Symbol Symbol { get; }

    public OrderSide Side { get; }

    public OrderType Type { get; }

    public Quantity Quantity { get; }

    public Price? LimitPrice { get; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Quantity FilledQuantity { get; private set; }

    public Quantity RemainingQuantity => new(Quantity.Value - FilledQuantity.Value);

    public IReadOnlyCollection<OrderFill> Fills => fills.Values;

    /// <summary>Latest exchange-assigned order identity, when acknowledged by the venue.</summary>
    public string? ExchangeOrderId { get; private set; }

    public IReadOnlyCollection<OrderTransition> Transitions => transitions.Values;

    public static Order Create(
        OrderId id,
        ClientOrderId clientOrderId,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        Quantity quantity,
        Price? limitPrice,
        IdempotencyKey idempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        return new Order(id, clientOrderId, symbol, side, type, quantity, limitPrice, idempotencyKey, createdAtUtc)
        {
            UpdatedAtUtc = createdAtUtc,
            FilledQuantity = new Quantity(0m),
        };
    }

    /// <summary>
    /// Applies one venue execution exactly once. A replay with the same
    /// execution identity and identical data is a no-op; conflicting data
    /// fails closed. Event timestamps may arrive out of order.
    /// </summary>
    public OrderFillResult ApplyFill(OrderFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        if (fills.TryGetValue(fill.ExecutionId, out var existing))
        {
            if (!existing.Equals(fill))
            {
                throw new InvalidOperationException(
                    $"Execution '{fill.ExecutionId}' was already applied with different data.");
            }

            var replayTransition = transitions.Values.Last(static item =>
                item.To is OrderStatus.PartiallyFilled or OrderStatus.Filled);
            return new(existing, true, replayTransition);
        }

        if (Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired or OrderStatus.Filled)
        {
            throw new InvalidOrderTransitionException(Status, OrderStatus.PartiallyFilled);
        }

        var nextFilledQuantity = FilledQuantity.Value + fill.Quantity.Value;
        if (nextFilledQuantity > Quantity.Value)
        {
            throw new InvalidOperationException("Applied fills cannot exceed the original order quantity.");
        }

        fills.Add(fill.ExecutionId, fill);
        FilledQuantity = new Quantity(nextFilledQuantity);
        var target = nextFilledQuantity == Quantity.Value
            ? OrderStatus.Filled
            : OrderStatus.PartiallyFilled;
        var transition = TransitionTo(target, fill.IdempotencyKey, fill.OccurredAtUtc);
        return new(fill, false, transition);
    }

    /// <summary>
    /// Applies a lifecycle command. Replaying an already accepted key is a no-op.
    /// Reusing a key for another target is rejected to prevent ambiguous retries.
    /// </summary>
    public OrderTransition TransitionTo(
        OrderStatus target,
        IdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        string? exchangeOrderId = null)
    {
        if (transitions.TryGetValue(idempotencyKey, out var previous))
        {
            if (previous.To != target)
            {
                throw new InvalidOperationException(
                    $"Idempotency key '{idempotencyKey}' was already used for transition to {previous.To}.");
            }

            return previous;
        }

        if (!IsAllowed(Status, target))
        {
            throw new InvalidOrderTransitionException(Status, target);
        }

        var transition = new OrderTransition(Status, target, idempotencyKey, occurredAtUtc, exchangeOrderId);
        transitions.Add(idempotencyKey, transition);
        Status = target;
        if (!string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            ExchangeOrderId = exchangeOrderId;
        }
        if (occurredAtUtc > UpdatedAtUtc)
        {
            UpdatedAtUtc = occurredAtUtc;
        }
        return transition;
    }

    private static bool IsAllowed(OrderStatus current, OrderStatus target) =>
        current == target || (current, target) switch
        {
            (OrderStatus.Created, OrderStatus.Validated or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Validated, OrderStatus.Submitted or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Submitted, OrderStatus.Acknowledged or OrderStatus.Unknown or OrderStatus.CancelPending or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Acknowledged, OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Unknown or OrderStatus.CancelPending or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.PartiallyFilled, OrderStatus.Filled or OrderStatus.Unknown or OrderStatus.CancelPending or OrderStatus.Cancelled or OrderStatus.Expired) => true,
            (OrderStatus.CancelPending, OrderStatus.Unknown or OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Unknown, OrderStatus.Acknowledged or OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.CancelPending or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired) => true,
            _ => false,
        };
}
