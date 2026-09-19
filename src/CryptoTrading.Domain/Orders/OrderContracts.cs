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

/// <summary>
/// Aggregate root for OMS order lifecycle. State changes are explicit and idempotent
/// by command key; adapters and persistence must persist the transition before sending
/// an exchange command.
/// </summary>
public sealed class Order
{
    private readonly Dictionary<IdempotencyKey, OrderTransition> transitions = [];

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
        };
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
        UpdatedAtUtc = occurredAtUtc;
        return transition;
    }

    private static bool IsAllowed(OrderStatus current, OrderStatus target) =>
        current == target || (current, target) switch
        {
            (OrderStatus.Created, OrderStatus.Validated or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Validated, OrderStatus.Submitted or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Submitted, OrderStatus.Acknowledged or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.Acknowledged, OrderStatus.PartiallyFilled or OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired) => true,
            (OrderStatus.PartiallyFilled, OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Expired) => true,
            _ => false,
        };
}
