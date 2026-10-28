using System.Text.Json;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceAccountFilterEvaluatorTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    private const string DocumentedMyFiltersResponse = """
        {
          "exchangeFilters": [
            { "filterType": "EXCHANGE_MAX_NUM_ORDERS", "maxNumOrders": 1000 }
          ],
          "symbolFilters": [
            {
              "filterType": "PRICE_FILTER",
              "minPrice": "0.00000100",
              "maxPrice": "100000.00000000",
              "tickSize": "0.00000100"
            }
          ],
          "assetFilters": [
            { "filterType": "MAX_ASSET", "limit": "100.00000000", "asset": "BNB" }
          ]
        }
        """;

    [Fact]
    public void ParsesDocumentedMyFiltersPayloadWithoutFloatingPoint()
    {
        var parsed = JsonSerializer.Deserialize<BinanceAccountFilters>(
            DocumentedMyFiltersResponse,
            CaseInsensitiveJson);
        var filters = parsed! with { ObservedAtUtc = DateTimeOffset.UnixEpoch };

        Assert.Single(filters.ExchangeFilters!);
        Assert.Equal(1000, filters.ExchangeFilters![0].MaxNumOrders);
        Assert.Equal("100.00000000", Assert.Single(filters.AssetFilters!).Limit);
        Assert.Equal("BNB", filters.AssetFilters![0].Asset);
        Assert.True(filters.HasOnlyKnownFilterTypes);
    }

    [Fact]
    public void ParsesRootExchangeFiltersFromExchangeInfo()
    {
        const string payload = """
            {
              "timezone": "UTC",
              "serverTime": 1565246363776,
              "exchangeFilters": [
                { "filterType": "EXCHANGE_MAX_NUM_ORDERS", "maxNumOrders": 1000 }
              ],
              "symbols": []
            }
            """;

        var info = JsonSerializer.Deserialize<BinanceExchangeInfo>(
            payload,
            CaseInsensitiveJson);

        Assert.Equal(1000, Assert.Single(info!.ExchangeFilters!).MaxNumOrders);
    }

    [Fact]
    public void MissingAccountEvidenceFailsClosed()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            null, "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);

        Assert.Contains(violations, item => item.Code == "ACCOUNT_FILTERS_UNAVAILABLE");
    }

    [Fact]
    public void OrderWithinLimitsProducesNoViolation()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000, asset: "BTC", limit: 1m),
            "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, openOrderCount: 999);

        Assert.Empty(violations);
    }

    [Fact]
    public void OpenOrderLimitIsEnforcedIncludingTheNewOrder()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, openOrderCount: 1000);

        Assert.Contains(violations, item => item.Code == "EXCHANGE_MAX_NUM_ORDERS_EXCEEDED");
    }

    [Fact]
    public void UnknownOpenOrderCountFailsClosed()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, openOrderCount: null);

        Assert.Contains(violations, item => item.Code == "ACCOUNT_ORDER_COUNT_UNAVAILABLE");
    }

    [Fact]
    public void BaseAssetLimitComparesQuantity()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "BTC", limit: 0.005m), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);

        Assert.Contains(violations, item => item.Code == "MAX_ASSET_EXCEEDED");
    }

    [Fact]
    public void QuoteAssetLimitComparesNotional()
    {
        var within = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "USDT", limit: 1m), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);
        var exceeded = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "USDT", limit: 0.5m), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);

        Assert.Empty(within);
        Assert.Contains(exceeded, item => item.Code == "MAX_ASSET_EXCEEDED");
    }

    [Fact]
    public void QuoteAssetLimitWithoutPriceOrForMarketOrderFailsClosed()
    {
        var missingPrice = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "USDT", limit: 10m), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, null, 0);
        var market = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "USDT", limit: 10m), "BTC", "USDT", BinanceSpotOrderType.Market, 0.01m, 100m, 0);

        Assert.Contains(missingPrice, item => item.Code == "MAX_ASSET_NOTIONAL_UNVERIFIABLE");
        Assert.Contains(market, item => item.Code == "MAX_ASSET_NOTIONAL_UNVERIFIABLE");
    }

    [Fact]
    public void UnrelatedAssetFilterAndUnknownTypesFailClosed()
    {
        var unrelated = BinanceAccountFilterEvaluator.Evaluate(
            Filters(asset: "ETH", limit: 10m), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);
        var unknown = BinanceAccountFilterEvaluator.Evaluate(
            new BinanceAccountFilters(
                [new BinanceExchangeFilter("FUTURE_EXCHANGE_FILTER")], null,
                [new BinanceAssetFilter("FUTURE_ASSET_FILTER", "BTC", "1")], DateTimeOffset.UnixEpoch),
            "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);

        Assert.Contains(unrelated, item => item.Code == "MAX_ASSET_ASSET_UNRELATED");
        Assert.Contains(unknown, item => item.Code == "ACCOUNT_FILTERS_UNSUPPORTED");
    }

    [Fact]
    public void IncompleteMaxAssetEvidenceFailsClosed()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            new BinanceAccountFilters(null, null, [new BinanceAssetFilter("MAX_ASSET", "BTC", null)], DateTimeOffset.UnixEpoch),
            "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 0);

        Assert.Contains(violations, item => item.Code == "ACCOUNT_FILTERS_UNSUPPORTED");
    }

    [Fact]
    public void MaxPositionBlocksBuyThatWouldOverflow()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.5m, 100m, 0,
            BinanceSpotOrderSide.Buy, basePosition: 9.8m, maxPosition: 10m);

        Assert.Contains(violations, item => item.Code == "MAX_POSITION_EXCEEDED");
    }

    [Fact]
    public void MaxPositionAllowsBuyWithinLimit()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.1m, 100m, 0,
            BinanceSpotOrderSide.Buy, basePosition: 9.5m, maxPosition: 10m);

        Assert.Empty(violations);
    }

    [Fact]
    public void MaxPositionDoesNotGateSellOrders()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.5m, 100m, 0,
            BinanceSpotOrderSide.Sell, basePosition: 20m, maxPosition: 10m);

        Assert.DoesNotContain(violations, item => item.Code is "MAX_POSITION_EXCEEDED" or "MAX_POSITION_UNAVAILABLE");
    }

    [Fact]
    public void MaxPositionWithoutPositionEvidenceFailsClosed()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.1m, 100m, 0,
            BinanceSpotOrderSide.Buy, basePosition: null, maxPosition: 10m);

        Assert.Contains(violations, item => item.Code == "MAX_POSITION_UNAVAILABLE");
    }

    [Fact]
    public void SymbolOrderLimitBlocksAtCapacity()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 5,
            symbolOpenOrderCount: 10, maxSymbolOrders: 10);

        Assert.Contains(violations, item => item.Code == "MAX_NUM_ORDERS_EXCEEDED");
    }

    [Fact]
    public void SymbolOrderLimitAllowsBelowCapacity()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 5,
            symbolOpenOrderCount: 9, maxSymbolOrders: 10);

        Assert.DoesNotContain(violations, item => item.Code == "MAX_NUM_ORDERS_EXCEEDED");
    }

    [Fact]
    public void SymbolOrderLimitWithoutCountFailsClosed()
    {
        var violations = BinanceAccountFilterEvaluator.Evaluate(
            Filters(maxOrders: 1000), "BTC", "USDT", BinanceSpotOrderType.Limit, 0.01m, 100m, 5,
            symbolOpenOrderCount: null, maxSymbolOrders: 10);

        Assert.Contains(violations, item => item.Code == "SYMBOL_ORDER_COUNT_UNAVAILABLE");
    }

    private static BinanceAccountFilters Filters(long maxOrders = 1000, string? asset = null, decimal? limit = null) =>
        new(
            [new BinanceExchangeFilter("EXCHANGE_MAX_NUM_ORDERS", maxOrders)],
            null,
            asset is null ? null : [new BinanceAssetFilter("MAX_ASSET", asset, limit?.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            DateTimeOffset.UnixEpoch);
}
