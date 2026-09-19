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
        ]);
}
