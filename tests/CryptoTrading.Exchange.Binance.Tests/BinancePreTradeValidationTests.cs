using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinancePreTradeValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CacheReusesFreshRulesWithoutCallingPublicClientAgain()
    {
        var market = new FakeMarketClient(TradingRules());
        using var cache = new BinanceSymbolTradingRulesCache(market, TimeSpan.FromMinutes(5), new FixedTimeProvider(Start));

        var first = await cache.GetAsync("btcusdt");
        var second = await cache.GetAsync("BTCUSDT");

        Assert.Same(first, second);
        Assert.Equal(1, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task ControlledRefreshAlwaysRevalidatesPublicRules()
    {
        var market = new FakeMarketClient(TradingRules());
        using var cache = new BinanceSymbolTradingRulesCache(market, TimeSpan.FromMinutes(5), new FixedTimeProvider(Start));

        _ = await cache.GetAsync("BTCUSDT");
        _ = await cache.RefreshAsync("BTCUSDT");

        Assert.Equal(2, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task ExpiredRulesAreRefreshedAndRefreshFailureDoesNotReturnStaleRules()
    {
        var clock = new MutableTimeProvider(Start);
        var market = new FakeMarketClient(TradingRules());
        using var cache = new BinanceSymbolTradingRulesCache(market, TimeSpan.FromMinutes(5), clock);

        _ = await cache.GetAsync("BTCUSDT");
        clock.Advance(TimeSpan.FromMinutes(6));
        market.ThrowOnCall = true;

        var exception = await Assert.ThrowsAsync<BinanceTradingRulesUnavailableException>(() => cache.GetAsync("BTCUSDT"));

        Assert.Equal("BTCUSDT", exception.Symbol);
        Assert.Equal(2, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task InvalidLimitOrderIsRejectedBeforeInnerClient()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var exception = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01005m, "ct-invalid", 100.005m)));

        Assert.Contains(exception.Violations, violation => violation.Code == "PRICE_TICK_MISMATCH");
        Assert.Contains(exception.Violations, violation => violation.Code == "QUANTITY_STEP_MISMATCH");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task ValidLimitOrderIsSubmittedAfterRulesPass()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        _ = await client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-valid", 1_000.00m));

        Assert.Equal(1, inner.SubmitCalls);
    }

    [Fact]
    public async Task SubmissionUsesMandatoryRulesRefreshRatherThanCachedLookup()
    {
        var inner = new FakeOrderClient();
        var rules = new RefreshTrackingRulesProvider(TradingRules());
        var client = new BinancePreTradeValidatedOrderClient(inner, rules);

        _ = await client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-refresh", 1_000m));

        Assert.Equal(0, rules.GetCalls);
        Assert.Equal(1, rules.RefreshCalls);
        Assert.Equal(1, inner.SubmitCalls);
    }

    [Fact]
    public async Task MarketOrderIsRejectedBecauseNotionalCannotBeProvenLocally()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var exception = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Market, 0.01m, "ct-market")));

        Assert.Contains(exception.Violations, violation => violation.Code == "MARKET_NOTIONAL_UNVERIFIABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    private static BinanceSymbolTradingRules TradingRules() => new(
        "BTCUSDT",
        "TRADING",
        0.01m,
        1_000_000m,
        0.01m,
        0.0001m,
        9000m,
        0.0001m,
        10m);

    private sealed class FixedRulesProvider(BinanceSymbolTradingRules rules) : IBinanceSymbolTradingRulesProvider
    {
        public Task<BinanceSymbolTradingRules> GetAsync(string symbol, CancellationToken cancellationToken = default) => Task.FromResult(rules);

        public Task<BinanceSymbolTradingRules> RefreshAsync(string symbol, CancellationToken cancellationToken = default) => Task.FromResult(rules);
    }

    private sealed class RefreshTrackingRulesProvider(BinanceSymbolTradingRules rules) : IBinanceSymbolTradingRulesProvider
    {
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<BinanceSymbolTradingRules> GetAsync(string symbol, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(rules);
        }

        public Task<BinanceSymbolTradingRules> RefreshAsync(string symbol, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(rules);
        }
    }

    private sealed class FakeMarketClient(BinanceSymbolTradingRules rules) : IBinanceSpotMarketClient
    {
        public int GetTradingRulesCalls { get; private set; }

        public bool ThrowOnCall { get; set; }

        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default)
        {
            GetTradingRulesCalls++;
            if (ThrowOnCall)
            {
                throw new HttpRequestException("simulated public market-data outage");
            }

            return Task.FromResult(rules);
        }

        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceServerTime(0));

        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceSpotKline>>([]);

        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);

        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, DateTimeOffset.UtcNow));

        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceExchangeInfo("UTC", 0, []));
    }

    private sealed class FakeOrderClient : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(1, request.Symbol, request.ClientOrderId, "NEW", request.Price ?? 0m, request.Quantity, 0m, 0));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
