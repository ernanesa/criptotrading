using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class TrendFollowingBacktestTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void EvaluatorProducesDeterministicNetReturnTurnoverAndDrawdown()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = TrendFollowingBacktestEvaluator.Evaluate(
            Bars(100m, 90m, 80m, 120m, 130m, 60m),
            parameters,
            new BacktestEvaluationOptions(1_000m, periodsPerYear: 365));

        Assert.InRange(result.Metrics.NetReturn, -0.500001m, -0.499999m);
        Assert.InRange(result.Metrics.FinalEquity, 499.999m, 500.001m);
        Assert.Equal(0m, result.Metrics.TotalFees);
        Assert.Equal(0m, result.Metrics.TotalSlippageCost);
        Assert.InRange(result.Metrics.TurnoverNotional, 1_499.999m, 1_500.001m);
        Assert.InRange(result.Metrics.TurnoverRatio, 1.499999m, 1.500001m);
        Assert.InRange(result.Metrics.MaximumDrawdown, 0.538460m, 0.538463m);
        Assert.Equal(2, result.Metrics.TradeCount);
        Assert.Equal(6, result.Metrics.PeriodCount);
    }

    [Fact]
    public void CostsAreAppliedOnBothSidesAndAreSeparatedFromReferenceTurnover()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var result = TrendFollowingBacktestEvaluator.Evaluate(
            Bars(100m, 90m, 80m, 120m, 130m, 60m),
            parameters,
            new BacktestEvaluationOptions(1_000m, periodsPerYear: 365));

        Assert.Equal(2, result.Trades.Count);
        Assert.True(result.Metrics.TotalFees > 0m);
        Assert.True(result.Metrics.TotalSlippageCost > 0m);
        Assert.True(result.Metrics.FinalEquity < 500m);
        Assert.Equal(result.Trades.Sum(trade => trade.Fee), result.Metrics.TotalFees);
        Assert.Equal(result.Trades.Sum(trade => trade.SlippageCost), result.Metrics.TotalSlippageCost);
    }

    [Fact]
    public void SharpeUsesConfiguredAnnualizationAndIsUndefinedWithoutVariation()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = TrendFollowingBacktestEvaluator.Evaluate(
            Bars(100m, 100m, 100m, 100m),
            parameters,
            new BacktestEvaluationOptions(1_000m, periodsPerYear: 12, annualRiskFreeRate: 0m));

        Assert.Null(result.Metrics.SharpeRatio);
        Assert.Equal(12, result.Metrics.PeriodsPerYear);
    }

    [Fact]
    public void EmptyDatasetReturnsInitialCapitalAndNoSharpe()
    {
        var result = TrendFollowingBacktestEvaluator.Evaluate(
            Array.Empty<CloseBar>(),
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto());

        Assert.Equal(10_000m, result.Metrics.FinalEquity);
        Assert.Equal(0m, result.Metrics.NetReturn);
        Assert.Empty(result.EquityCurve);
        Assert.Null(result.Metrics.SharpeRatio);
    }

    [Fact]
    public void WalkForwardPlanCreatesNonOverlappingTrainAndOutOfSampleWindows()
    {
        var windows = WalkForwardPlan.Create(totalBars: 30, trainingBars: 10, outOfSampleBars: 5, stepBars: 5);

        Assert.Equal(4, windows.Count);
        Assert.Equal((0, 10, 10, 5), (windows[0].InSampleStartIndex, windows[0].InSampleLength, windows[0].OutOfSampleStartIndex, windows[0].OutOfSampleLength));
        Assert.Equal((15, 10, 25, 5), (windows[^1].InSampleStartIndex, windows[^1].InSampleLength, windows[^1].OutOfSampleStartIndex, windows[^1].OutOfSampleLength));
        Assert.All(windows, window => Assert.True(window.OutOfSampleStartIndex >= window.InSampleStartIndex + window.InSampleLength));
    }

    [Fact]
    public void SensitivityPlanRequiresUniqueCaseIdentifiers()
    {
        var parameters = TrendFollowingParameters.BtcUsdtBaseline;

        Assert.Throws<ArgumentException>(() => SensitivityPlan.Create([
            new SensitivityCase("baseline", parameters),
            new SensitivityCase("baseline", parameters)]));
    }

    private static CloseBar[] Bars(params decimal[] prices) =>
        prices.Select((price, index) => new CloseBar(
                BtcUsdt,
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                price))
            .ToArray();
}
