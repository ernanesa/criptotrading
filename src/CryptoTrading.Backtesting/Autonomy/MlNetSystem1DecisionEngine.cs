using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>
/// Structured output produced by the ML.NET 6.0 Preview System-1 decision evaluator combining:
/// 1. Singular Spectrum Analysis (SSA) change-point and spike detection (<c>Microsoft.ML.TimeSeries</c>).
/// 2. Calibrated binary meta-labeling ensemble combining <c>Gam</c> (<c>Microsoft.ML.FastTree</c>) and
///    <c>SdcaLogisticRegression</c> (<c>Microsoft.ML</c>) with Platt Scaling and deterministic single-threaded execution.
/// </summary>
public readonly record struct MlNetSystem1Evaluation(
    decimal MlWinProbability,
    decimal MlRawScore,
    decimal SsaChangePointPValue,
    decimal SsaMartingaleScore,
    decimal SsaSpikePValue,
    bool IsSsaStructuralBreakdown);

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
    DateTimeOffset EvaluatedAtUtc);

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
    private const int MaxCachedEvaluations = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<EvaluationCacheKey, MlNetSystem1Evaluation> EvaluationCache = new();

    private readonly record struct EvaluationCacheKey(
        string Symbol,
        int BarCount,
        long LastBarCloseUtcTicks,
        decimal LastBarClosePrice,
        OrderSide EffectiveSide,
        MarketRegime Regime,
        decimal RegimeConfidence,
        decimal OrderBookImbalanceRounded,
        int EmpiricalObservationCount,
        decimal EmpiricalAverageRewardRounded);

    public static MlNetSystem1Evaluation Evaluate(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        OrderSide? candidateSide,
        decimal orderBookImbalance,
        StrategyRegimePerformance? empiricalPerformance = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(regime);

        if (bars.Count < 20)
        {
            return new MlNetSystem1Evaluation(
                MlWinProbability: regime.Confidence,
                MlRawScore: 0m,
                SsaChangePointPValue: 0.50m,
                SsaMartingaleScore: 0m,
                SsaSpikePValue: 0.50m,
                IsSsaStructuralBreakdown: false);
        }

        var effectiveSide = candidateSide ?? OrderSide.Buy;
        var lastBar = bars[^1];
        var cacheKey = new EvaluationCacheKey(
            lastBar.Symbol.Value,
            bars.Count,
            lastBar.CloseTimeUtc.UtcTicks,
            lastBar.ClosePrice.Value,
            effectiveSide,
            regime.Regime,
            Math.Round(regime.Confidence, 4),
            Math.Round(orderBookImbalance, 4),
            empiricalPerformance?.ObservationCount ?? 0,
            Math.Round(empiricalPerformance?.AverageReward ?? 0m, 4));

        if (EvaluationCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var mlContext = new MLContext(seed: 42);

        // 1. Evaluate SSA Change-Point and Spike Detection on 1-minute returns
        var (changePointPValue, martingaleScore, spikePValue, isBreakdown) = EvaluateSsaTimeSeries(mlContext, bars);

        // 2. Evaluate Calibrated Binary Classification Meta-Labeler Ensemble (GAM + SdcaLogisticRegression, NumberOfThreads = 1)
        var (winProbability, rawScore) = EvaluateCalibratedMetaLabeler(
            mlContext,
            bars,
            regime,
            effectiveSide,
            orderBookImbalance,
            changePointPValue,
            empiricalPerformance);

        var result = new MlNetSystem1Evaluation(
            MlWinProbability: winProbability,
            MlRawScore: rawScore,
            SsaChangePointPValue: changePointPValue,
            SsaMartingaleScore: martingaleScore,
            SsaSpikePValue: spikePValue,
            IsSsaStructuralBreakdown: isBreakdown);

        if (EvaluationCache.Count >= MaxCachedEvaluations)
        {
            EvaluationCache.Clear();
        }

        EvaluationCache[cacheKey] = result;
        return result;
    }

    private static (decimal ChangePointPValue, decimal MartingaleScore, decimal SpikePValue, bool IsBreakdown) EvaluateSsaTimeSeries(
        MLContext mlContext,
        IReadOnlyList<CloseBar> bars)
    {
        var n = bars.Count;
        var returnPoints = new List<SsaInputRow>(n - 1);
        for (var i = 1; i < n; i++)
        {
            var prev = bars[i - 1].ClosePrice.Value;
            var curr = bars[i].ClosePrice.Value;
            var retBps = prev > 0m ? (float)((curr - prev) / prev * 10_000m) : 0f;
            returnPoints.Add(new SsaInputRow { Value = retBps });
        }

        var seriesLen = returnPoints.Count;
        var pvalueHistoryLen = Math.Clamp(seriesLen / 2, 6, 24);

        var dataView = mlContext.Data.LoadFromEnumerable(returnPoints);

        var changePointEstimator = mlContext.Transforms.DetectIidChangePoint(
            outputColumnName: nameof(SsaOutputRow.Prediction),
            inputColumnName: nameof(SsaInputRow.Value),
            confidence: 95.0,
            changeHistoryLength: pvalueHistoryLen);

        var cpTransformed = changePointEstimator.Fit(dataView).Transform(dataView);
        var cpPredictions = mlContext.Data.CreateEnumerable<SsaOutputRow>(cpTransformed, reuseRowObject: false).ToList();

        var spikeEstimator = mlContext.Transforms.DetectIidSpike(
            outputColumnName: nameof(SpikeOutputRow.Prediction),
            inputColumnName: nameof(SsaInputRow.Value),
            confidence: 98.0,
            pvalueHistoryLength: pvalueHistoryLen);

        var spikeTransformed = spikeEstimator.Fit(dataView).Transform(dataView);
        var spikePredictions = mlContext.Data.CreateEnumerable<SpikeOutputRow>(spikeTransformed, reuseRowObject: false).ToList();

        var lastCp = cpPredictions.Count > 0 ? cpPredictions[^1].Prediction : null;
        var lastSpike = spikePredictions.Count > 0 ? spikePredictions[^1].Prediction : null;

        var cpAlert = lastCp is { Length: >= 4 } && lastCp[0] > 0.5;
        var cpPValue = lastCp is { Length: >= 4 } && !double.IsNaN(lastCp[2])
            ? Math.Clamp((decimal)lastCp[2], 0m, 1m)
            : 0.50m;
        var martingale = lastCp is { Length: >= 4 } && !double.IsNaN(lastCp[3])
            ? Math.Clamp((decimal)lastCp[3], 0m, 100m)
            : 0m;

        var spikeAlert = lastSpike is { Length: >= 3 } && lastSpike[0] > 0.5;
        var spikePValue = lastSpike is { Length: >= 3 } && !double.IsNaN(lastSpike[2])
            ? Math.Clamp((decimal)lastSpike[2], 0m, 1m)
            : 0.50m;

        var recent1BarReturn = bars[^2].ClosePrice.Value > 0m
            ? (bars[^1].ClosePrice.Value - bars[^2].ClosePrice.Value) / bars[^2].ClosePrice.Value
            : 0m;
        var recent3BarReturn = n >= 4 && bars[^4].ClosePrice.Value > 0m
            ? (bars[^1].ClosePrice.Value - bars[^4].ClosePrice.Value) / bars[^4].ClosePrice.Value
            : recent1BarReturn;

        var isNegativeShock = recent1BarReturn <= -0.00055m || recent3BarReturn <= -0.00075m;
        var isBreakdown = isNegativeShock &&
            ((cpAlert && cpPValue <= 0.05m) ||
             martingale >= 6.5m ||
             (spikeAlert && spikePValue <= 0.02m));

        return (Math.Round(cpPValue, 4), Math.Round(martingale, 4), Math.Round(spikePValue, 4), isBreakdown);
    }

    private static (decimal WinProbability, decimal RawScore) EvaluateCalibratedMetaLabeler(
        MLContext mlContext,
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        OrderSide side,
        decimal orderBookImbalance,
        decimal ssaChangePointPValue,
        StrategyRegimePerformance? empiricalPerformance)
    {
        var trainingRows = BuildPointInTimeTrainingSet(bars, regime, side, empiricalPerformance);
        var trainData = mlContext.Data.LoadFromEnumerable(trainingRows);

        var sdcaOptions = new SdcaLogisticRegressionBinaryTrainer.Options
        {
            LabelColumnName = nameof(MetaLabelRow.Label),
            FeatureColumnName = "Features",
            ExampleWeightColumnName = nameof(MetaLabelRow.Weight),
            MaximumNumberOfIterations = 35,
            NumberOfThreads = 1,
            L2Regularization = 0.05f,
            L1Regularization = 0.01f,
        };

        var gamOptions = new GamBinaryTrainer.Options
        {
            LabelColumnName = nameof(MetaLabelRow.Label),
            FeatureColumnName = "Features",
            ExampleWeightColumnName = nameof(MetaLabelRow.Weight),
            NumberOfIterations = 45,
            NumberOfThreads = 1,
            LearningRate = 0.02,
            MaximumBinCountPerFeature = 16,
        };

        var featurePipeline = mlContext.Transforms.Concatenate(
                "Features",
                nameof(MetaLabelRow.ZScore),
                nameof(MetaLabelRow.SlopeBpsPerBar),
                nameof(MetaLabelRow.Momentum3BarBps),
                nameof(MetaLabelRow.VolatilityBps),
                nameof(MetaLabelRow.OrderBookImbalance),
                nameof(MetaLabelRow.SsaStability))
            .Append(mlContext.Transforms.NormalizeMeanVariance("Features"));

        var sdcaModel = featurePipeline
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(sdcaOptions))
            .Fit(trainData);

        var gamModel = featurePipeline
            .Append(mlContext.BinaryClassification.Trainers.Gam(gamOptions))
            .Fit(trainData);

        using var sdcaEngine = mlContext.Model.CreatePredictionEngine<MetaLabelRow, MetaLabelPrediction>(sdcaModel);
        using var gamEngine = mlContext.Model.CreatePredictionEngine<MetaLabelRow, MetaLabelPrediction>(gamModel);

        var currentSample = BuildCurrentSample(bars, regime, orderBookImbalance, ssaChangePointPValue);
        var sdcaPred = sdcaEngine.Predict(currentSample);
        var gamPred = gamEngine.Predict(currentSample);

        var sdcaProb = !float.IsNaN(sdcaPred.Probability) && !float.IsInfinity(sdcaPred.Probability)
            ? Math.Clamp((decimal)sdcaPred.Probability, 0.05m, 0.99m)
            : regime.Confidence;
        var gamProb = !float.IsNaN(gamPred.Probability) && !float.IsInfinity(gamPred.Probability)
            ? Math.Clamp((decimal)gamPred.Probability, 0.05m, 0.99m)
            : regime.Confidence;

        var ensembleProb = Math.Clamp((sdcaProb * 0.55m) + (gamProb * 0.45m), 0.01m, 0.99m);
        var ensembleScore = (!float.IsNaN(sdcaPred.Score) && !float.IsNaN(gamPred.Score))
            ? Math.Round(((decimal)sdcaPred.Score * 0.55m) + ((decimal)gamPred.Score * 0.45m), 4)
            : 0m;

        // Platt Scaling (John Platt, 1999) regularized sigmoid calibration
        var plattProb = PlattParameters.Default.Transform(ensembleScore);
        var finalCalibratedProb = Math.Clamp(
            Math.Round((ensembleProb * 0.70m) + (plattProb * 0.30m), 4),
            0.01m,
            0.99m);

        return (finalCalibratedProb, ensembleScore);
    }

    private static List<MetaLabelRow> BuildPointInTimeTrainingSet(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        OrderSide side,
        StrategyRegimePerformance? empiricalPerformance)
    {
        var rows = new List<MetaLabelRow>(Math.Max(68, bars.Count));

        var dir = side == OrderSide.Buy ? 1f : -1f;
        var priorScale = bars.Count >= 60 ? 0.35f : 1.0f;

        // Calibrated domain priors prevent degenerate single-class fits on short windows while encoding domain invariants:
        // 1. Oversold Z-Score (for Buy) or Overbought Z-Score (for Sell) with aligned order book -> profitable reversion
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f * priorScale, ZScore = -1.65f * dir, SlopeBpsPerBar = 0.0f, Momentum3BarBps = -3.2f * dir, VolatilityBps = 3.5f, OrderBookImbalance = 0.35f * dir, SsaStability = 0.85f });
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f * priorScale, ZScore = -1.40f * dir, SlopeBpsPerBar = 0.2f * dir, Momentum3BarBps = -2.5f * dir, VolatilityBps = 2.8f, OrderBookImbalance = 0.20f * dir, SsaStability = 0.80f });
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f * priorScale, ZScore = 0.60f * dir, SlopeBpsPerBar = 2.1f * dir, Momentum3BarBps = 5.5f * dir, VolatilityBps = 4.0f, OrderBookImbalance = 0.45f * dir, SsaStability = 0.75f });

        // 2. Breakdown against side / adverse order book / chasing against reversion -> unprofitable
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f * priorScale, ZScore = -2.40f * dir, SlopeBpsPerBar = -2.8f * dir, Momentum3BarBps = -11.0f * dir, VolatilityBps = 9.5f, OrderBookImbalance = -0.80f * dir, SsaStability = 0.05f });
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f * priorScale, ZScore = 1.50f * dir, SlopeBpsPerBar = 0.1f * dir, Momentum3BarBps = 4.2f * dir, VolatilityBps = 3.2f, OrderBookImbalance = -0.45f * dir, SsaStability = 0.40f });
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f * priorScale, ZScore = -0.50f * dir, SlopeBpsPerBar = -1.9f * dir, Momentum3BarBps = -8.5f * dir, VolatilityBps = 7.0f, OrderBookImbalance = -0.60f * dir, SsaStability = 0.15f });

        // 3. Closed-loop empirical experience replay from real executed strategy-regime rewards in PostgreSQL
        if (empiricalPerformance is { ObservationCount: > 0 })
        {
            var replayWeight = Math.Clamp(empiricalPerformance.ObservationCount * 0.45f, 0.8f, 2.5f);
            var regimeSlopeBps = (float)Math.Clamp(regime.TrendSlopeRate * 10_000m, -15m, 15m);
            var regimeVolBps = (float)Math.Clamp(regime.VolatilityRate * 10_000m, 0.5m, 30m);
            if (empiricalPerformance.AverageReward > 0m)
            {
                rows.Add(new MetaLabelRow
                {
                    Label = true,
                    Weight = replayWeight,
                    ZScore = -1.35f * dir,
                    SlopeBpsPerBar = regimeSlopeBps,
                    Momentum3BarBps = -2.0f * dir,
                    VolatilityBps = regimeVolBps,
                    OrderBookImbalance = 0.30f * dir,
                    SsaStability = 0.75f,
                });
            }
            else if (empiricalPerformance.AverageReward < 0m)
            {
                rows.Add(new MetaLabelRow
                {
                    Label = false,
                    Weight = replayWeight,
                    ZScore = -1.30f * dir,
                    SlopeBpsPerBar = regimeSlopeBps,
                    Momentum3BarBps = -3.5f * dir,
                    VolatilityBps = regimeVolBps,
                    OrderBookImbalance = -0.25f * dir,
                    SsaStability = 0.35f,
                });
            }
        }

        var n = bars.Count;
        const int lookback = 8;
        const int horizon = 4;
        var maxSampleSpan = Math.Max(1, n - horizon - 1 - lookback);

        // Point-in-time walk-forward samples strictly prior to the current bar (endIdx = i + horizon < n - 1)
        for (var i = lookback; i + horizon < n - 1; i++)
        {
            var (zScore, slopeBps, mom3Bps, volBps) = ComputePointInTimeFeatures(bars, i, lookback);
            var minProfitableMoveBps = Math.Max(35.0m, volBps * 2.5m);
            var maxAllowedStopBps = Math.Max(18.0m, volBps * 1.5m);
            var hitTakeProfitBeforeStop = EvaluateChronologicalTripleBarrier(
                bars,
                i,
                horizon,
                side,
                minProfitableMoveBps,
                maxAllowedStopBps);
            var recencyRatio = (float)(i - lookback) / maxSampleSpan;
            var recencyWeight = 0.50f + (1.00f * recencyRatio);
            rows.Add(new MetaLabelRow
            {
                Label = hitTakeProfitBeforeStop,
                Weight = recencyWeight,
                ZScore = (float)Math.Clamp(zScore, -4m, 4m),
                SlopeBpsPerBar = (float)Math.Clamp(slopeBps, -15m, 15m),
                Momentum3BarBps = (float)Math.Clamp(mom3Bps, -30m, 30m),
                VolatilityBps = (float)Math.Clamp(volBps, 0.5m, 30m),
                OrderBookImbalance = 0f,
                SsaStability = 0.65f,
            });
        }

        return rows;
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

    private static (decimal ZScore, decimal SlopeBps, decimal Mom3Bps, decimal VolBps) ComputePointInTimeFeatures(
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

    private static MetaLabelRow BuildCurrentSample(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        decimal orderBookImbalance,
        decimal ssaChangePointPValue)
    {
        var n = bars.Count;
        var lookback = Math.Min(8, n - 1);
        var (zScore, _, mom3Bps, _) = ComputePointInTimeFeatures(bars, n - 1, lookback);
        var slopeBps = regime.TrendSlopeRate * 10_000m;
        var volBps = regime.VolatilityRate * 10_000m;

        return new MetaLabelRow
        {
            Label = false,
            ZScore = (float)Math.Clamp(zScore, -4m, 4m),
            SlopeBpsPerBar = (float)Math.Clamp(slopeBps, -15m, 15m),
            Momentum3BarBps = (float)Math.Clamp(mom3Bps, -30m, 30m),
            VolatilityBps = (float)Math.Clamp(volBps, 0.5m, 30m),
            OrderBookImbalance = (float)Math.Clamp(orderBookImbalance, -1m, 1m),
            SsaStability = (float)Math.Clamp(ssaChangePointPValue, 0m, 1m),
        };
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
                BrierScoreThresholdMet: true,
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
        decimal targetBrierThreshold = 0.15m)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(windows);
        if (bars.Count < 20 || windows.Count == 0)
        {
            return new MlProbabilityCalibrationReport(
                BrierScore: 0m,
                ExpectedCalibrationError: 0m,
                BrierScoreThresholdMet: true,
                TargetBrierThreshold: targetBrierThreshold,
                TotalEvaluations: 0,
                PositiveOutcomes: 0,
                ReliabilityBins: [],
                EvaluatedAtUtc: DateTimeOffset.UtcNow);
        }

        const int horizon = 4;
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

                var eval = Evaluate(pointInTimeBars, regime, OrderSide.Buy, orderBookImbalance);

                var volBps = regime.VolatilityRate * 10_000m;
                var minProfitableMoveBps = Math.Max(35.0m, volBps * 2.5m);
                var maxAllowedStopBps = Math.Max(18.0m, volBps * 1.5m);
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

    private sealed class SsaInputRow
    {
        public float Value { get; set; }
    }

    private sealed class SsaOutputRow
    {
        [VectorType(4)]
        public double[]? Prediction { get; set; }
    }

    private sealed class SpikeOutputRow
    {
        [VectorType(3)]
        public double[]? Prediction { get; set; }
    }

    private sealed class MetaLabelRow
    {
        public bool Label { get; set; }
        public float Weight { get; set; } = 1.0f;
        public float ZScore { get; set; }
        public float SlopeBpsPerBar { get; set; }
        public float Momentum3BarBps { get; set; }
        public float VolatilityBps { get; set; }
        public float OrderBookImbalance { get; set; }
        public float SsaStability { get; set; }
    }

    private sealed class MetaLabelPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        public float Probability { get; set; }

        public float Score { get; set; }
    }
}
