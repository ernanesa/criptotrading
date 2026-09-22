using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;

namespace CryptoTrading.Domain.Research;

/// <summary>Market bar observation containing price, high, low, close and volume for regime ML feature pipeline.</summary>
public sealed record MarketBarFeature(
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>
/// ML Challenger regime classifier (US-171).
/// Uses multi-feature pipeline (momentum acceleration, volume flow, ATR percentiles)
/// to challenge baseline linear slope classifier.
/// </summary>
public static class RegimeChallengerClassifier
{
    public static MarketRegimeSnapshot Classify(
        Symbol symbol,
        IReadOnlyList<MarketBarFeature> bars,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Observation timestamp must be UTC.", nameof(observedAtUtc));
        }

        if (bars.Count < 10)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.IlliquidOrStale,
                Confidence: 0.0m,
                TrendSlopeRate: 0.0m,
                VolatilityRate: 0.0m,
                Reason: "CHALLENGER_INSUFFICIENT_BARS: Need at least 10 bars for ML challenger evaluation.");
        }

        // 1. Calculate Returns & Volatility
        var returns = new List<decimal>(bars.Count - 1);
        for (var i = 1; i < bars.Count; i++)
        {
            var prevClose = bars[i - 1].Close;
            if (prevClose > 0m)
            {
                returns.Add((bars[i].Close - prevClose) / prevClose);
            }
        }

        if (returns.Count == 0)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.IlliquidOrStale,
                0m, 0m, 0m, "CHALLENGER_NO_RETURNS");
        }

        var meanReturn = returns.Average();
        var variance = returns.Select(r => (r - meanReturn) * (r - meanReturn)).Average();
        var stdDev = (decimal)Math.Sqrt((double)variance);

        // Volatility threshold
        if (stdDev > 0.04m) // > 4% bar-to-bar volatility
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.HighVolatilityChop,
                Confidence: 0.90m,
                TrendSlopeRate: Math.Round(meanReturn, 4),
                VolatilityRate: Math.Round(stdDev, 4),
                Reason: "CHALLENGER_HIGH_VOLATILITY: Volatility breached 4% threshold.");
        }

        // 2. Volume Flow Imbalance (Chaikin / OBV-like proxy)
        decimal volumeImbalance = 0m;
        decimal totalVolume = 0m;
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var range = b.High - b.Low;
            var clv = range > 0m ? ((b.Close - b.Low) - (b.High - b.Close)) / range : 0m;
            volumeImbalance += clv * b.Volume;
            totalVolume += b.Volume;
        }

        var normalizedVolumeFlow = totalVolume > 0m ? volumeImbalance / totalVolume : 0m;

        // 3. Momentum trend (first half vs second half)
        var half = bars.Count / 2;
        var firstHalfAvg = bars.Take(half).Average(b => b.Close);
        var secondHalfAvg = bars.Skip(half).Average(b => b.Close);
        var trendSlope = firstHalfAvg > 0m ? (secondHalfAvg - firstHalfAvg) / firstHalfAvg : 0m;

        if (trendSlope > 0.005m && normalizedVolumeFlow > 0.05m)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.BullTrend,
                Confidence: 0.85m,
                TrendSlopeRate: Math.Round(trendSlope, 4),
                VolatilityRate: Math.Round(stdDev, 4),
                Reason: "CHALLENGER_BULL: Upward trend confirmed with positive volume flow.");
        }

        if (trendSlope < -0.005m && normalizedVolumeFlow < -0.05m)
        {
            return new MarketRegimeSnapshot(
                symbol,
                observedAtUtc,
                MarketRegime.BearTrend,
                Confidence: 0.85m,
                TrendSlopeRate: Math.Round(trendSlope, 4),
                VolatilityRate: Math.Round(stdDev, 4),
                Reason: "CHALLENGER_BEAR: Downward trend confirmed with negative volume flow.");
        }

        return new MarketRegimeSnapshot(
            symbol,
            observedAtUtc,
            MarketRegime.MeanRevertingRange,
            Confidence: 0.80m,
            TrendSlopeRate: Math.Round(trendSlope, 4),
            VolatilityRate: Math.Round(stdDev, 4),
            Reason: "CHALLENGER_RANGE: Balanced volume flow and bounded slope indicate mean-reverting range.");
    }
}
