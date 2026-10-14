using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Regimes;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MlNetSystem1DecisionEngineTests
{
    [Fact]
    public void BrierScorePerfectForecastsReturnsZero()
    {
        var predictions = new (decimal, bool)[]
        {
            (1.0m, true),
            (0.0m, false),
            (1.0m, true),
            (0.0m, false),
        };

        var score = MlNetSystem1DecisionEngine.CalculateBrierScore(predictions);

        Assert.Equal(0.0m, score);
    }

    [Fact]
    public void BrierScoreInvertedForecastsReturnsOne()
    {
        var predictions = new (decimal, bool)[]
        {
            (0.0m, true),
            (1.0m, false),
        };

        var score = MlNetSystem1DecisionEngine.CalculateBrierScore(predictions);

        Assert.Equal(1.0m, score);
    }

    [Fact]
    public void BrierScoreUniformUninformativeFiftyPercentReturnsTwentyFivePercent()
    {
        var predictions = new (decimal, bool)[]
        {
            (0.5m, true),
            (0.5m, false),
        };

        var score = MlNetSystem1DecisionEngine.CalculateBrierScore(predictions);

        Assert.Equal(0.25m, score);
    }

    [Fact]
    public void CalibrationReportPartitionsIntoBinsAndCalculatesEce()
    {
        var predictions = new (decimal, bool)[]
        {
            (0.10m, false),
            (0.15m, false),
            (0.35m, false),
            (0.55m, true),
            (0.70m, true),
            (0.90m, true),
        };

        var report = MlNetSystem1DecisionEngine.CalculateCalibrationReport(predictions, binCount: 5, targetBrierThreshold: 0.15m);

        Assert.Equal(6, report.TotalEvaluations);
        Assert.Equal(3, report.PositiveOutcomes);
        Assert.Equal(5, report.ReliabilityBins.Count);
        Assert.InRange(report.BrierScore, 0.01m, 0.15m);
        Assert.True(report.BrierScoreThresholdMet);
        Assert.InRange(report.ExpectedCalibrationError, 0.0m, 0.35m);
    }

    [Fact]
    public void PlattCalibratorFitConvergesAndEnforcesMonotonicity()
    {
        var samples = new List<(decimal Score, bool Label)>
        {
            (-2.5m, false),
            (-1.8m, false),
            (-1.2m, false),
            (-0.5m, false),
            (-0.1m, false),
            (0.2m, true),
            (0.8m, true),
            (1.4m, true),
            (1.9m, true),
            (2.6m, true),
        };

        var parameters = PlattCalibrator.Fit(samples);

        Assert.True(parameters.Alpha > 0m, "Alpha must be positive for monotonic probability scaling.");

        var pLow = parameters.Transform(-2.0m);
        var pMid = parameters.Transform(0.0m);
        var pHigh = parameters.Transform(2.0m);

        Assert.True(pLow < pMid, "p(-2) must be strictly less than p(0)");
        Assert.True(pMid < pHigh, "p(0) must be strictly less than p(2)");
        Assert.InRange(pLow, 0.01m, 0.99m);
        Assert.InRange(pMid, 0.01m, 0.99m);
        Assert.InRange(pHigh, 0.01m, 0.99m);
    }

    [Fact]
    public void PlattCalibratorSmallOrSingleClassReturnsDefaultParameters()
    {
        var empty = PlattCalibrator.Fit([]);
        Assert.Equal(PlattParameters.Default, empty);

        var singleClass = PlattCalibrator.Fit([
            (1.0m, true),
            (2.0m, true),
            (3.0m, true),
            (4.0m, true),
            (5.0m, true),
            (6.0m, true),
        ]);
        Assert.Equal(PlattParameters.Default, singleClass);
    }

    [Fact]
    public void EvaluateProducesPlattCalibratedProbabilityAndScores()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var closePrices = bars.Select(b => b.ClosePrice.Value).ToArray();
        var regime = MarketRegimeClassifier.Classify(bars[0].Symbol, closePrices, bars[^1].CloseTimeUtc);

        var evaluation = MlNetSystem1DecisionEngine.Evaluate(
            bars,
            regime,
            OrderSide.Buy,
            orderBookImbalance: 0.15m);

        Assert.InRange(evaluation.MlWinProbability, 0.01m, 0.99m);
        Assert.InRange(evaluation.SsaChangePointPValue, 0.0m, 1.0m);
        Assert.InRange(evaluation.SsaSpikePValue, 0.0m, 1.0m);
        Assert.False(evaluation.IsSsaStructuralBreakdown);
    }

    [Fact]
    public void EvaluateWalkForwardCalibrationWalkForwardWindowsProducesBrierScoreBelowFifteenBps()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);

        var report = MlNetSystem1DecisionEngine.EvaluateWalkForwardCalibration(
            bars,
            windows,
            orderBookImbalance: 0.05m,
            targetBrierThreshold: 0.15m);

        Assert.True(report.TotalEvaluations > 0, "Walk-forward evaluation must generate OOS samples.");
        Assert.InRange(report.BrierScore, 0.0m, 0.15m);
        Assert.True(report.BrierScoreThresholdMet, $"Brier score {report.BrierScore} must meet threshold <= 0.15.");
        Assert.Equal(5, report.ReliabilityBins.Count);
        Assert.InRange(report.ExpectedCalibrationError, 0.0m, 0.35m);
        Assert.All(report.ReliabilityBins, bin =>
        {
            Assert.InRange(bin.MeanConfidence, bin.LowerBound, bin.UpperBound);
            Assert.InRange(bin.EmpiricalWinRate, 0.0m, 1.0m);
        });
    }
}
