using System.Globalization;

namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Exact decimal rules needed to validate a Binance Spot limit order locally.
/// </summary>
public sealed record BinanceSymbolTradingRules(
    string Symbol,
    string Status,
    decimal MinimumPrice,
    decimal MaximumPrice,
    decimal PriceTickSize,
    decimal MinimumQuantity,
    decimal MaximumQuantity,
    decimal QuantityStepSize,
    decimal MinimumNotional)
{
    public BinanceOrderValidationResult ValidateLimitOrder(decimal price, decimal quantity)
    {
        var violations = new List<BinanceOrderFilterViolation>();

        if (!string.Equals(Status, "TRADING", StringComparison.Ordinal))
        {
            violations.Add(new("SYMBOL_NOT_TRADING", $"Symbol '{Symbol}' is not in TRADING status."));
        }

        if (price <= 0m)
        {
            violations.Add(new("PRICE_NON_POSITIVE", "Price must be greater than zero."));
        }
        else
        {
            AddRangeViolation(violations, "PRICE_BELOW_MINIMUM", price, MinimumPrice, value => value < MinimumPrice);
            AddRangeViolation(violations, "PRICE_ABOVE_MAXIMUM", price, MaximumPrice, value => MaximumPrice > 0m && value > MaximumPrice);
            AddStepViolation(violations, "PRICE_TICK_MISMATCH", price, PriceTickSize);
        }

        if (quantity <= 0m)
        {
            violations.Add(new("QUANTITY_NON_POSITIVE", "Quantity must be greater than zero."));
        }
        else
        {
            AddRangeViolation(violations, "QUANTITY_BELOW_MINIMUM", quantity, MinimumQuantity, value => value < MinimumQuantity);
            AddRangeViolation(violations, "QUANTITY_ABOVE_MAXIMUM", quantity, MaximumQuantity, value => MaximumQuantity > 0m && value > MaximumQuantity);
            AddStepViolation(violations, "QUANTITY_STEP_MISMATCH", quantity, QuantityStepSize);
        }

        if (price > 0m && quantity > 0m && price * quantity < MinimumNotional)
        {
            violations.Add(new(
                "NOTIONAL_BELOW_MINIMUM",
                $"Order notional {Format(price * quantity)} is below minimum {Format(MinimumNotional)}."));
        }

        return new(violations.Count is 0, violations);
    }

    private static void AddRangeViolation(
        List<BinanceOrderFilterViolation> violations,
        string code,
        decimal value,
        decimal limit,
        Func<decimal, bool> violates)
    {
        if (violates(value))
        {
            violations.Add(new(code, $"Value {Format(value)} violates limit {Format(limit)}."));
        }
    }

    private static void AddStepViolation(
        List<BinanceOrderFilterViolation> violations,
        string code,
        decimal value,
        decimal step)
    {
        if (step > 0m && value % step != 0m)
        {
            violations.Add(new(code, $"Value {Format(value)} is not aligned to step {Format(step)}."));
        }
    }

    private static string Format(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}

public sealed record BinanceOrderFilterViolation(string Code, string Message);

public sealed record BinanceOrderValidationResult(
    bool IsValid,
    IReadOnlyList<BinanceOrderFilterViolation> Violations);

public static class BinanceSymbolTradingRulesParser
{
    public static BinanceSymbolTradingRules Parse(BinanceSymbolInfo symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (string.IsNullOrWhiteSpace(symbol.Symbol))
        {
            throw new FormatException("Binance symbol metadata must contain a symbol.");
        }

        var filters = symbol.Filters ?? throw new FormatException($"Symbol '{symbol.Symbol}' did not contain filters.");
        var price = RequireSingle(filters, "PRICE_FILTER", symbol.Symbol);
        var lot = RequireSingle(filters, "LOT_SIZE", symbol.Symbol);
        var notional = RequireSingle(filters, "MIN_NOTIONAL", symbol.Symbol);

        return new(
            symbol.Symbol,
            symbol.Status,
            ParseNonNegative(price.MinPrice, "minPrice", symbol.Symbol),
            ParseNonNegative(price.MaxPrice, "maxPrice", symbol.Symbol),
            ParsePositive(price.TickSize, "tickSize", symbol.Symbol),
            ParsePositive(lot.MinQty, "minQty", symbol.Symbol),
            ParseNonNegative(lot.MaxQty, "maxQty", symbol.Symbol),
            ParsePositive(lot.StepSize, "stepSize", symbol.Symbol),
            ParsePositive(notional.MinNotional, "minNotional", symbol.Symbol));
    }

    private static BinanceSymbolFilter RequireSingle(
        IReadOnlyList<BinanceSymbolFilter> filters,
        string type,
        string symbol)
    {
        var matching = filters.Where(filter => string.Equals(filter.FilterType, type, StringComparison.Ordinal)).ToArray();
        return matching.Length switch
        {
            1 => matching[0],
            0 => throw new FormatException($"Symbol '{symbol}' is missing required {type} filter."),
            _ => throw new FormatException($"Symbol '{symbol}' contains duplicate {type} filters.")
        };
    }

    private static decimal ParsePositive(string? raw, string field, string symbol)
    {
        var value = ParseDecimal(raw, field, symbol);
        if (value <= 0m)
        {
            throw new FormatException($"Symbol '{symbol}' has a non-positive {field}.");
        }

        return value;
    }

    private static decimal ParseNonNegative(string? raw, string field, string symbol)
    {
        var value = ParseDecimal(raw, field, symbol);
        if (value < 0m)
        {
            throw new FormatException($"Symbol '{symbol}' has a negative {field}.");
        }

        return value;
    }

    private static decimal ParseDecimal(string? raw, string field, string symbol)
    {
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Symbol '{symbol}' has an invalid {field}.");
        }

        return value;
    }
}

public static class BinanceSymbolInfoExtensions
{
    public static BinanceSymbolTradingRules GetTradingRules(this BinanceSymbolInfo symbol) =>
        BinanceSymbolTradingRulesParser.Parse(symbol);
}
