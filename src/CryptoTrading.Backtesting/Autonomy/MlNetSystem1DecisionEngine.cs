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
///    <c>SdcaLogisticRegression</c> (<c>Microsoft.ML</c>) with deterministic single-threaded execution.
/// </summary>
public readonly record struct MlNetSystem1Evaluation(
    decimal MlWinProbability,
    decimal MlRawScore,
    decimal SsaChangePointPValue,
    decimal SsaMartingaleScore,
    decimal SsaSpikePValue,
    bool IsSsaStructuralBreakdown);

public static class MlNetSystem1DecisionEngine
{
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

        var mlContext = new MLContext(seed: 42);

        // 1. Evaluate SSA Change-Point and Spike Detection on 1-minute returns
        var (changePointPValue, martingaleScore, spikePValue, isBreakdown) = EvaluateSsaTimeSeries(mlContext, bars);

        // 2. Evaluate Calibrated Binary Classification Meta-Labeler Ensemble (GAM + SdcaLogisticRegression, NumberOfThreads = 1)
        var (winProbability, rawScore) = EvaluateCalibratedMetaLabeler(
            mlContext,
            bars,
            regime,
            candidateSide ?? OrderSide.Buy,
            orderBookImbalance,
            changePointPValue,
            empiricalPerformance);

        return new MlNetSystem1Evaluation(
            MlWinProbability: winProbability,
            MlRawScore: rawScore,
            SsaChangePointPValue: changePointPValue,
            SsaMartingaleScore: martingaleScore,
            SsaSpikePValue: spikePValue,
            IsSsaStructuralBreakdown: isBreakdown);
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

        var ensembleProb = Math.Clamp((sdcaProb * 0.55m) + (gamProb * 0.45m), 0.05m, 0.99m);
        var ensembleScore = (!float.IsNaN(sdcaPred.Score) && !float.IsNaN(gamPred.Score))
            ? Math.Round(((decimal)sdcaPred.Score * 0.55m) + ((decimal)gamPred.Score * 0.45m), 4)
            : 0m;

        return (Math.Round(ensembleProb, 4), ensembleScore);
    }

    private static List<MetaLabelRow> BuildPointInTimeTrainingSet(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        OrderSide side,
        StrategyRegimePerformance? empiricalPerformance)
    {
        var rows = new List<MetaLabelRow>(68);

        var dir = side == OrderSide.Buy ? 1f : -1f;

        // Calibrated domain priors prevent degenerate single-class fits on short windows while encoding domain invariants:
        // 1. Oversold Z-Score (for Buy) or Overbought Z-Score (for Sell) with aligned order book -> profitable reversion
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f, ZScore = -1.65f * dir, SlopeBpsPerBar = 0.0f, Momentum3BarBps = -3.2f * dir, VolatilityBps = 3.5f, OrderBookImbalance = 0.35f * dir, SsaStability = 0.85f });
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f, ZScore = -1.40f * dir, SlopeBpsPerBar = 0.2f * dir, Momentum3BarBps = -2.5f * dir, VolatilityBps = 2.8f, OrderBookImbalance = 0.20f * dir, SsaStability = 0.80f });
        rows.Add(new MetaLabelRow { Label = true, Weight = 1.25f, ZScore = 0.60f * dir, SlopeBpsPerBar = 2.1f * dir, Momentum3BarBps = 5.5f * dir, VolatilityBps = 4.0f, OrderBookImbalance = 0.45f * dir, SsaStability = 0.75f });

        // 2. Breakdown against side / adverse order book / chasing against reversion -> unprofitable
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f, ZScore = -2.40f * dir, SlopeBpsPerBar = -2.8f * dir, Momentum3BarBps = -11.0f * dir, VolatilityBps = 9.5f, OrderBookImbalance = -0.80f * dir, SsaStability = 0.05f });
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f, ZScore = 1.50f * dir, SlopeBpsPerBar = 0.1f * dir, Momentum3BarBps = 4.2f * dir, VolatilityBps = 3.2f, OrderBookImbalance = -0.45f * dir, SsaStability = 0.40f });
        rows.Add(new MetaLabelRow { Label = false, Weight = 1.35f, ZScore = -0.50f * dir, SlopeBpsPerBar = -1.9f * dir, Momentum3BarBps = -8.5f * dir, VolatilityBps = 7.0f, OrderBookImbalance = -0.60f * dir, SsaStability = 0.15f });

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
            var sum = 0m;
            for (var j = i - lookback; j <= i; j++)
            {
                sum += bars[j].ClosePrice.Value;
            }

            var mean = sum / (lookback + 1);
            var sqDiff = 0m;
            for (var j = i - lookback; j <= i; j++)
            {
                var diff = bars[j].ClosePrice.Value - mean;
                sqDiff += diff * diff;
            }

            var std = (decimal)Math.Sqrt((double)(sqDiff / lookback));
            var zScore = std > 0m ? (bars[i].ClosePrice.Value - mean) / std : 0m;
            var slopeBps = bars[i - lookback].ClosePrice.Value > 0m
                ? (bars[i].ClosePrice.Value - bars[i - lookback].ClosePrice.Value) / bars[i - lookback].ClosePrice.Value * 10_000m / lookback
                : 0m;
            var mom3Bps = bars[i - 3].ClosePrice.Value > 0m
                ? (bars[i].ClosePrice.Value - bars[i - 3].ClosePrice.Value) / bars[i - 3].ClosePrice.Value * 10_000m
                : 0m;
            var volBps = mean > 0m ? std / mean * 10_000m : 0m;

            // Triple-barrier outcome over the next 'horizon' bars
            var entryPrice = bars[i].ClosePrice.Value;
            var maxFavorableBps = 0m;
            var maxAdverseBps = 0m;
            for (var h = 1; h <= horizon; h++)
            {
                var fwdPrice = bars[i + h].ClosePrice.Value;
                var retBps = entryPrice > 0m
                    ? (side == OrderSide.Buy ? (fwdPrice - entryPrice) : (entryPrice - fwdPrice)) / entryPrice * 10_000m
                    : 0m;
                if (retBps > maxFavorableBps)
                {
                    maxFavorableBps = retBps;
                }

                if (retBps < maxAdverseBps)
                {
                    maxAdverseBps = retBps;
                }
            }

            var minProfitableMoveBps = Math.Max(20.0m, Math.Clamp(volBps * 2.5m, 18.0m, 35.0m));
            var maxAllowedStopBps = Math.Clamp(volBps * 1.5m, 8.0m, 18.0m);
            var hitTakeProfitBeforeStop = maxFavorableBps >= minProfitableMoveBps && maxAdverseBps > -maxAllowedStopBps;
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

    private static MetaLabelRow BuildCurrentSample(
        IReadOnlyList<CloseBar> bars,
        MarketRegimeSnapshot regime,
        decimal orderBookImbalance,
        decimal ssaChangePointPValue)
    {
        var n = bars.Count;
        var lookback = Math.Min(12, n - 1);
        var sum = 0m;
        for (var j = n - lookback; j < n; j++)
        {
            sum += bars[j].ClosePrice.Value;
        }

        var mean = sum / lookback;
        var sqDiff = 0m;
        for (var j = n - lookback; j < n; j++)
        {
            var diff = bars[j].ClosePrice.Value - mean;
            sqDiff += diff * diff;
        }

        var std = lookback > 1 ? (decimal)Math.Sqrt((double)(sqDiff / (lookback - 1))) : 0m;
        var zScore = std > 0m ? (bars[^1].ClosePrice.Value - mean) / std : 0m;
        var slopeBps = regime.TrendSlopeRate * 10_000m;
        var mom3Bps = n >= 4 && bars[^4].ClosePrice.Value > 0m
            ? (bars[^1].ClosePrice.Value - bars[^4].ClosePrice.Value) / bars[^4].ClosePrice.Value * 10_000m
            : 0m;
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
