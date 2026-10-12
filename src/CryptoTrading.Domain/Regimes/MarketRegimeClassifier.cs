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
    public bool FavorsTrendFollowing => Regime is MarketRegime.BullTrend or MarketRegime.BearTrend;
    public bool FavorsMeanReversion => Regime is MarketRegime.MeanRevertingRange;
    public bool ProhibitsNewExposure => Regime is MarketRegime.HighVolatilityChop or MarketRegime.IlliquidOrStale;
}

/// <summary>
/// Deterministic statistical market regime classifier.
/// Computes normalized linear trend slope and coefficient of variation to segment market conditions.
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
        decimal trendSlopeThreshold = DefaultTrendSlopeThreshold)
    {
        ArgumentNullException.ThrowIfNull(closePrices);
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
                "INSUFFICIENT_DATA: Requires at least 5 price observations.");
        }

        var n = closePrices.Count;
        decimal sum = 0m;
        for (var i = 0; i < n; i++)
        {
            if (closePrices[i] <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(closePrices), "Price must be strictly positive.");
            }
            sum += closePrices[i];
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

        if (volatilityRate > maxVolatilityThreshold)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.HighVolatilityChop,
                Confidence: 0.90m,
                trendSlopeRate,
                volatilityRate,
                $"CHOP: Volatility {volatilityRate:P2} exceeds threshold {maxVolatilityThreshold:P2}.");
        }

        if (trendSlopeRate > trendSlopeThreshold)
        {
            var confidence = Math.Min(1.0m, 0.70m + (trendSlopeRate / trendSlopeThreshold * 0.1m));
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.BullTrend,
                confidence,
                trendSlopeRate,
                volatilityRate,
                $"BULL: Slope {trendSlopeRate:P3} exceeds threshold {trendSlopeThreshold:P3}.");
        }

        if (trendSlopeRate < -trendSlopeThreshold)
        {
            var confidence = Math.Min(1.0m, 0.70m + (Math.Abs(trendSlopeRate) / trendSlopeThreshold * 0.1m));
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.BearTrend,
                confidence,
                trendSlopeRate,
                volatilityRate,
                $"BEAR: Slope {trendSlopeRate:P3} is below threshold -{trendSlopeThreshold:P3}.");
        }

        var rangeConfidence = n >= 20 && trendSlopeThreshold > 0m
            ? Math.Round(Math.Clamp(0.95m - (Math.Abs(trendSlopeRate) / trendSlopeThreshold * 0.35m), 0.55m, 0.95m), 4)
            : 0.85m;

        return new MarketRegimeSnapshot(
            symbol,
            observedAtUtc,
            MarketRegime.MeanRevertingRange,
            Confidence: rangeConfidence,
            trendSlopeRate,
            volatilityRate,
            $"RANGE: Slope {trendSlopeRate:P3} and volatility {volatilityRate:P2} within bounds.");
    }
}
