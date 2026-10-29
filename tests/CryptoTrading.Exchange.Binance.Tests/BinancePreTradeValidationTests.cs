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

        var first = await cache.GetAsync("btcusdt", cancellationToken: TestContext.Current.CancellationToken);
        var second = await cache.GetAsync("BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task ControlledRefreshAlwaysRevalidatesPublicRules()
    {
        var market = new FakeMarketClient(TradingRules());
        using var cache = new BinanceSymbolTradingRulesCache(market, TimeSpan.FromMinutes(5), new FixedTimeProvider(Start));

        _ = await cache.GetAsync("BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);
        _ = await cache.RefreshAsync("BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task ExpiredRulesAreRefreshedAndRefreshFailureDoesNotReturnStaleRules()
    {
        var clock = new MutableTimeProvider(Start);
        var market = new FakeMarketClient(TradingRules());
        using var cache = new BinanceSymbolTradingRulesCache(market, TimeSpan.FromMinutes(5), clock);

        _ = await cache.GetAsync("BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(6));
        market.ThrowOnCall = true;

        var exception = await Assert.ThrowsAsync<BinanceTradingRulesUnavailableException>(() => cache.GetAsync("BTCUSDT", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("BTCUSDT", exception.Symbol);
        Assert.Equal(2, market.GetTradingRulesCalls);
    }

    [Fact]
    public async Task InvalidLimitOrderIsRejectedBeforeInnerClient()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var exception = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01005m, "ct-invalid", 100.005m), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(exception.Violations, violation => violation.Code == "PRICE_TICK_MISMATCH");
        Assert.Contains(exception.Violations, violation => violation.Code == "QUANTITY_STEP_MISMATCH");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task LimitOrderPassingSymbolRulesStillFailsClosedWithoutAccountEvidence()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-valid", 1_000.00m), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_EXCHANGE_FILTERS_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Theory]
    [InlineData(BinanceSpotOrderSide.Buy)]
    [InlineData(BinanceSpotOrderSide.Sell)]
    public async Task LimitOrderCannotSubmitWithoutAccountAndExchangeFilterEvidence(BinanceSpotOrderSide side)
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", side, BinanceSpotOrderType.Limit, 0.01m, "ct-no-account-filters", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_EXCHANGE_FILTERS_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task SubmissionUsesMandatoryRulesRefreshRatherThanCachedLookup()
    {
        var inner = new FakeOrderClient();
        var rules = new RefreshTrackingRulesProvider(TradingRules());
        var client = new BinancePreTradeValidatedOrderClient(inner, rules);

        await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-refresh", 1_000m), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, rules.GetCalls);
        Assert.Equal(1, rules.RefreshCalls);
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task MarketOrderIsRejectedBecauseNotionalCannotBeProvenLocally()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var exception = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Market, 0.01m, "ct-market"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(exception.Violations, violation => violation.Code == "MARKET_NOTIONAL_UNVERIFIABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Theory]
    [InlineData("FUTURE_UNKNOWN_FILTER")]
    public async Task LimitBuyRejectsApplicableOrUnknownFiltersWithoutContext(string filterType)
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with { SymbolFilters = [.. CoreFilters(), new BinanceSymbolFilter(filterType)] };
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(rules));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-unknown", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "UNSUPPORTED_APPLICABLE_FILTER");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Theory]
    [InlineData("PERCENT_PRICE")]
    [InlineData("PERCENT_PRICE_BY_SIDE")]
    public async Task PercentPriceFiltersFailClosedWithoutReferencePrice(string filterType)
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with { SymbolFilters = [.. CoreFilters(), new BinanceSymbolFilter(filterType)] };
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(rules));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-percent", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PERCENT_PRICE_REFERENCE_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task LimitSellMayIgnoreBuyOnlyMaxPositionButNotOtherFilters()
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with { SymbolFilters = [.. CoreFilters(), new BinanceSymbolFilter("MAX_POSITION"), new BinanceSymbolFilter("MARKET_LOT_SIZE")] };
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(rules));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Sell, BinanceSpotOrderType.Limit, 0.01m, "ct-sell", 1_000m), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(error.Violations, item => item.Code == "UNSUPPORTED_APPLICABLE_FILTER");
        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_EXCHANGE_FILTERS_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-type")]
    [InlineData("duplicate")]
    public async Task LimitRejectsMissingOrAmbiguousFilterMetadata(string scenario)
    {
        BinanceSymbolFilter[]? filters = scenario switch
        {
            "missing" => null,
            "empty-type" => [.. CoreFilters(), new BinanceSymbolFilter(string.Empty)],
            _ => [.. CoreFilters(), new BinanceSymbolFilter("PRICE_FILTER")]
        };
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules() with { SymbolFilters = filters }));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-metadata", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "FILTER_METADATA_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task LimitRejectsRulesForDifferentSymbol()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules() with { Symbol = "ETHUSDT" }));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-symbol", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "SYMBOL_RULES_MISMATCH");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public void OrderRequestRejectsUnknownSideAtConstruction()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BinanceSpotOrderRequest("BTCUSDT", (BinanceSpotOrderSide)999, BinanceSpotOrderType.Limit, 0.01m, "ct-side", 1_000m));
        Assert.Equal("side", error.ParamName);
    }

    [Fact]
    public void OrderRequestRejectsUnknownTypeAtConstruction()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, (BinanceSpotOrderType)999, 0.01m, "ct-type"));
        Assert.Equal("type", error.ParamName);
    }

    [Fact]
    public async Task ExtendedOrderMethodsAreNotDelegatedByPreTradeBoundary()
    {
        var inner = new FakeOrderClient();
        IBinanceSpotOrderClient boundary = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));
        var replacement = new BinanceSpotCancelReplaceRequest("BTCUSDT", BinanceSpotOrderSide.Sell, BinanceSpotOrderType.Limit, "original", "replacement", 0.01m, 1_000m);
        var oco = new BinanceSpotOcoOrderRequest("BTCUSDT", BinanceSpotOrderSide.Sell, 0.01m, "list", "take", 1_100m, "stop", 900m, 899m);
        var otoco = new BinanceSpotOtocoOrderRequest("BTCUSDT", "list-2", BinanceSpotOrderSide.Buy, 1_000m, 0.01m, "entry", BinanceSpotOrderSide.Sell, 0.01m, "take-2", 1_100m, "stop-2", 900m, 899m);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await boundary.CancelReplaceAsync(replacement, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await boundary.SubmitOcoOrderListAsync(oco, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await boundary.SubmitOtocoOrderListAsync(otoco, TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(0, inner.ExtendedSubmitCalls);
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
        10m,
        SymbolFilters: CoreFilters(),
        BaseAsset: "BTC",
        QuoteAsset: "USDT",
        AllowedOrderTypes: ["LIMIT", "LIMIT_MAKER", "MARKET"]);

    private static BinanceSymbolFilter[] CoreFilters() => [new("PRICE_FILTER"), new("LOT_SIZE"), new("MIN_NOTIONAL")];

    [Theory]
    [InlineData("MARKET")]
    [InlineData("LIMIT_MAKER")]
    public async Task RejectsOrderTypeNotAdvertisedBySymbol(string allowed)
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with { AllowedOrderTypes = [allowed] };
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(rules),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0), new FixedReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-type", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ORDER_TYPE_NOT_ALLOWED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task RejectsWhenSymbolOrderTypesAreMissing()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(TradingRules() with { AllowedOrderTypes = null }),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0), new FixedReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-type-missing", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ORDER_TYPE_METADATA_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    private sealed class FixedReferencePrice(decimal? price) : IBinanceReferencePriceProvider
    {
        public Task<BinanceReferencePriceEvidence?> GetReferencePriceAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(price is { } value
                ? new BinanceReferencePriceEvidence(symbol, value, DateTimeOffset.UtcNow)
                : null);
    }

    [Fact]
    public async Task NominalAccountEvidenceDoesNotAuthorizeSubmission()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000, asset: "BTC", limit: "1")),
            new FixedTradingState(5));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-evidence", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PRETRADE_EVIDENCE_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task SubmitIsBlockedWhenAccountEvidenceViolatesLimits()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000, asset: "BTC", limit: "0.001")),
            new FixedTradingState(0));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-limit", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "MAX_ASSET_EXCEEDED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Theory]
    [InlineData(BinanceSpotOrderSide.Buy)]
    [InlineData(BinanceSpotOrderSide.Sell)]
    public async Task SubmissionRemainsBlockedUntilAccountAndExchangeEvidenceIsVerified(BinanceSpotOrderSide side)
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)), new FixedTradingState(0));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", side, BinanceSpotOrderType.Limit, 0.01m, "ct-unverified", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PRETRADE_EVIDENCE_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task ContainmentStillDelegatesReadAndCancelWithoutSubmitting()
    {
        var inner = new FakeOrderClient();
        var boundary = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()));

        var observed = await boundary.QueryByClientOrderIdAsync("BTCUSDT", "ct-existing", TestContext.Current.CancellationToken);
        var cancelled = await boundary.CancelAsync("BTCUSDT", "ct-existing", TestContext.Current.CancellationToken);

        Assert.Equal("NEW", observed.Status);
        Assert.Equal("CANCELED", cancelled.Status);
        Assert.Equal(1, inner.QueryCalls);
        Assert.Equal(1, inner.CancelCalls);
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task SubmitIsBlockedWhenOrderCountCannotBeRead()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new ThrowingTradingState());

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-count", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_STATE_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task EmptyFiltersCannotHideUnavailableAccountState()
    {
        var inner = new FakeOrderClient();
        var empty = new BinanceAccountFilters([], [], [], DateTimeOffset.UnixEpoch);
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(empty), new ThrowingTradingState());

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-no-state", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_STATE_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task CancellationOfAccountStateIsPropagatedWithoutSubmission()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(new BinanceAccountFilters([], [], [], DateTimeOffset.UnixEpoch)), new CancelledTradingState());

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-cancelled-state", 1_000m),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task SubmitIsBlockedWhenSymbolAssetMetadataIsMissing()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner,
            new FixedRulesProvider(TradingRules() with { BaseAsset = "", QuoteAsset = "" }),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-assets", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "SYMBOL_ASSET_METADATA_UNAVAILABLE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task InconsistentAccountObservationIsRejected()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new InconsistentTradingState(), new FixedReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-inconsistent", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_STATE_INCONSISTENT");
        Assert.Equal(0, inner.SubmitCalls);
    }

    private sealed class InconsistentTradingState : IBinanceAccountTradingStateProvider
    {
        public Task<BinanceAccountTradingState> GetTradingStateAsync(
            string symbol, string baseAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountTradingState(0, 0, 0m, DateTimeOffset.UnixEpoch, true, IsConsistent: false));
    }

    [Fact]
    public async Task AccountUnableToTradeIsRejectedBeforeSubmission()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0, canTrade: false), new FixedReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-cantrade", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "ACCOUNT_CANNOT_TRADE");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task AccountAbleToTradeDoesNotBypassUnverifiedEvidence()
    {
        var inner = new FakeOrderClient();
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(TradingRules()),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0, canTrade: true), new FixedReferencePrice(100m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-cantrade-ok", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PRETRADE_EVIDENCE_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task MaxPositionIsEvaluatedAgainstAccountState()
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with
        {
            SymbolFilters = [.. CoreFilters(), new BinanceSymbolFilter("MAX_POSITION", MaxPosition: "10")]
        };
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(rules),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0, position: 9.99m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.02m, "ct-position", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "MAX_POSITION_EXCEEDED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    [Fact]
    public async Task MaxPositionWithinLimitDoesNotBypassUnverifiedEvidence()
    {
        var inner = new FakeOrderClient();
        var rules = TradingRules() with
        {
            SymbolFilters = [.. CoreFilters(), new BinanceSymbolFilter("MAX_POSITION", MaxPosition: "10")]
        };
        var client = new BinancePreTradeValidatedOrderClient(
            inner, new FixedRulesProvider(rules),
            new FixedAccountFiltersProvider(AccountFilters(maxOrders: 1000)),
            new FixedTradingState(0, position: 1m));

        var error = await Assert.ThrowsAsync<BinancePreTradeValidationException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-position-ok", 1_000m),
            TestContext.Current.CancellationToken));

        Assert.Contains(error.Violations, item => item.Code == "PRETRADE_EVIDENCE_UNVERIFIED");
        Assert.Equal(0, inner.SubmitCalls);
    }

    private static BinanceAccountFilters AccountFilters(long maxOrders, string? asset = null, string? limit = null) =>
        new(
            [new BinanceExchangeFilter("EXCHANGE_MAX_NUM_ORDERS", maxOrders)],
            [],
            asset is null ? [] : [new BinanceAssetFilter("MAX_ASSET", asset, limit)],
            DateTimeOffset.UnixEpoch);

    private sealed class FixedAccountFiltersProvider(BinanceAccountFilters filters) : IBinanceAccountFiltersProvider
    {
        public Task<BinanceAccountFilters> GetAccountFiltersAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(filters);
    }

    private sealed class FixedTradingState(long count, decimal position = 0m, bool canTrade = true) : IBinanceAccountTradingStateProvider
    {
        public Task<BinanceAccountTradingState> GetTradingStateAsync(
            string symbol, string baseAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountTradingState(count, count, position, DateTimeOffset.UnixEpoch, canTrade));
    }

    private sealed class ThrowingTradingState : IBinanceAccountTradingStateProvider
    {
        public Task<BinanceAccountTradingState> GetTradingStateAsync(
            string symbol, string baseAsset, CancellationToken cancellationToken = default) =>
            Task.FromException<BinanceAccountTradingState>(new HttpRequestException("offline-count-failure"));
    }

    private sealed class CancelledTradingState : IBinanceAccountTradingStateProvider
    {
        public Task<BinanceAccountTradingState> GetTradingStateAsync(
            string symbol, string baseAsset, CancellationToken cancellationToken = default) =>
            Task.FromException<BinanceAccountTradingState>(new OperationCanceledException());
    }

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
        public int ExtendedSubmitCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(1, request.Symbol, request.ClientOrderId, "NEW", request.Price ?? 0m, request.Quantity, 0m, 0));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(1, symbol, clientOrderId, "NEW", 100m, 1m, 0m, 0));
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(1, symbol, clientOrderId, "CANCELED", 100m, 1m, 0m, 0));
        }

        public Task<BinanceSpotCancelReplaceResult> CancelReplaceAsync(BinanceSpotCancelReplaceRequest request, CancellationToken cancellationToken = default)
        {
            ExtendedSubmitCalls++;
            return Task.FromException<BinanceSpotCancelReplaceResult>(new InvalidOperationException("Unexpected delegated call."));
        }

        public Task<BinanceSpotOrderListResult> SubmitOcoOrderListAsync(BinanceSpotOcoOrderRequest request, CancellationToken cancellationToken = default)
        {
            ExtendedSubmitCalls++;
            return Task.FromException<BinanceSpotOrderListResult>(new InvalidOperationException("Unexpected delegated call."));
        }

        public Task<BinanceSpotOrderListResult> SubmitOtocoOrderListAsync(BinanceSpotOtocoOrderRequest request, CancellationToken cancellationToken = default)
        {
            ExtendedSubmitCalls++;
            return Task.FromException<BinanceSpotOrderListResult>(new InvalidOperationException("Unexpected delegated call."));
        }
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
