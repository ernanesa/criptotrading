using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Regimes;

/// <summary>Market regime classification for autonomous multi-strategy selection.</summary>
public enum MarketRegime
{
    BullTrend,
    BearTrend,
    MeanRevertingRange,
    HighVolatilityChop,
    IlliquidOrStale,
}

/// <summary>Immutable point-in-time classification of a market's underlying regime.</summary>
public sealed record MarketRegimeSnapshot(
    Symbol Symbol,
    DateTimeOffset ObservedAtUtc,
    MarketRegime Regime,
    decimal Confidence,
    decimal TrendSlopeRate,
    decimal VolatilityRate,
    string Reason)
{
    // VolatilityRate remains the legacy price CV for existing policies/models.
    // It must not be interpreted as the sigma of returns.
    public decimal PriceDispersionRate => VolatilityRate;
    public decimal ReturnSigmaPerBar { get; init; }
    public decimal EfficiencyRatio { get; init; }
    public bool HasCompleteMetrics { get; init; } = true;
    public bool LegacyDispersionLimitBreached { get; init; }
    public decimal? AppliedMaxReturnSigmaPerBar { get; init; }
    public bool FavorsTrendFollowing => !ProhibitsNewExposure && (Regime is MarketRegime.BullTrend or MarketRegime.BearTrend);
    public bool FavorsMeanReversion => !ProhibitsNewExposure && Regime is MarketRegime.MeanRevertingRange;
    public bool ProhibitsNewExposure => !HasCompleteMetrics || LegacyDispersionLimitBreached || Regime is MarketRegime.HighVolatilityChop or MarketRegime.IlliquidOrStale;
}

/// <summary>
/// Deterministic statistical market regime classifier.
/// Measures per-bar return sigma and trend efficiency independently of the
/// legacy price-dispersion exposure guard. No return limit is inferred from CV.
/// </summary>
public static class MarketRegimeClassifier
{
    public const decimal DefaultMaxVolatilityThreshold = 0.05m; // 5%
    public const decimal DefaultTrendSlopeThreshold = 0.003m;   // 0.3% per bar slope

    public static MarketRegimeSnapshot Classify(
        Symbol symbol,
        IReadOnlyList<decimal> closePrices,
        DateTimeOffset observedAtUtc,
        decimal maxVolatilityThreshold = DefaultMaxVolatilityThreshold,
        decimal trendSlopeThreshold = DefaultTrendSlopeThreshold,
        decimal? maxReturnSigmaPerBar = null)
    {
        ArgumentNullException.ThrowIfNull(closePrices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVolatilityThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trendSlopeThreshold);
        if (maxReturnSigmaPerBar is <= 0m)
            throw new ArgumentOutOfRangeException(nameof(maxReturnSigmaPerBar));
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Observation timestamp must be UTC.", nameof(observedAtUtc));
        }

        if (closePrices.Count < 5)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.IlliquidOrStale,
                Confidence: 0.0m,
                TrendSlopeRate: 0.0m,
                VolatilityRate: 0.0m,
                "INSUFFICIENT_DATA: Requires at least 5 price observations.") { HasCompleteMetrics = false };
        }

        var n = closePrices.Count;
        decimal sum = 0m;
        decimal returnMean = 0m, returnM2 = 0m, path = 0m;
        for (var i = 0; i < n; i++)
        {
            if (closePrices[i] <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(closePrices), "Price must be strictly positive.");
            }
            sum += closePrices[i];
            if (i > 0)
            {
                var change = closePrices[i] - closePrices[i - 1];
                var value = change / closePrices[i - 1];
                var delta = value - returnMean;
                returnMean += delta / i;
                returnM2 += delta * (value - returnMean);
                path += Math.Abs(change);
            }
        }

        var mean = sum / n;

        // Sample variance & std dev
        decimal varianceSum = 0m;
        for (var i = 0; i < n; i++)
        {
            var diff = closePrices[i] - mean;
            varianceSum += diff * diff;
        }

        var variance = varianceSum / (n - 1);
        var stdDev = (decimal)Math.Sqrt((double)variance);
        var volatilityRate = mean > 0m ? stdDev / mean : 0m;

        // Linear regression slope over indices 0 .. n-1
        decimal sumX = 0m;
        decimal sumY = 0m;
        decimal sumXY = 0m;
        decimal sumX2 = 0m;

        for (var i = 0; i < n; i++)
        {
            decimal x = i;
            var y = closePrices[i];
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        var denominator = (n * sumX2) - (sumX * sumX);
        decimal rawSlope = 0m;
        if (denominator != 0m)
        {
            rawSlope = ((n * sumXY) - (sumX * sumY)) / denominator;
        }

        var trendSlopeRate = mean > 0m ? rawSlope / mean : 0m;
        // The conversion is confined to an analytical statistic, never money.
        var returnSigma = (decimal)Math.Sqrt((double)Math.Max(0m, returnM2 / (n - 2)));
        var efficiency = path > 0m ? Math.Abs(closePrices[^1] - closePrices[0]) / path : 0m;
        var dispersionBreached = volatilityRate > maxVolatilityThreshold;
        MarketRegime regime;
        decimal confidence;
        string reason;
        if (maxReturnSigmaPerBar is { } returnLimit && returnSigma > returnLimit)
        {
            regime = MarketRegime.HighVolatilityChop;
            confidence = 0.90m;
            reason = $"CHOP: Return sigma per bar {returnSigma:P2} exceeds explicit limit {returnLimit:P2}.";
        }
        else if (trendSlopeRate > trendSlopeThreshold)
        {
            regime = MarketRegime.BullTrend;
            confidence = Math.Min(1.0m, 0.70m + (trendSlopeRate / trendSlopeThreshold * 0.1m));
            reason = $"BULL: Slope {trendSlopeRate:P3} exceeds threshold {trendSlopeThreshold:P3}.";
        }
        else if (trendSlopeRate < -trendSlopeThreshold)
        {
            regime = MarketRegime.BearTrend;
            confidence = Math.Min(1.0m, 0.70m + (Math.Abs(trendSlopeRate) / trendSlopeThreshold * 0.1m));
            reason = $"BEAR: Slope {trendSlopeRate:P3} is below threshold -{trendSlopeThreshold:P3}.";
        }
        else
        {
            regime = MarketRegime.MeanRevertingRange;
            confidence = n >= 20
                ? Math.Round(Math.Clamp(0.95m - (Math.Abs(trendSlopeRate) / trendSlopeThreshold * 0.35m), 0.55m, 0.95m), 4)
                : 0.85m;
            reason = $"RANGE: Slope {trendSlopeRate:P3}; return sigma per bar {returnSigma:P2}.";
        }

        if (dispersionBreached)
            reason += $" PRICE_DISPERSION_GUARD: Legacy price CV {volatilityRate:P2} exceeds {maxVolatilityThreshold:P2}.";

        return new MarketRegimeSnapshot(symbol, observedAtUtc, regime, confidence, trendSlopeRate, volatilityRate, reason)
        {
            ReturnSigmaPerBar = returnSigma,
            EfficiencyRatio = efficiency,
            LegacyDispersionLimitBreached = dispersionBreached,
            AppliedMaxReturnSigmaPerBar = maxReturnSigmaPerBar
        };
    }
}
