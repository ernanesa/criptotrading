using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Exchange.Paper.Tests;

public sealed class PaperExchangeTests
{
    [Fact]
    public void MarketOrderAppliesExplicitFeeAndSlippage()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var exchange = new PaperExchange(new PaperExchangeOptions { FeeRate = new(0.001m), SlippageRate = new(0.01m) }, clock);
        var request = BuyMarket("first", 2m);
        exchange.Submit(request, new IdempotencyKey("submit-1"));

        var fills = exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100.5m, clock.GetUtcNow()));

        var fill = Assert.Single(fills);
        Assert.Equal(2m, fill.Quantity.Value);
        Assert.Equal(102.01m, fill.Price.Value);
        Assert.Equal(0.20402m, fill.Fee.Amount);
        Assert.Equal(2.02m, fill.SlippageAmount);
        Assert.Equal(OrderStatus.Filled, exchange.GetOrder(request.ClientOrderId).Status);
    }

    [Fact]
    public void LimitOrderWaitsUntilQuoteIsExecutableAndCancelIsIdempotent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var exchange = new PaperExchange(timeProvider: clock);
        var request = new PaperOrderRequest(new ClientOrderId("limit-1"), "BTCUSDT", OrderSide.Buy, OrderType.Limit, 1m, 100m);
        exchange.Submit(request, new IdempotencyKey("submit-2"));

        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, clock.GetUtcNow())));
        var cancelled = exchange.Cancel(request.ClientOrderId, new IdempotencyKey("cancel-1"));
        var replayed = exchange.Cancel(request.ClientOrderId, new IdempotencyKey("cancel-1"));

        Assert.Equal(OrderStatus.Cancelled, cancelled.Order.Status);
        Assert.True(replayed.AlreadyProcessed);
    }

    [Fact]
    public void ReplayIsDeterministicForSameTicksAndTimeProvider()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ticks = new[] { new PaperMarketTick("BTCUSDT", 100m, 101m, 100.5m, start) };
        var first = NewExchange(start);
        var second = NewExchange(start);
        var firstRequest = BuyMarket("replay-a", 1m);
        var secondRequest = BuyMarket("replay-a", 1m);
        first.Submit(firstRequest, new IdempotencyKey("submit-replay"));
        second.Submit(secondRequest, new IdempotencyKey("submit-replay"));

        var firstFill = Assert.Single(first.Replay(ticks));
        var secondFill = Assert.Single(second.Replay(ticks));

        Assert.Equal(firstFill.Price, secondFill.Price);
        Assert.Equal(firstFill.Fee, secondFill.Fee);
        Assert.Equal(firstFill.OccurredAtUtc, secondFill.OccurredAtUtc);
    }

    [Fact]
    public void OpenOrderBoundIsEnforced()
    {
        var exchange = new PaperExchange(new PaperExchangeOptions { MaxOpenOrders = 1 });
        exchange.Submit(BuyMarket("one", 1m), new IdempotencyKey("one"));

        Assert.Throws<InvalidOperationException>(() => exchange.Submit(BuyMarket("two", 1m), new IdempotencyKey("two")));
    }

    [Fact]
    public void AvailableTickQuantityProducesPartialThenCompleteFill()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var exchange = new PaperExchange(timeProvider: clock);
        var request = BuyMarket("partial", 3m);
        exchange.Submit(request, new IdempotencyKey("submit-partial"));

        var first = Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, clock.GetUtcNow(), 1m)));
        var second = Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, clock.GetUtcNow().AddSeconds(1), 2m)));

        Assert.Equal(1m, first.Quantity.Value);
        Assert.Equal(2m, second.Quantity.Value);
        Assert.Equal(OrderStatus.Filled, exchange.GetOrder(request.ClientOrderId).Status);
    }

    private static PaperExchange NewExchange(DateTimeOffset start) =>
        new(new PaperExchangeOptions { FeeRate = new(0.001m), SlippageRate = new(0.01m) }, new ManualTimeProvider(start));

    private static PaperOrderRequest BuyMarket(string clientId, decimal quantity) =>
        new(new ClientOrderId(clientId), "BTCUSDT", OrderSide.Buy, OrderType.Market, quantity);

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}
