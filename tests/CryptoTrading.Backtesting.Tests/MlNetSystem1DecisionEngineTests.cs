using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Regimes;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MlNetSystem1DecisionEngineTests
{
    [Fact]
    public void HorizonScalesWithVolatilityInsteadOfStayingAtFourBars()
    {
        Assert.Equal(18, MlNetSystem1DecisionEngine.ComputeHorizonBars(180m, 10m));
        Assert.Equal(6, MlNetSystem1DecisionEngine.ComputeHorizonBars(180m, 30m));
        Assert.Equal(4, MlNetSystem1DecisionEngine.ComputeHorizonBars(180m, 60m));
        Assert.Equal(32, MlNetSystem1DecisionEngine.ComputeHorizonBars(180m, 1m));
    }

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
    public void EvaluateWithoutArtifactExplicitlyReportsUnavailableEvidence()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var closePrices = bars.Select(b => b.ClosePrice.Value).ToArray();
        var regime = MarketRegimeClassifier.Classify(bars[0].Symbol, closePrices, bars[^1].CloseTimeUtc);

        var evaluation = MlNetSystem1DecisionEngine.Evaluate(
            bars,
            regime,
            OrderSide.Buy,
            orderBookImbalance: 0.15m);

        Assert.Equal(0m, evaluation.MlWinProbability);
        Assert.False(evaluation.HasCalibratedProbability);
        Assert.False(evaluation.HasSsaEvidence);
        Assert.Equal("ML_CALIBRATED_ARTIFACT_UNAVAILABLE", evaluation.AvailabilityReason);
    }

    [Fact]
    public void MissingArtifactCannotTurnRegimeConfidenceIntoAProbability()
    {
        var at = DateTimeOffset.Parse("2026-09-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var symbol = new CryptoTrading.Domain.Primitives.Symbol("BTCUSDT");
        CloseBar[] bars = [new(symbol, at.AddMinutes(-1), 100m), new(symbol, at, 101m)];
        var regime = new MarketRegimeSnapshot(symbol, at, MarketRegime.BullTrend, 0.95m, 0.01m, 0.01m, "unit fixture");
        var evaluation = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, 0.8m);
        Assert.Equal(0m, evaluation.MlWinProbability);
        Assert.False(evaluation.HasCalibratedProbability);
    }

    [Fact]
    public void MissingArtifactOnLongHistoryDoesNotTrainOrPublishAProbability()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var regime = MarketRegimeClassifier.Classify(bars[0].Symbol, bars.Select(bar => bar.ClosePrice.Value).ToArray(), bars[^1].CloseTimeUtc);
        var evaluation = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, 0.8m);
        Assert.Equal(0m, evaluation.MlWinProbability);
        Assert.False(evaluation.HasCalibratedProbability);
    }

    [Fact]
    public void InferenceUsesPreviouslyTrainedArtifactAndRejectsWrongSide()
    {
        var export = MlDecisionArtifactTests.Train();
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        var symbol = new CryptoTrading.Domain.Primitives.Symbol("BTCUSDT");
        var start = DateTimeOffset.Parse("2026-09-01T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var bars = Enumerable.Range(0, 30).Select(i => new CloseBar(symbol, start.AddMinutes(i), 100m + i % 5 * 0.1m)).ToArray();
        var regime = MarketRegimeClassifier.Classify(symbol, bars.Select(bar => bar.ClosePrice.Value).ToArray(), bars[^1].CloseTimeUtc);
        var result = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, null, artifact: artifact);
        Assert.True(result.HasCalibratedProbability);
        Assert.InRange(result.MlWinProbability, 0.01m, 0.99m);
        Assert.Equal(artifact.Metadata.ModelId, result.ModelId);
        Assert.Null(result.AvailabilityReason);
        var incompatible = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Sell, null, artifact: artifact);
        Assert.False(incompatible.HasCalibratedProbability);
        Assert.Equal("ML_ARTIFACT_CONTEXT_MISMATCH", incompatible.AvailabilityReason);
    }

    [Fact]
    public void LateEvaluationCannotUseAModelCreatedAfterItsFeatureBar()
    {
        var export = MlDecisionArtifactTests.Train();
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        var symbol = new CryptoTrading.Domain.Primitives.Symbol("BTCUSDT");
        var start = DateTimeOffset.Parse("2026-09-01T02:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var bars = Enumerable.Range(0, 30).Select(i => new CloseBar(symbol, start.AddMinutes(i), 100m + i % 5 * 0.1m)).ToArray();
        var regime = MarketRegimeClassifier.Classify(symbol, bars.Select(bar => bar.ClosePrice.Value).ToArray(), start.AddHours(2));
        var result = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, null, artifact: artifact);
        Assert.False(result.HasCalibratedProbability);
        Assert.Equal("ML_ARTIFACT_NOT_POINT_IN_TIME", result.AvailabilityReason);
    }

    [Fact]
    public void WalkForwardCannotUseOneCurrentBookValueAsHistoricalBookEvidence()
    {
        var export = MlDecisionArtifactTests.Train(usesBook: true);
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        var symbol = new CryptoTrading.Domain.Primitives.Symbol("BTCUSDT");
        var start = DateTimeOffset.Parse("2026-09-01T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var bars = Enumerable.Range(0, 60).Select(i => new CloseBar(symbol, start.AddMinutes(i), 100m + i % 5 * 0.1m)).ToArray();
        var windows = WalkForwardPlan.Create(bars.Length, 20, 10, 10);
        var report = MlNetSystem1DecisionEngine.EvaluateWalkForwardCalibration(bars, windows, orderBookImbalance: 0.8m, artifact: artifact);
        Assert.Equal(0, report.TotalEvaluations);
        Assert.False(report.BrierScoreThresholdMet);
    }

    [Fact]
    public void WalkForwardWithoutCalibratedArtifactCannotClaimCalibrationSuccess()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);

        var report = MlNetSystem1DecisionEngine.EvaluateWalkForwardCalibration(
            bars,
            windows,
            orderBookImbalance: 0.05m,
            targetBrierThreshold: 0.15m);

        Assert.Equal(0, report.TotalEvaluations);
        Assert.False(report.BrierScoreThresholdMet);
        Assert.Empty(report.ReliabilityBins);
    }
}
