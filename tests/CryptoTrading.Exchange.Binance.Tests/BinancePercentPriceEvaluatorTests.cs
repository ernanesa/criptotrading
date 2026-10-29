using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinancePercentPriceEvaluatorTests
{
    [Fact]
    public void PriceInsideSymmetricBandPasses()
    {
        var filter = new BinanceSymbolFilter("PERCENT_PRICE", MultiplierUp: "1.3", MultiplierDown: "0.7");

        var violations = BinancePercentPriceEvaluator.Evaluate(
            filter, BinanceSpotOrderSide.Buy, 100m, referencePrice: 100m);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(131)]
    [InlineData(69)]
    public void PriceOutsideSymmetricBandFails(decimal price)
    {
        var filter = new BinanceSymbolFilter("PERCENT_PRICE", MultiplierUp: "1.3", MultiplierDown: "0.7");

        var violations = BinancePercentPriceEvaluator.Evaluate(
            filter, BinanceSpotOrderSide.Buy, price, referencePrice: 100m);

        Assert.Single(violations);
    }

    [Fact]
    public void SideSpecificBandUsesBidForBuyAndAskForSell()
    {
        var filter = new BinanceSymbolFilter(
            "PERCENT_PRICE_BY_SIDE",
            BidMultiplierUp: "1.1", BidMultiplierDown: "0.9",
            AskMultiplierUp: "2.0", AskMultiplierDown: "0.8");

        var buy = BinancePercentPriceEvaluator.Evaluate(filter, BinanceSpotOrderSide.Buy, 115m, 100m);
        var sell = BinancePercentPriceEvaluator.Evaluate(filter, BinanceSpotOrderSide.Sell, 115m, 100m);

        Assert.Contains(buy, item => item.Code == "PERCENT_PRICE_ABOVE_MAXIMUM");
        Assert.Empty(sell);
    }

    [Fact]
    public void MissingReferencePriceFailsClosed()
    {
        var filter = new BinanceSymbolFilter("PERCENT_PRICE", MultiplierUp: "1.3", MultiplierDown: "0.7");

        var violations = BinancePercentPriceEvaluator.Evaluate(
            filter, BinanceSpotOrderSide.Buy, 100m, referencePrice: null);

        Assert.Contains(violations, item => item.Code == "PERCENT_PRICE_REFERENCE_UNAVAILABLE");
    }

    [Fact]
    public void InvalidMultipliersFailClosed()
    {
        var filter = new BinanceSymbolFilter("PERCENT_PRICE", MultiplierUp: "0", MultiplierDown: "0.7");

        var violations = BinancePercentPriceEvaluator.Evaluate(
            filter, BinanceSpotOrderSide.Buy, 100m, referencePrice: 100m);

        Assert.Contains(violations, item => item.Code == "FILTER_METADATA_UNAVAILABLE");
    }

    [Fact]
    public async Task PriceInsideBandDoesNotBypassUnverifiedEvidence()
    {
        var inner = new CountingOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new StubRules(CreateRules()),
            new StubAccountFilters(),
            new StubTradingState(),
            new StubReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() =>
            client.SubmitAsync(Order(price: 105m), TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PRETRADE_EVIDENCE_UNVERIFIED");
        Assert.Equal(0, inner.Submits);
    }

    [Fact]
    public async Task BoundaryBlocksOrderOutsidePercentBand()
    {
        var inner = new CountingOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new StubRules(CreateRules()),
            new StubAccountFilters(),
            new StubTradingState(),
            new StubReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(
            () => client.SubmitAsync(Order(price: 140m), TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PERCENT_PRICE_ABOVE_MAXIMUM");
        Assert.Equal(0, inner.Submits);
    }

    [Fact]
    public async Task BoundaryBlocksWhenReferencePriceProviderIsMissing()
    {
        var inner = new CountingOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new StubRules(CreateRules()),
            new StubAccountFilters(),
            new StubTradingState());

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(
            () => client.SubmitAsync(Order(price: 105m), TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PERCENT_PRICE_REFERENCE_UNAVAILABLE");
        Assert.Equal(0, inner.Submits);
    }

    [Theory]
    [InlineData("mismatched-symbol")]
    [InlineData("stale")]
    [InlineData("future")]
    public async Task BoundaryRejectsReferencePriceWithoutProvenIdentityOrFreshness(string failure)
    {
        var now = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var evidence = failure switch
        {
            "mismatched-symbol" => new BinanceReferencePriceEvidence("ETHUSDT", 100m, now),
            "stale" => new BinanceReferencePriceEvidence("BTCUSDT", 100m, now.AddMinutes(-5)),
            _ => new BinanceReferencePriceEvidence("BTCUSDT", 100m, now.AddMinutes(5))
        };
        var inner = new CountingOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new StubRules(CreateRules()), new StubAccountFilters(), new StubTradingState(),
            new ConstantReferencePrice(evidence), new FixedClock(now));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(
            () => client.SubmitAsync(Order(price: 105m), TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PERCENT_PRICE_REFERENCE_UNAVAILABLE");
        Assert.Equal(0, inner.Submits);
    }

    private sealed class ConstantReferencePrice(BinanceReferencePriceEvidence evidence) : IBinanceReferencePriceProvider
    {
        public Task<BinanceReferencePriceEvidence?> GetReferencePriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<BinanceReferencePriceEvidence?>(evidence);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BinanceSpotOrderRequest Order(decimal price) =>
        new("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.1m, "ct-percent", price);

    private static BinanceSymbolTradingRules CreateRules() => new(
        "BTCUSDT", "TRADING", 0.01m, 1_000_000m, 0.01m, 0.0001m, 9000m, 0.0001m, 10m,
        SymbolFilters:
        [
            new("PRICE_FILTER", MinPrice: "0.01", MaxPrice: "1000000", TickSize: "0.01"),
            new("LOT_SIZE", MinQty: "0.0001", MaxQty: "9000", StepSize: "0.0001"),
            new("MIN_NOTIONAL", MinNotional: "10.00"),
            new("PERCENT_PRICE", MultiplierUp: "1.3", MultiplierDown: "0.7")
        ],
        BaseAsset: "BTC",
        QuoteAsset: "USDT",
        AllowedOrderTypes: ["LIMIT", "MARKET"]);

    private sealed class StubRules(BinanceSymbolTradingRules rules) : IBinanceSymbolTradingRulesProvider
    {
        public Task<BinanceSymbolTradingRules> GetAsync(string symbol, CancellationToken cancellationToken = default) => Task.FromResult(rules);
        public Task<BinanceSymbolTradingRules> RefreshAsync(string symbol, CancellationToken cancellationToken = default) => Task.FromResult(rules);
    }

    private sealed class StubAccountFilters : IBinanceAccountFiltersProvider
    {
        public Task<BinanceAccountFilters> GetAccountFiltersAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountFilters(
                [new BinanceExchangeFilter("EXCHANGE_MAX_NUM_ORDERS", 1000)], [], [], DateTimeOffset.UnixEpoch));
    }

    private sealed class StubTradingState : IBinanceAccountTradingStateProvider
    {
        public Task<BinanceAccountTradingState> GetTradingStateAsync(
            string symbol, string baseAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountTradingState(0, 0, 0m, DateTimeOffset.UnixEpoch, true));
    }

    private sealed class StubReferencePrice(decimal? price) : IBinanceReferencePriceProvider
    {
        public Task<BinanceReferencePriceEvidence?> GetReferencePriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(price is { } value
                ? new BinanceReferencePriceEvidence(symbol, value, DateTimeOffset.UtcNow)
                : null);
    }

    private sealed class CountingOrderClient : IBinanceSpotOrderClient
    {
        public int Submits { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            Submits++;
            return Task.FromResult(new BinanceSpotOrderResult(
                1, request.Symbol, request.ClientOrderId, "NEW", request.Price ?? 0m, request.Quantity, 0m, 0));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSpotCancelReplaceResult> CancelReplaceAsync(BinanceSpotCancelReplaceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSpotOrderListResult> SubmitOcoOrderListAsync(BinanceSpotOcoOrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSpotOrderListResult> SubmitOtocoOrderListAsync(BinanceSpotOtocoOrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
