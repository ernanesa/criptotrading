using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Exchange.Paper.Tests;

public sealed class PaperExchangeTests
{
    [Fact]
    public void UnknownTickLiquidityDoesNotInventACompleteFillByDefault()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(at));
        exchange.Submit(BuyMarket("unknown-volume", 1m), new IdempotencyKey("unknown-volume"));
        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at)));
        Assert.Equal(OrderStatus.Submitted, exchange.GetOrder(new ClientOrderId("unknown-volume")).Status);
    }

    [Fact]
    public void ExplicitZeroTickLiquidityIsKnownButCannotFill()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(at));
        exchange.Submit(BuyMarket("zero-volume", 1m), new IdempotencyKey("zero-volume"));
        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 0m)));
    }

    [Fact]
    public void CandleCloseApproximationRequiresExplicitOptInAndNeverOverridesKnownZeroLiquidity()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(new PaperExchangeOptions { LiquidityModel = PaperLiquidityModel.CandleCloseApproximation }, new ManualTimeProvider(at));
        exchange.Submit(BuyMarket("candle-only", 1m), new IdempotencyKey("candle-only"));
        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 0m)));
        Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at.AddSeconds(1))));
    }

    [Fact]
    public void RepeatedOrRegressedTicksCannotSpendTheSameObservedLiquidityAgain()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(at));
        var request = BuyMarket("dedup", 3m);
        exchange.Submit(request, new IdempotencyKey("dedup"));
        var tick = new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 1m);
        Assert.Single(exchange.Apply(tick));
        Assert.Empty(exchange.Apply(tick));
        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at.AddSeconds(-1), 2m)));
        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 2m)));
        Assert.Equal(1m, exchange.GetOrder(request.ClientOrderId).FilledQuantity.Value);
        Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at.AddSeconds(1), 2m)));
        Assert.Equal(OrderStatus.Filled, exchange.GetOrder(request.ClientOrderId).Status);
    }

    [Fact]
    public void DefaultPriceStructsCannotBypassPaperTickValidation()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaperMarketTick(new Symbol("BTCUSDT"), default, default, default, at, 1m));
    }

    [Fact]
    public void SamePriceOrdersUseAcceptanceTimeInsteadOfLexicalClientOrderIds()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(at));
        var older = new PaperOrderRequest(new ClientOrderId("z-older"), "BTCUSDT", OrderSide.Buy, OrderType.Limit, 1m, 101m);
        exchange.Submit(older, new IdempotencyKey("older"));
        exchange.Submit(older, new IdempotencyKey("older"));
        exchange.Submit(new(new ClientOrderId("a-later"), "BTCUSDT", OrderSide.Buy, OrderType.Limit, 1m, 101m), new IdempotencyKey("later"));
        Assert.Equal(older.ClientOrderId, Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 1m))).ClientOrderId);
    }

    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public void MoreCompetitiveLimitPriceHasPriorityWithinTheSameSide(OrderSide side)
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(at));
        exchange.Submit(new(new ClientOrderId("a-passive"), "BTCUSDT", side, OrderType.Limit, 1m, side == OrderSide.Buy ? 101m : 100m), new IdempotencyKey("passive"));
        exchange.Submit(new(new ClientOrderId("z-competitive"), "BTCUSDT", side, OrderType.Limit, 1m, side == OrderSide.Buy ? 102m : 99m), new IdempotencyKey("competitive"));
        Assert.Equal(new ClientOrderId("z-competitive"), Assert.Single(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, at, 1m))).ClientOrderId);
    }

    [Fact]
    public void MarketOrderAppliesExplicitFeeAndSlippage()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var exchange = new PaperExchange(new PaperExchangeOptions { FeeRate = new(0.001m), SlippageRate = new(0.01m) }, clock);
        var request = BuyMarket("first", 2m);
        exchange.Submit(request, new IdempotencyKey("submit-1"));

        var fills = exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100.5m, clock.GetUtcNow(), 2m));

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

    [Theory]
    [InlineData(OrderSide.Buy)]
    [InlineData(OrderSide.Sell)]
    public void LimitOrderDoesNotFillBeyondItsPriceAfterSlippage(OrderSide side)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(new PaperExchangeOptions { SlippageRate = new(0.01m) }, new ManualTimeProvider(now));
        var request = new PaperOrderRequest(new ClientOrderId("limit-slippage"), "BTCUSDT", side, OrderType.Limit, 1m, 100m);
        exchange.Submit(request, new IdempotencyKey("limit-slippage"));
        var tick = side == OrderSide.Buy
            ? new PaperMarketTick("BTCUSDT", 99m, 100m, 99.5m, now, 1m)
            : new PaperMarketTick("BTCUSDT", 100m, 101m, 100.5m, now, 1m);

        Assert.Empty(exchange.Apply(tick));
        Assert.Equal(OrderStatus.Submitted, exchange.GetOrder(request.ClientOrderId).Status);
    }

    [Fact]
    public void ReplayIsDeterministicForSameTicksAndTimeProvider()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ticks = new[] { new PaperMarketTick("BTCUSDT", 100m, 101m, 100.5m, start, 1m) };
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

    [Fact]
    public void AvailableTickQuantityIsSharedAcrossEligibleOrders()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(now));
        exchange.Submit(BuyMarket("first", 1m), new IdempotencyKey("first"));
        exchange.Submit(BuyMarket("second", 1m), new IdempotencyKey("second"));

        var fills = exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, now, 1.5m));

        Assert.Equal(1.5m, fills.Sum(fill => fill.Quantity.Value));
        Assert.Equal(2, fills.Count);
        Assert.Equal(1m, fills[0].Quantity.Value);
        Assert.Equal(0.5m, fills[1].Quantity.Value);
    }

    [Fact]
    public void FillHistoryIsAnImmutableSnapshot()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new ManualTimeProvider(now));
        exchange.Submit(BuyMarket("snapshot", 1m), new IdempotencyKey("snapshot"));
        var before = exchange.Fills;

        exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, now, 1m));

        Assert.Empty(before);
        Assert.Single(exchange.Fills);
    }

    [Fact]
    public void ExhaustedLiquidityDoesNotHideOrderRejection()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(new PaperExchangeOptions { MaxOrderQuantity = 1m }, new ManualTimeProvider(now));
        exchange.Submit(BuyMarket("a", 1m), new IdempotencyKey("a"));
        var oversized = BuyMarket("b", 2m);
        exchange.Submit(oversized, new IdempotencyKey("b"));

        exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, now, 1m));

        Assert.Equal(OrderStatus.Rejected, exchange.GetOrder(oversized.ClientOrderId).Status);
    }

    [Fact]
    public void ConcurrentIdenticalSubmissionsCreateOneOrder()
    {
        var exchange = new PaperExchange();
        var request = BuyMarket("concurrent", 1m);
        var results = new PaperSubmitResult[32];

        Parallel.For(0, results.Length, index =>
            results[index] = exchange.Submit(request, new IdempotencyKey("concurrent")));

        Assert.Single(exchange.OpenOrders);
        Assert.Single(results, result => !result.AlreadyProcessed);
        Assert.All(results, result => Assert.Equal(results[0].Order.ClientOrderId, result.Order.ClientOrderId));
    }

    [Fact]
    public void SubmissionLatencyDelaysExecutionUntilTheOrderIsEligible()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(start);
        var exchange = new PaperExchange(new PaperExchangeOptions { SubmissionLatency = TimeSpan.FromSeconds(5) }, clock);
        var request = BuyMarket("latency", 1m);
        exchange.Submit(request, new IdempotencyKey("submit-latency"));

        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, start.AddSeconds(4), 1m)));
        var fills = exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, start.AddSeconds(5), 1m));

        Assert.Single(fills);
        Assert.Equal(OrderStatus.Filled, exchange.GetOrder(request.ClientOrderId).Status);
    }

    [Fact]
    public void SpreadLimitRejectsWithoutCreatingAFill()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var exchange = new PaperExchange(new PaperExchangeOptions { MaxSpreadRate = new(0.01m) }, clock);
        var request = BuyMarket("spread-limit", 1m);
        exchange.Submit(request, new IdempotencyKey("submit-spread"));

        Assert.Empty(exchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 103m, 101m, clock.GetUtcNow())));

        var order = exchange.GetOrder(request.ClientOrderId);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(PaperRejectionReason.SpreadLimit, order.RejectionReason);
        Assert.Empty(exchange.Fills);
    }

    [Fact]
    public void QuantityAndNotionalBoundsRejectOrdersDeterministically()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var quantityExchange = new PaperExchange(new PaperExchangeOptions { MaxOrderQuantity = 1m }, clock);
        var quantityRequest = BuyMarket("quantity-limit", 2m);
        quantityExchange.Submit(quantityRequest, new IdempotencyKey("submit-quantity"));
        quantityExchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, clock.GetUtcNow()));
        Assert.Equal(PaperRejectionReason.QuantityLimit, quantityExchange.GetOrder(quantityRequest.ClientOrderId).RejectionReason);

        var notionalExchange = new PaperExchange(new PaperExchangeOptions { MaxOrderNotional = 100m }, clock);
        var notionalRequest = BuyMarket("notional-limit", 2m);
        notionalExchange.Submit(notionalRequest, new IdempotencyKey("submit-notional"));
        notionalExchange.Apply(new PaperMarketTick("BTCUSDT", 100m, 101m, 100m, clock.GetUtcNow()));
        Assert.Equal(PaperRejectionReason.NotionalLimit, notionalExchange.GetOrder(notionalRequest.ClientOrderId).RejectionReason);
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
