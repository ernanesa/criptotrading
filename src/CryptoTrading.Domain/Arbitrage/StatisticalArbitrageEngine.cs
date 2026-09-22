using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Arbitrage;

/// <summary>Signal emitted by statistical arbitrage pairs trading model.</summary>
public enum PairSignal
{
    Neutral,
    LongSpread,  // Buy Asset A, Short Asset B
    ShortSpread, // Short Asset A, Buy Asset B
    ClosePosition,
    StopLossExit,
}

/// <summary>Calculated point-in-time state of a cointegrated asset pair spread (US-150).</summary>
public sealed record PairSpreadSnapshot(
    Symbol AssetA,
    Symbol AssetB,
    decimal PriceA,
    decimal PriceB,
    decimal HedgeRatioBeta,
    decimal SpreadValue,
    decimal SpreadMean,
    decimal SpreadStdDev,
    decimal ZScore,
    PairSignal Signal,
    string Reason);

/// <summary>
/// Statistical arbitrage and pairs trading engine (US-150).
/// Computes cointegration spread and Z-score signals between correlated crypto assets.
/// </summary>
public static class StatisticalArbitrageEngine
{
    public const decimal DefaultEntryZScoreThreshold = 2.0m;
    public const decimal DefaultExitZScoreThreshold = 0.5m;
    public const decimal DefaultStopLossZScoreThreshold = 3.5m;

    public static PairSpreadSnapshot EvaluatePairSpread(
        Symbol assetA,
        Symbol assetB,
        decimal priceA,
        decimal priceB,
        decimal hedgeRatioBeta,
        IReadOnlyList<decimal> historicalSpreads,
        decimal entryZThreshold = DefaultEntryZScoreThreshold,
        decimal exitZThreshold = DefaultExitZScoreThreshold,
        decimal stopLossZThreshold = DefaultStopLossZScoreThreshold)
    {
        ArgumentNullException.ThrowIfNull(historicalSpreads);
        if (priceA <= 0m || priceB <= 0m || hedgeRatioBeta <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceA), "Prices and hedge ratio must be positive.");
        }

        // Current spread: ln(P_A) - beta * ln(P_B)
        var currentSpread = (decimal)Math.Log((double)priceA) - (hedgeRatioBeta * (decimal)Math.Log((double)priceB));

        if (historicalSpreads.Count < 10)
        {
            return new PairSpreadSnapshot(
                assetA,
                assetB,
                priceA,
                priceB,
                hedgeRatioBeta,
                Math.Round(currentSpread, 6),
                SpreadMean: 0m,
                SpreadStdDev: 0m,
                ZScore: 0m,
                PairSignal.Neutral,
                Reason: "INSUFFICIENT_HISTORY: Need at least 10 spread observations.");
        }

        var mean = historicalSpreads.Average();
        var variance = historicalSpreads.Select(s => (s - mean) * (s - mean)).Average();
        var stdDev = (decimal)Math.Sqrt((double)variance);

        if (stdDev <= 0.000001m)
        {
            return new PairSpreadSnapshot(
                assetA,
                assetB,
                priceA,
                priceB,
                hedgeRatioBeta,
                Math.Round(currentSpread, 6),
                mean,
                stdDev,
                ZScore: 0m,
                PairSignal.Neutral,
                Reason: "ZERO_VARIANCE: Historical spread variance is negligible.");
        }

        var zScore = (currentSpread - mean) / stdDev;

        PairSignal signal;
        string reason;

        // Extreme divergence -> Stop Loss Exit
        if (Math.Abs(zScore) >= stopLossZThreshold)
        {
            signal = PairSignal.StopLossExit;
            reason = $"STOP_LOSS: Z-score {zScore:F2} breached risk threshold {stopLossZThreshold:F2}. Divergence failure protection.";
        }
        // Mean reversion entry opportunities
        else if (zScore <= -entryZThreshold)
        {
            signal = PairSignal.LongSpread;
            reason = $"LONG_SPREAD: Spread is underpriced (Z={zScore:F2} <= -{entryZThreshold:F2}). Buy {assetA.Value}, Short {assetB.Value}.";
        }
        else if (zScore >= entryZThreshold)
        {
            signal = PairSignal.ShortSpread;
            reason = $"SHORT_SPREAD: Spread is overpriced (Z={zScore:F2} >= +{entryZThreshold:F2}). Short {assetA.Value}, Buy {assetB.Value}.";
        }
        // Position reversion to mean -> Close
        else if (Math.Abs(zScore) <= exitZThreshold)
        {
            signal = PairSignal.ClosePosition;
            reason = $"REVERSION_COMPLETE: Spread reverted within threshold |Z| <= {exitZThreshold:F2}. Realize arbitrage profit.";
        }
        else
        {
            signal = PairSignal.Neutral;
            reason = $"NEUTRAL: Z-score {zScore:F2} in holding band.";
        }

        return new PairSpreadSnapshot(
            assetA,
            assetB,
            priceA,
            priceB,
            hedgeRatioBeta,
            Math.Round(currentSpread, 6),
            Math.Round(mean, 6),
            Math.Round(stdDev, 6),
            Math.Round(zScore, 4),
            signal,
            reason);
    }
}
