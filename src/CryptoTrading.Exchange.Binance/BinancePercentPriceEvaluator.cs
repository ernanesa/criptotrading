using System.Globalization;

namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Validates the percentage price band filters against a venue reference price.
/// Binance evaluates these against a non-null reference price, falling back to a
/// volume-weighted average when none exists; this adapter cannot compute that
/// fallback, so a missing reference price is a fail-closed violation rather than
/// an implicit pass.
/// See https://github.com/binance/binance-spot-api-docs/blob/master/filters.md#percent_price
/// </summary>
public static class BinancePercentPriceEvaluator
{
    public static IReadOnlyList<BinanceOrderFilterViolation> Evaluate(
        BinanceSymbolFilter filter,
        BinanceSpotOrderSide side,
        decimal price,
        decimal? referencePrice)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (referencePrice is not { } reference || reference <= 0m)
        {
            return [new("PERCENT_PRICE_REFERENCE_UNAVAILABLE",
                "A venue reference price is required to verify the percentage price band.")];
        }

        return filter.FilterType switch
        {
            "PERCENT_PRICE" => EvaluateSymmetric(filter, price, reference),
            "PERCENT_PRICE_BY_SIDE" => EvaluateBySide(filter, side, price, reference),
            _ => [new("UNSUPPORTED_APPLICABLE_FILTER", "Filter is not a percentage price band.")]
        };
    }

    private static List<BinanceOrderFilterViolation> EvaluateSymmetric(
        BinanceSymbolFilter filter, decimal price, decimal reference)
    {
        if (!TryParseMultiplier(filter.MultiplierUp, out var up) ||
            !TryParseMultiplier(filter.MultiplierDown, out var down))
        {
            return [new("FILTER_METADATA_UNAVAILABLE", "PERCENT_PRICE multipliers are missing or invalid.")];
        }

        var violations = new List<BinanceOrderFilterViolation>(2);
        if (price > reference * up)
        {
            violations.Add(new("PERCENT_PRICE_ABOVE_MAXIMUM",
                string.Create(CultureInfo.InvariantCulture, $"Price {price} exceeds {reference} * {up}.")));
        }

        if (price < reference * down)
        {
            violations.Add(new("PERCENT_PRICE_BELOW_MINIMUM",
                string.Create(CultureInfo.InvariantCulture, $"Price {price} is below {reference} * {down}.")));
        }

        return violations;
    }

    private static List<BinanceOrderFilterViolation> EvaluateBySide(
        BinanceSymbolFilter filter, BinanceSpotOrderSide side, decimal price, decimal reference)
    {
        var isBuy = side is BinanceSpotOrderSide.Buy;
        var rawUp = isBuy ? filter.BidMultiplierUp : filter.AskMultiplierUp;
        var rawDown = isBuy ? filter.BidMultiplierDown : filter.AskMultiplierDown;
        if (!TryParseMultiplier(rawUp, out var up) || !TryParseMultiplier(rawDown, out var down))
        {
            return [new("FILTER_METADATA_UNAVAILABLE", "PERCENT_PRICE_BY_SIDE multipliers are missing or invalid.")];
        }

        var violations = new List<BinanceOrderFilterViolation>(2);
        if (price > reference * up)
        {
            violations.Add(new("PERCENT_PRICE_ABOVE_MAXIMUM",
                string.Create(CultureInfo.InvariantCulture, $"Price {price} exceeds {reference} * {up}.")));
        }

        if (price < reference * down)
        {
            violations.Add(new("PERCENT_PRICE_BELOW_MINIMUM",
                string.Create(CultureInfo.InvariantCulture, $"Price {price} is below {reference} * {down}.")));
        }

        return violations;
    }

    private static bool TryParseMultiplier(string? raw, out decimal multiplier) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out multiplier) && multiplier > 0m;
}
