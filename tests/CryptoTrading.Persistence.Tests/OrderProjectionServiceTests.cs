#pragma warning disable CA1515 // xUnit discovers public test fixtures.
#pragma warning disable CA2007 // Test assertions intentionally follow the task context.

using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class OrderProjectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task OpenOrderTransitionIsProjectedWithExchangeCompatibleStatus()
    {
        var store = new FakeOpenOrderStore();
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(
            order,
            OrderStatus.Submitted,
            Key("submit"),
            Now.AddSeconds(2),
            exchangeOrderId: "12345",
            cancellationToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Upserts);
        Assert.Equal("BTCUSDT", record.Symbol);
        Assert.Equal(12345L, record.OrderId);
        Assert.Equal(order.ClientOrderId.Value, record.ClientOrderId);
        Assert.Equal("NEW", record.Status);
        Assert.Empty(store.Removed);
    }

    [Fact]
    public async Task PartialFillIsProjectedAndTerminalTransitionRemovesOpenOrder()
    {
        var store = new FakeOpenOrderStore();
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(order, OrderStatus.Submitted, Key("submit"), Now.AddSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(order, OrderStatus.Acknowledged, Key("ack"), Now.AddSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(order, OrderStatus.PartiallyFilled, Key("partial"), Now.AddSeconds(4), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(order, OrderStatus.Filled, Key("fill"), Now.AddSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, store.Upserts.Count);
        Assert.Equal("PARTIALLY_FILLED", store.Upserts[^1].Status);
        Assert.Equal([order.ClientOrderId.Value], store.Removed);
    }

    [Fact]
    public async Task LaterTransitionsPreserveTheExchangeOrderIdentity()
    {
        var store = new FakeOpenOrderStore();
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(
            order, OrderStatus.Submitted, Key("submit"), Now.AddSeconds(2), exchangeOrderId: "98765", cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(
            order, OrderStatus.Acknowledged, Key("ack"), Now.AddSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(
            order, OrderStatus.PartiallyFilled, Key("partial"), Now.AddSeconds(4), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(store.Upserts, record => Assert.Equal(98765L, record.OrderId));
        Assert.Equal("PARTIALLY_FILLED", store.Upserts[^1].Status);
        Assert.Equal("98765", order.ExchangeOrderId);
    }

    [Fact]
    public async Task PreSubmissionStatesAreNotProjectedUnlessExplicitlyRequested()
    {
        var store = new FakeOpenOrderStore();
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(store.Upserts);

        await service.ProjectCurrentAsync(order, projectBeforeSubmission: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("NEW", Assert.Single(store.Upserts).Status);
    }

    [Fact]
    public async Task ProjectionCanBeRetriedAfterStoreFailureWithoutASecondLifecycleTransition()
    {
        var store = new FakeOpenOrderStore { FailNextUpsert = true };
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransitionAndProjectAsync(
            order, OrderStatus.Submitted, Key("submit"), Now.AddSeconds(2), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(OrderStatus.Submitted, order.Status);
        await service.ProjectCurrentAsync(order, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(store.Upserts);
        Assert.Equal("NEW", store.Upserts[0].Status);
        var replay = await service.TransitionAndProjectAsync(
            order, OrderStatus.Submitted, Key("submit"), Now.AddSeconds(99), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Equal(OrderStatus.Submitted, replay.To);
        Assert.Equal(3, order.Transitions.Count);
        Assert.Equal(2, store.Upserts.Count);
    }

    [Fact]
    public async Task LifecycleTransitionIsPersistedBeforeItsCurrentStateProjection()
    {
        var store = new FakeOpenOrderStore();
        var lifecycle = new FakeOrderLifecycleStore();
        var service = new OrderProjectionService(store, lifecycle);
        var order = NewOrder();

        await service.TransitionAndProjectAsync(order, OrderStatus.Validated, Key("validate"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken);
        await service.TransitionAndProjectAsync(order, OrderStatus.Submitted, Key("submit"), Now.AddSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Collection(
            lifecycle.Events,
            item => Assert.Equal((OrderStatus.Created, OrderStatus.Validated), (item.Transition.From, item.Transition.To)),
            item => Assert.Equal((OrderStatus.Validated, OrderStatus.Submitted), (item.Transition.From, item.Transition.To)));
        Assert.Single(store.Upserts);
    }

    [Fact]
    public async Task InvalidTransitionAndInvalidExchangeIdentityDoNotWriteProjection()
    {
        var store = new FakeOpenOrderStore();
        var service = new OrderProjectionService(store);
        var order = NewOrder();

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => service.TransitionAndProjectAsync(
            order, OrderStatus.Filled, Key("invalid"), Now.AddSeconds(1), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FormatException>(() => service.TransitionAndProjectAsync(
            order, OrderStatus.Submitted, Key("bad-id"), Now.AddSeconds(2), exchangeOrderId: "paper-1", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(store.Upserts);
        Assert.Empty(store.Removed);
        Assert.Equal(OrderStatus.Created, order.Status);
    }

    private static Order NewOrder() => Order.Create(
        OrderId.Parse("11111111111111111111111111111111"),
        new ClientOrderId("ct-projection-test"),
        new Symbol("BTCUSDT"),
        OrderSide.Buy,
        OrderType.Market,
        new Quantity(1m),
        limitPrice: null,
        Key("create"),
        Now);

    private static IdempotencyKey Key(string value) => new($"projection-{value}");

    private sealed class FakeOpenOrderStore : ILocalOpenOrderStore
    {
        public List<LocalOpenOrderRecord> Upserts { get; } = [];

        public List<string> Removed { get; } = [];

        public bool FailNextUpsert { get; set; }

        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>(Upserts);

        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
        {
            if (FailNextUpsert)
            {
                FailNextUpsert = false;
                throw new InvalidOperationException("simulated local store failure");
            }

            Upserts.Add(order);
            return Task.CompletedTask;
        }

        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
        {
            Removed.Add(clientOrderId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderLifecycleStore : IOrderLifecycleStore
    {
        public List<OrderLifecycleEvent> Events { get; } = [];

        public Task<bool> AppendOrderLifecycleEventAsync(
            OrderLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(lifecycleEvent);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OrderLifecycleEvent>> ReadOrderLifecycleEventsAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderLifecycleEvent>>(
                Events.Where(e => string.Equals(e.ClientOrderId, clientOrderId, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}
