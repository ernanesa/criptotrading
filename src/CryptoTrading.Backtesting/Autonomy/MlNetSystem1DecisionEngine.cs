using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>
/// Inference from a previously trained, calibrated and integrity-checked artifact.
/// Neutral legacy SSA fields mean unavailable evidence, not measured p-values.
/// </summary>
public readonly record struct MlNetSystem1Evaluation(
    decimal MlWinProbability,
    decimal MlRawScore,
    decimal SsaChangePointPValue,
    decimal SsaMartingaleScore,
    decimal SsaSpikePValue,
    bool IsSsaStructuralBreakdown)
{
    public bool HasCalibratedProbability { get; init; }
    public bool HasSsaEvidence { get; init; }
    public bool IsNegativePriceShock { get; init; }
    public string? ModelId { get; init; }
    public string? ModelArtifactSha256 { get; init; }
    public OrderSide? ModelTargetSide { get; init; }
    public string? AvailabilityReason { get; init; }
}

/// <summary>
/// Walk-forward out-of-sample probability calibration report featuring Brier Score and Expected Calibration Error (ECE).
/// </summary>
public sealed record MlProbabilityCalibrationReport(
    decimal BrierScore,
    decimal ExpectedCalibrationError,
    bool BrierScoreThresholdMet,
    decimal TargetBrierThreshold,
    int TotalEvaluations,
    int PositiveOutcomes,
    IReadOnlyList<MlProbabilityCalibrationBin> ReliabilityBins,
    DateTimeOffset EvaluatedAtUtc)
{
    public string? EvidenceUnavailableReason { get; init; }
}

/// <summary>
/// Individual confidence/reliability bin for probability calibration curve analysis.
/// </summary>
public sealed record MlProbabilityCalibrationBin(
    int BinIndex,
    decimal LowerBound,
    decimal UpperBound,
    int SampleCount,
    decimal MeanConfidence,
    decimal EmpiricalWinRate,
    decimal AbsoluteError);

/// <summary>
/// Out-of-sample walk-forward promotion governance report combining Brier Score calibration,
/// out-of-sample Sharpe Ratio, and Bailey &amp; López de Prado (2014) Deflated Sharpe Ratio (DSR).
/// </summary>
public sealed record MlWalkForwardPromotionReport(
    int SampleCount,
    decimal BrierScore,
    decimal ExpectedCalibrationError,
    decimal OutOfSampleSharpeRatio,
    decimal DeflatedSharpeRatio,
    bool PromotionAuthorized,
    string? RejectionReason);

public static class MlNetSystem1DecisionEngine
{
    public static MlNetSystem1Evaluation Evaluate(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        OrderSide? candidateSide,
        decimal? orderBookImbalance,
        StrategyRegimePerformance? empiricalPerformance = null,
        int horizonBars = 4,
        MlDecisionArtifact? artifact = null,
        string timeframe = "1m")
    {
        if (horizonBars is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonBars));
        }
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(regime);

        if (bars.Count < 20) return Unavailable("ML_INSUFFICIENT_HISTORY");
        var last = bars[^1];
        if (last.CloseTimeUtc > regime.ObservedAtUtc) return Unavailable("ML_FEATURES_FROM_FUTURE");
        for (var i = 0; i < bars.Count; i++)
            if (bars[i].Symbol != last.Symbol || bars[i].CloseTimeUtc.Offset != TimeSpan.Zero ||
                i > 0 && bars[i].CloseTimeUtc <= bars[i - 1].CloseTimeUtc)
                return Unavailable("ML_BAR_SEQUENCE_INVALID");
        var oneBarReturn = (last.ClosePrice.Value - bars[^2].ClosePrice.Value) / bars[^2].ClosePrice.Value;
        var threeBarReturn = (last.ClosePrice.Value - bars[^4].ClosePrice.Value) / bars[^4].ClosePrice.Value;
        // Preserve the existing negative-shock entry veto conservatively, without
        // presenting an unavailable IID/SSA estimate as measured evidence.
        var negativeShock = oneBarReturn <= -0.00055m || threeBarReturn <= -0.00075m;
        if (artifact is null) return Unavailable("ML_CALIBRATED_ARTIFACT_UNAVAILABLE", negativeShock);
        if (regime.Symbol != last.Symbol) return Unavailable("ML_REGIME_SYMBOL_MISMATCH", negativeShock);
        var features = CreateFeatureVector(bars, bars.Count - 1, orderBookImbalance);
        var context = new MlInferenceContext(last.Symbol.Value, timeframe, candidateSide ?? OrderSide.Buy,
            horizonBars, last.CloseTimeUtc, regime.ObservedAtUtc, features);
        if (!artifact.TryPredict(context, out var prediction))
            return Unavailable(prediction.RejectionReason ?? "ML_PREDICTION_UNAVAILABLE", negativeShock) with
            { ModelId = prediction.ModelId, ModelArtifactSha256 = artifact.VerifiedSha256, ModelTargetSide = artifact.Metadata.Policy.Side };
        return new MlNetSystem1Evaluation(prediction.Probability!.Value, prediction.RawScore!.Value, 0.50m, 0m, 0.50m, false)
        {
            HasCalibratedProbability = true,
            ModelId = prediction.ModelId,
            ModelArtifactSha256 = artifact.VerifiedSha256,
            ModelTargetSide = artifact.Metadata.Policy.Side,
            IsNegativePriceShock = negativeShock
        };
    }

    private static MlNetSystem1Evaluation Unavailable(string reason, bool negativeShock = false) =>
        new(0m, 0m, 0.50m, 0m, 0.50m, false) { AvailabilityReason = reason, IsNegativePriceShock = negativeShock };

    public static int ComputeHorizonBars(
        decimal targetMoveBps,
        decimal volatilityBpsPerBar,
        int minHorizon = 4,
        int maxHorizon = 32)
    {
        if (minHorizon < 1 || maxHorizon < minHorizon)
        {
            throw new ArgumentOutOfRangeException(nameof(minHorizon));
        }

        if (targetMoveBps <= 0m || volatilityBpsPerBar <= 0m)
        {
            return maxHorizon;
        }

        var bars = (int)Math.Ceiling(targetMoveBps / volatilityBpsPerBar);
        return Math.Clamp(bars, minHorizon, maxHorizon);
    }

    public static bool EvaluateChronologicalTripleBarrier(
        IReadOnlyList<CloseBar> bars,
        int entryIndex,
        int horizon,
        OrderSide side,
        decimal takeProfitBps,
        decimal stopLossBps)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var entryPrice = bars[entryIndex].ClosePrice.Value;
        if (entryPrice <= 0m)
        {
            return false;
        }

        var maxStep = Math.Min(horizon, bars.Count - 1 - entryIndex);
        for (var h = 1; h <= maxStep; h++)
        {
            var fwdBar = bars[entryIndex + h];
            var high = fwdBar.HighPrice.Value;
            var low = fwdBar.LowPrice.Value;

            decimal favorableBps;
            decimal adverseBps;
            if (side == OrderSide.Buy)
            {
                favorableBps = (high - entryPrice) / entryPrice * 10_000m;
                adverseBps = (low - entryPrice) / entryPrice * 10_000m;
            }
            else
            {
                favorableBps = (entryPrice - low) / entryPrice * 10_000m;
                adverseBps = (entryPrice - high) / entryPrice * 10_000m;
            }

            // Conservative chronological ordering: if stop-loss is touched on this bar
            // (including same-bar TP+SL ambiguity), resolve immediately as a stop-out.
            if (adverseBps <= -stopLossBps)
            {
                return false;
            }

            if (favorableBps >= takeProfitBps)
            {
                return true;
            }
        }

        return false;
    }

    internal static MlFeatureVector CreateFeatureVector(IReadOnlyList<CloseBar> bars, int index, decimal? orderBookImbalance)
    {
        var (zScore, slope, momentum, dispersion) = ComputePointInTimeFeatures(bars, index, 8);
        return new(Math.Clamp(zScore, -4m, 4m), Math.Clamp(slope, -15m, 15m),
            Math.Clamp(momentum, -30m, 30m), Math.Clamp(dispersion, 0.5m, 30m), orderBookImbalance);
    }

    internal static (decimal ZScore, decimal SlopeBps, decimal Mom3Bps, decimal VolBps) ComputePointInTimeFeatures(
        IReadOnlyList<CloseBar> bars,
        int index,
        int lookback)
    {
        var windowCount = lookback + 1;
        var sum = 0m;
        for (var j = index - lookback; j <= index; j++)
        {
            sum += bars[j].ClosePrice.Value;
        }

        var mean = sum / windowCount;
        var sqDiff = 0m;
        for (var j = index - lookback; j <= index; j++)
        {
            var diff = bars[j].ClosePrice.Value - mean;
            sqDiff += diff * diff;
        }

        var std = lookback > 0 ? (decimal)Math.Sqrt((double)(sqDiff / lookback)) : 0m;
        var currentBar = bars[index];
        var zScore = std > 0m ? (currentBar.ClosePrice.Value - mean) / std : 0m;
        var slopeBps = bars[index - lookback].ClosePrice.Value > 0m
            ? (currentBar.ClosePrice.Value - bars[index - lookback].ClosePrice.Value) / bars[index - lookback].ClosePrice.Value * 10_000m / lookback
            : 0m;
        var mom3Idx = Math.Max(0, index - 3);
        var mom3Bps = bars[mom3Idx].ClosePrice.Value > 0m
            ? (currentBar.ClosePrice.Value - bars[mom3Idx].ClosePrice.Value) / bars[mom3Idx].ClosePrice.Value * 10_000m
            : 0m;
        var volBps = mean > 0m ? std / mean * 10_000m : 0m;
        return (zScore, slopeBps, mom3Bps, volBps);
    }

    /// <summary>
    /// Computes the Brier score for probabilistic calibration verification:
    /// Brier = (1 / N) * sum((p_t - y_t)^2).
    /// </summary>
    public static decimal CalculateBrierScore(IReadOnlyList<(decimal PredictedProb, bool ActualOutcome)> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        if (predictions.Count == 0)
        {
            return 0m;
        }

        var sumSqErr = 0m;
        for (var i = 0; i < predictions.Count; i++)
        {
            var p = Math.Clamp(predictions[i].PredictedProb, 0m, 1m);
            var y = predictions[i].ActualOutcome ? 1m : 0m;
            var diff = p - y;
            sumSqErr += diff * diff;
        }

        return Math.Round(sumSqErr / predictions.Count, 4);
    }

    /// <summary>
    /// Evaluates probabilistic calibration metrics across discrete confidence bins,
    /// calculating Brier Score, Expected Calibration Error (ECE), and empirical reliability bins.
    /// </summary>
    public static MlProbabilityCalibrationReport CalculateCalibrationReport(
        IReadOnlyList<(decimal PredictedProb, bool ActualOutcome)> predictions,
        int binCount = 5,
        decimal targetBrierThreshold = 0.15m)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        if (binCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(binCount), "Bin count must be positive.");
        }

        if (predictions.Count == 0)
        {
            return new MlProbabilityCalibrationReport(
                BrierScore: 0m,
                ExpectedCalibrationError: 0m,
                BrierScoreThresholdMet: false,
                TargetBrierThreshold: targetBrierThreshold,
                TotalEvaluations: 0,
                PositiveOutcomes: 0,
                ReliabilityBins: [],
                EvaluatedAtUtc: DateTimeOffset.UtcNow);
        }

        var brierScore = CalculateBrierScore(predictions);
        var binStep = 1.0m / binCount;
        var bins = new List<MlProbabilityCalibrationBin>(binCount);
        var weightedEceSum = 0m;
        var positiveOutcomes = 0;

        for (var b = 0; b < binCount; b++)
        {
            var lower = b * binStep;
            var upper = (b == binCount - 1) ? 1.0001m : (b + 1) * binStep;

            var binSamples = predictions
                .Where(p => p.PredictedProb >= lower && p.PredictedProb < upper)
                .ToArray();

            var nominalUpper = Math.Min(1.0m, (b + 1) * binStep);
            if (binSamples.Length == 0)
            {
                bins.Add(new MlProbabilityCalibrationBin(
                    BinIndex: b + 1,
                    LowerBound: lower,
                    UpperBound: nominalUpper,
                    SampleCount: 0,
                    MeanConfidence: (lower + nominalUpper) / 2m,
                    EmpiricalWinRate: 0m,
                    AbsoluteError: 0m));
                continue;
            }

            var meanConf = binSamples.Average(s => s.PredictedProb);
            var wins = binSamples.Count(s => s.ActualOutcome);
            var winRate = (decimal)wins / binSamples.Length;
            var absErr = Math.Abs(meanConf - winRate);

            weightedEceSum += (decimal)binSamples.Length / predictions.Count * absErr;
            bins.Add(new MlProbabilityCalibrationBin(
                BinIndex: b + 1,
                LowerBound: lower,
                UpperBound: nominalUpper,
                SampleCount: binSamples.Length,
                MeanConfidence: Math.Round(meanConf, 4),
                EmpiricalWinRate: Math.Round(winRate, 4),
                AbsoluteError: Math.Round(absErr, 4)));
        }

        for (var i = 0; i < predictions.Count; i++)
        {
            if (predictions[i].ActualOutcome)
            {
                positiveOutcomes++;
            }
        }

        var ece = Math.Round(weightedEceSum, 4);
        var thresholdMet = brierScore <= targetBrierThreshold;

        return new MlProbabilityCalibrationReport(
            BrierScore: brierScore,
            ExpectedCalibrationError: ece,
            BrierScoreThresholdMet: thresholdMet,
            TargetBrierThreshold: targetBrierThreshold,
            TotalEvaluations: predictions.Count,
            PositiveOutcomes: positiveOutcomes,
            ReliabilityBins: bins,
            EvaluatedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Executes walk-forward point-in-time System-1 probability calibration against out-of-sample klines
    /// without lookahead leakage.
    /// </summary>
    public static MlProbabilityCalibrationReport EvaluateWalkForwardCalibration(
        IReadOnlyList<CloseBar> bars,
        IReadOnlyList<WalkForwardWindow> windows,
        decimal orderBookImbalance = 0m,
        decimal targetBrierThreshold = 0.15m,
        MlDecisionArtifact? artifact = null,
        string timeframe = "1m")
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(windows);
        if (artifact?.Metadata.Policy.UsesOrderBook is true)
            return CalculateCalibrationReport([], targetBrierThreshold: targetBrierThreshold) with
            { EvidenceUnavailableReason = "ML_HISTORICAL_BOOK_SNAPSHOTS_REQUIRED" };
        if (artifact is null)
            return CalculateCalibrationReport([], targetBrierThreshold: targetBrierThreshold) with
            { EvidenceUnavailableReason = "ML_CALIBRATED_ARTIFACT_UNAVAILABLE" };
        if (bars.Count < 20 || windows.Count == 0)
        {
            return new MlProbabilityCalibrationReport(
                BrierScore: 0m,
                ExpectedCalibrationError: 0m,
                BrierScoreThresholdMet: false,
                TargetBrierThreshold: targetBrierThreshold,
                TotalEvaluations: 0,
                PositiveOutcomes: 0,
                ReliabilityBins: [],
                EvaluatedAtUtc: DateTimeOffset.UtcNow);
        }

        var horizon = artifact.Metadata.Policy.HorizonBars;
        var samples = new List<(decimal PredictedProb, bool ActualOutcome)>();

        foreach (var window in windows)
        {
            var oosStart = window.OutOfSampleStartIndex;
            var oosEnd = window.OutOfSampleStartIndex + window.OutOfSampleLength;

            for (var t = oosStart; t < oosEnd && t + horizon < bars.Count; t++)
            {
                // Point-in-time slice up to bar t (strictly no lookahead)
                var pointInTimeBars = bars.Take(t + 1).ToArray();
                if (pointInTimeBars.Length < 20)
                {
                    continue;
                }

                var closePrices = pointInTimeBars.Select(b => b.ClosePrice.Value).ToArray();
                var regime = MarketRegimeClassifier.Classify(
                    pointInTimeBars[0].Symbol,
                    closePrices,
                    pointInTimeBars[^1].CloseTimeUtc);

                var eval = Evaluate(pointInTimeBars, regime, OrderSide.Buy, orderBookImbalance,
                    horizonBars: horizon, artifact: artifact, timeframe: timeframe);
                if (!eval.HasCalibratedProbability) continue;

                var volBps = regime.VolatilityRate * 10_000m;
                var minProfitableMoveBps = Math.Max(180.0m, volBps * 3.0m);
                var maxAllowedStopBps = Math.Max(100.0m, volBps * 1.8m);
                var actualOutcome = EvaluateChronologicalTripleBarrier(
                    bars,
                    t,
                    horizon,
                    OrderSide.Buy,
                    minProfitableMoveBps,
                    maxAllowedStopBps);
                samples.Add((eval.MlWinProbability, actualOutcome));
            }
        }

        return CalculateCalibrationReport(samples, binCount: 5, targetBrierThreshold: targetBrierThreshold);
    }

    /// <summary>
    /// Computes the Bailey &amp; López de Prado (2014) Deflated Sharpe Ratio (DSR) probability in [0, 1],
    /// adjusting an observed Sharpe Ratio for sample length, non-normal skewness/kurtosis, and multiple testing trials.
    /// </summary>
    public static decimal CalculateDeflatedSharpeRatio(
        decimal observedSharpeRatio,
        int sampleCount,
        int independentTrials = 6,
        decimal skewness = 0m,
        decimal kurtosis = 3m,
        decimal trialSharpeStdDev = 0.25m)
    {
        if (sampleCount < 2)
        {
            return 0m;
        }

        var k = Math.Max(1, independentTrials);
        double sr0 = 0.0;
        if (k > 1 && trialSharpeStdDev > 0m)
        {
            const double eulerMascheroni = 0.5772156649015329;
            var z1 = InverseStandardNormalCdf(1.0 - (1.0 / k));
            var z2 = InverseStandardNormalCdf(1.0 - (1.0 / (k * Math.E)));
            sr0 = (double)trialSharpeStdDev * (((1.0 - eulerMascheroni) * z1) + (eulerMascheroni * z2));
        }

        var sr = (double)observedSharpeRatio;
        var skew = (double)skewness;
        var kurt = Math.Max(1.0, (double)kurtosis);
        var varianceAdjustment = 1.0 - (skew * sr) + (((kurt - 1.0) / 4.0) * sr * sr);
        var denom = Math.Sqrt(Math.Max(1e-9, varianceAdjustment));
        var zScore = (sr - sr0) * Math.Sqrt(sampleCount - 1) / denom;
        var dsr = StandardNormalCdf(zScore);
        return Math.Clamp(Math.Round((decimal)dsr, 4), 0m, 1m);
    }

    /// <summary>
    /// Evaluates whether a candidate walk-forward ML model satisfies institutional promotion governance:
    /// Brier Score calibration threshold, positive out-of-sample Sharpe Ratio, and Deflated Sharpe Ratio (DSR) gate.
    /// </summary>
    public static MlWalkForwardPromotionReport EvaluateWalkForwardModelPromotion(
        IReadOnlyList<(decimal PredictedWinProbability, bool RealizedOutcome, decimal RealizedNetReturnBps)> outOfSampleEvaluations,
        int independentTrials = 6,
        decimal maxBrierScore = 0.25m,
        decimal minDeflatedSharpeRatio = 0.65m,
        int minSampleCount = 10)
    {
        ArgumentNullException.ThrowIfNull(outOfSampleEvaluations);
        if (outOfSampleEvaluations.Count == 0)
        {
            return new MlWalkForwardPromotionReport(
                SampleCount: 0,
                BrierScore: 0.25m,
                ExpectedCalibrationError: 0m,
                OutOfSampleSharpeRatio: 0m,
                DeflatedSharpeRatio: 0m,
                PromotionAuthorized: false,
                RejectionReason: "INSUFFICIENT_OUT_OF_SAMPLE_EVALUATIONS");
        }

        var calibrationSamples = outOfSampleEvaluations
            .Select(static x => (x.PredictedWinProbability, x.RealizedOutcome))
            .ToArray();
        var calibration = CalculateCalibrationReport(calibrationSamples, binCount: 5, targetBrierThreshold: maxBrierScore);

        var activeReturns = outOfSampleEvaluations
            .Where(static x => x.PredictedWinProbability >= 0.55m)
            .Select(static x => (double)x.RealizedNetReturnBps)
            .ToArray();
        if (activeReturns.Length == 0)
        {
            activeReturns = outOfSampleEvaluations.Select(static x => (double)x.RealizedNetReturnBps).ToArray();
        }

        var n = activeReturns.Length;
        var mean = activeReturns.Average();
        var variance = n > 1
            ? activeReturns.Sum(r => (r - mean) * (r - mean)) / (n - 1)
            : 0.0;
        var std = Math.Sqrt(Math.Max(0.0, variance));
        var sharpe = std > 1e-6 ? (decimal)Math.Round(mean / std, 4) : (mean > 0 ? 1.0m : 0m);

        decimal skewness = 0m;
        decimal kurtosis = 3m;
        if (n >= 4 && std > 1e-6)
        {
            var m3 = activeReturns.Average(r => Math.Pow((r - mean) / std, 3));
            var m4 = activeReturns.Average(r => Math.Pow((r - mean) / std, 4));
            skewness = (decimal)Math.Clamp(m3, -3.0, 3.0);
            kurtosis = (decimal)Math.Clamp(m4, 1.5, 10.0);
        }

        var dsr = CalculateDeflatedSharpeRatio(
            sharpe,
            n,
            independentTrials,
            skewness,
            kurtosis);

        string? rejectionReason = null;
        if (outOfSampleEvaluations.Count < minSampleCount)
        {
            rejectionReason = "INSUFFICIENT_OUT_OF_SAMPLE_EVALUATIONS";
        }
        else if (calibration.BrierScore > maxBrierScore)
        {
            rejectionReason = "BRIER_SCORE_ABOVE_THRESHOLD";
        }
        else if (sharpe <= 0m)
        {
            rejectionReason = "NON_POSITIVE_OUT_OF_SAMPLE_SHARPE";
        }
        else if (dsr < minDeflatedSharpeRatio)
        {
            rejectionReason = "DEFLATED_SHARPE_RATIO_BELOW_THRESHOLD";
        }

        return new MlWalkForwardPromotionReport(
            SampleCount: outOfSampleEvaluations.Count,
            BrierScore: calibration.BrierScore,
            ExpectedCalibrationError: calibration.ExpectedCalibrationError,
            OutOfSampleSharpeRatio: sharpe,
            DeflatedSharpeRatio: dsr,
            PromotionAuthorized: rejectionReason is null,
            RejectionReason: rejectionReason);
    }

    private static double StandardNormalCdf(double x)
    {
        // Abramowitz & Stegun 7.1.26 approximation
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1.0 : 1.0;
        var absX = Math.Abs(x) / Math.Sqrt(2.0);
        var t = 1.0 / (1.0 + (p * absX));
        var y = 1.0 - (((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * Math.Exp(-absX * absX));
        return 0.5 * (1.0 + (sign * y));
    }

    private static double InverseStandardNormalCdf(double p)
    {
        var clamped = Math.Clamp(p, 1e-6, 1.0 - 1e-6);
        // Beasley-Springer-Moro rational approximation for central/tail regions
        var q = clamped - 0.5;
        if (Math.Abs(q) <= 0.42)
        {
            var r = q * q;
            return q * (((((-25.44106049637 * r + 41.39119773534) * r - 18.61500062529) * r + 2.50662823884) /
                        ((((3.13082909833 * r - 21.06224101826) * r + 23.08336743743) * r - 8.47351093090) * r + 1.0)));
        }

        var tail = q > 0 ? 1.0 - clamped : clamped;
        var s = Math.Sqrt(-2.0 * Math.Log(tail));
        var z = s - ((2.515517 + (0.802853 * s) + (0.010328 * s * s)) /
                     (1.0 + (1.432788 * s) + (0.189269 * s * s) + (0.001308 * s * s * s)));
        return q < 0 ? -z : z;
    }

}
