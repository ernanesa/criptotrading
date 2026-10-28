using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceSymbolTradingRulesTests
{
    [Fact]
    public void ParserReadsRequiredFiltersWithExactDecimals()
    {
        var rules = BinanceSymbolTradingRulesParser.Parse(CreateSymbol());

        Assert.Equal("BTCUSDT", rules.Symbol);
        Assert.Equal(0.01m, rules.PriceTickSize);
        Assert.Equal(0.0001m, rules.QuantityStepSize);
        Assert.Equal(10m, rules.MinimumNotional);
    }

    [Fact]
    public void ValidatorAcceptsPriceQuantityAlignedToExchangeRules()
    {
        var result = BinanceSymbolTradingRulesParser.Parse(CreateSymbol()).ValidateLimitOrder(100.01m, 0.1000m);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ValidatorReportsAllFilterViolationsWithoutRounding()
    {
        var result = BinanceSymbolTradingRulesParser.Parse(CreateSymbol()).ValidateLimitOrder(0.00999m, 0.00005m);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, violation => violation.Code == "PRICE_BELOW_MINIMUM");
        Assert.Contains(result.Violations, violation => violation.Code == "PRICE_TICK_MISMATCH");
        Assert.Contains(result.Violations, violation => violation.Code == "QUANTITY_BELOW_MINIMUM");
        Assert.Contains(result.Violations, violation => violation.Code == "QUANTITY_STEP_MISMATCH");
        Assert.Contains(result.Violations, violation => violation.Code == "NOTIONAL_BELOW_MINIMUM");
    }

    [Fact]
    public void ParserRejectsMissingOrDuplicateRequiredFilter()
    {
        var missing = CreateSymbol() with { Filters = CreateSymbol().Filters!.Where(filter => filter.FilterType != "MIN_NOTIONAL").ToArray() };
        var duplicate = CreateSymbol() with { Filters = [.. CreateSymbol().Filters!, CreateSymbol().Filters![0]] };

        Assert.Throws<FormatException>(() => missing.GetTradingRules());
        Assert.Throws<FormatException>(() => duplicate.GetTradingRules());
    }

    [Fact]
    public void ParserRejectsMalformedDecimal()
    {
        var malformed = CreateSymbol();

        var filters = malformed.Filters!.Select(filter =>
            filter.FilterType == "PRICE_FILTER" ? filter with { TickSize = "not-a-decimal" } : filter).ToArray();

        Assert.Throws<FormatException>(() => (malformed with { Filters = filters }).GetTradingRules());
    }

    [Fact]
    public void ParserSupportsModernNotionalAndPreservesUnknownFilters()
    {
        var symbol = CreateSymbol() with
        {
            Filters = [
                .. CreateSymbol().Filters!,
                new("NOTIONAL", MinNotional: "12", MaxNotional: "1000"),
                new("MAX_NUM_ORDERS", MaxQty: "200")]
        };

        var rules = symbol.GetTradingRules();

        Assert.Equal(12m, rules.MinimumNotional);
        Assert.Equal(1000m, rules.MaximumNotional);
        Assert.Contains(rules.SymbolFilters!, filter => filter.FilterType == "MAX_NUM_ORDERS");
        Assert.Contains(rules.ValidateLimitOrder(100m, 11m).Violations,
            violation => violation.Code == "NOTIONAL_ABOVE_MAXIMUM");
    }

    [Fact]
    public void ZeroTickSizeDisablesThePriceTickRule()
    {
        var symbol = CreateSymbol() with
        {
            Filters =
            [
                new("PRICE_FILTER", MinPrice: "0", MaxPrice: "0", TickSize: "0.00000000"),
                new("LOT_SIZE", MinQty: "0.0001", MaxQty: "9000", StepSize: "0.0001"),
                new("MIN_NOTIONAL", MinNotional: "10.00", ApplyToMarket: true, AveragePriceMinutes: 5)
            ]
        };

        var rules = BinanceSymbolTradingRulesParser.Parse(symbol);
        var result = rules.ValidateLimitOrder(100.007m, 0.1000m);

        Assert.Equal(0m, rules.PriceTickSize);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Violations, violation => violation.Code == "PRICE_TICK_MISMATCH");
    }

    [Fact]
    public void NonZeroTickSizeStillRejectsUnalignedPrice()
    {
        var result = BinanceSymbolTradingRulesParser.Parse(CreateSymbol()).ValidateLimitOrder(100.007m, 0.1000m);

        Assert.Contains(result.Violations, violation => violation.Code == "PRICE_TICK_MISMATCH");
    }

    private static BinanceSymbolInfo CreateSymbol() => new(
        "BTCUSDT",
        "TRADING",
        "BTC",
        "USDT",
        8,
        8,
        [
            new("PRICE_FILTER", MinPrice: "0.01", MaxPrice: "100000.00", TickSize: "0.01"),
            new("LOT_SIZE", MinQty: "0.0001", MaxQty: "9000", StepSize: "0.0001"),
            new("MIN_NOTIONAL", MinNotional: "10.00", ApplyToMarket: true, AveragePriceMinutes: 5)
        ],
        OrderTypes: ["LIMIT", "LIMIT_MAKER", "MARKET"]);
}
