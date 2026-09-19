using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NewOrderStartsCreatedAndRecordsCreationCommand()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(Now, order.CreatedAtUtc);
        Assert.Equal(Now, order.UpdatedAtUtc);
        Assert.Single(order.Transitions);
    }

    [Fact]
    public void ValidLifecycleCanReachFilled()
    {
        var order = NewOrder();

        order.TransitionTo(OrderStatus.Validated, Key("validate"), Now.AddSeconds(1));
        order.TransitionTo(OrderStatus.Submitted, Key("submit"), Now.AddSeconds(2));
        order.TransitionTo(OrderStatus.Acknowledged, Key("ack"), Now.AddSeconds(3), "exchange-1");
        order.TransitionTo(OrderStatus.PartiallyFilled, Key("partial"), Now.AddSeconds(4));
        var filled = order.TransitionTo(OrderStatus.Filled, Key("fill"), Now.AddSeconds(5));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(OrderStatus.PartiallyFilled, filled.From);
        Assert.Equal("exchange-1", order.Transitions.Single(t => t.To == OrderStatus.Acknowledged).ExchangeOrderId);
        Assert.Equal(Now.AddSeconds(5), order.UpdatedAtUtc);
    }

    [Fact]
    public void InvalidTransitionDoesNotMutateOrder()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOrderTransitionException>(() =>
            order.TransitionTo(OrderStatus.Filled, Key("invalid"), Now.AddSeconds(1)));

        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Single(order.Transitions);
    }

    [Fact]
    public void ReplayingSameIdempotencyKeyIsNoOp()
    {
        var order = NewOrder();
        var key = Key("validate");

        var first = order.TransitionTo(OrderStatus.Validated, key, Now.AddSeconds(1));
        var replay = order.TransitionTo(OrderStatus.Validated, key, Now.AddSeconds(99));

        Assert.Same(first, replay);
        Assert.Equal(OrderStatus.Validated, order.Status);
        Assert.Equal(Now.AddSeconds(1), order.UpdatedAtUtc);
        Assert.Equal(2, order.Transitions.Count);
    }

    [Fact]
    public void ReusingIdempotencyKeyForDifferentCommandIsRejected()
    {
        var order = NewOrder();
        var key = Key("command");
        order.TransitionTo(OrderStatus.Validated, key, Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            order.TransitionTo(OrderStatus.Rejected, key, Now.AddSeconds(2)));
        Assert.Equal(OrderStatus.Validated, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Filled, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Submitted)]
    [InlineData(OrderStatus.Rejected, OrderStatus.Validated)]
    [InlineData(OrderStatus.Expired, OrderStatus.Acknowledged)]
    [InlineData(OrderStatus.Submitted, OrderStatus.PartiallyFilled)]
    public void TerminalOrOutOfOrderTransitionsAreRejected(OrderStatus current, OrderStatus target)
    {
        var order = NewOrder();
        MoveTo(order, current);

        Assert.Throws<InvalidOrderTransitionException>(() =>
            order.TransitionTo(target, Key($"invalid-{current}-{target}"), Now.AddMinutes(1)));
    }

    [Fact]
    public void PartiallyFilledCanBeCancelledOrFilled()
    {
        var cancelled = NewOrder();
        MoveTo(cancelled, OrderStatus.PartiallyFilled);
        cancelled.TransitionTo(OrderStatus.Cancelled, Key("cancel"), Now.AddSeconds(10));
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

        var filled = NewOrder();
        MoveTo(filled, OrderStatus.PartiallyFilled);
        filled.TransitionTo(OrderStatus.Filled, Key("fill"), Now.AddSeconds(10));
        Assert.Equal(OrderStatus.Filled, filled.Status);
    }

    [Fact]
    public void OrderShapeIsValidatedAtCreation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Create(
            OrderId.New(), new ClientOrderId("ct-invalid-qty"), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Market, new Quantity(0m), null, Key("create"), Now));

        Assert.Throws<ArgumentException>(() => Order.Create(
            OrderId.New(), new ClientOrderId("ct-missing-price"), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Limit, new Quantity(1m), null, Key("create"), Now));

        Assert.Throws<ArgumentException>(() => Order.Create(
            OrderId.New(), new ClientOrderId("ct-market-price"), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Market, new Quantity(1m), new Price(100m), Key("create"), Now));
    }

    private static Order NewOrder() => Order.Create(
        OrderId.New(), new ClientOrderId("ct-test-order"), new Symbol("BTCUSDT"),
        OrderSide.Buy, OrderType.Market, new Quantity(1m), null, Key("create"), Now);

    private static IdempotencyKey Key(string suffix) => new($"test-{suffix}");

    private static void MoveTo(Order order, OrderStatus target)
    {
        OrderStatus[] path = target switch
        {
            OrderStatus.Created => [],
            OrderStatus.Validated => [OrderStatus.Validated],
            OrderStatus.Submitted => [OrderStatus.Validated, OrderStatus.Submitted],
            OrderStatus.Acknowledged => [OrderStatus.Validated, OrderStatus.Submitted, OrderStatus.Acknowledged],
            OrderStatus.PartiallyFilled => [OrderStatus.Validated, OrderStatus.Submitted, OrderStatus.Acknowledged, OrderStatus.PartiallyFilled],
            OrderStatus.Filled => [OrderStatus.Validated, OrderStatus.Submitted, OrderStatus.Acknowledged, OrderStatus.Filled],
            OrderStatus.Cancelled => [OrderStatus.Validated, OrderStatus.Submitted, OrderStatus.Acknowledged, OrderStatus.Cancelled],
            OrderStatus.Rejected => [OrderStatus.Rejected],
            OrderStatus.Expired => [OrderStatus.Expired],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        var index = 0;
        foreach (var state in path)
        {
            order.TransitionTo(state, Key($"move-{index++}-{state}"), Now.AddSeconds(index));
        }
    }
}
