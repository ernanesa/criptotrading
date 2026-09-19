using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MonteCarloStressTests
{
    [Fact]
    public void SameSeedAndInputsProduceIdenticalDistribution()
    {
        var returns = new decimal[] { 0.02m, -0.01m, 0.005m, -0.03m, 0.01m };
        var options = new MonteCarloStressOptions(500, 20, seed: 42, initialEquity: 1_000m);

        var first = MonteCarloStressEvaluator.Evaluate(returns, options);
        var second = MonteCarloStressEvaluator.Evaluate(returns, options);

        Assert.Equal(first, second);
        Assert.Equal(options.SimulationCount, first.SimulationCount);
    }

    [Fact]
    public void AdverseShockCannotImproveTerminalDistributionOrDrawdownQuantiles()
    {
        var returns = new decimal[] { 0.02m, -0.01m, 0.005m, -0.03m, 0.01m };
        var baseline = MonteCarloStressEvaluator.Evaluate(
            returns,
            new MonteCarloStressOptions(1_000, 25, seed: 9, initialEquity: 1_000m));
        var stressed = MonteCarloStressEvaluator.Evaluate(
            returns,
            new MonteCarloStressOptions(
                1_000,
                25,
                seed: 9,
                initialEquity: 1_000m,
                negativeReturnMultiplier: 1.5m,
                adverseReturnShock: 0.002m,
                scenarioId: "adverse"));

        Assert.True(stressed.MeanFinalEquity <= baseline.MeanFinalEquity);
        Assert.True(stressed.FinalEquityP50 <= baseline.FinalEquityP50);
        Assert.True(stressed.MaximumDrawdownP95 >= baseline.MaximumDrawdownP95);
        Assert.True(stressed.ProbabilityOfLoss >= baseline.ProbabilityOfLoss);
    }

    [Fact]
    public void EvaluationCanConsumeExistingBacktestEquityReturns()
    {
        var backtest = TrendFollowingBacktestEvaluator.Evaluate(
            Enumerable.Range(0, 30)
                .Select(index => new CloseBar(new Symbol("BTCUSDT"), DateTimeOffset.UnixEpoch.AddDays(index), 100m + index))
                .ToArray(),
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto());

        var result = MonteCarloStressEvaluator.Evaluate(
            backtest,
            new MonteCarloStressOptions(100, 10, seed: 7, initialEquity: 10_000m));

        Assert.Equal(backtest.EquityCurve.Count, result.ObservedPeriodCount);
        Assert.Equal(100, result.SimulationCount);
        Assert.Equal("bootstrap", result.ScenarioId);
    }

    [Fact]
    public void ConfigurationRejectsUnboundedWorkAndInvalidSamples()
    {
        Assert.Throws<ArgumentException>(() => new MonteCarloStressOptions(10_000, 501, 1, 1_000m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonteCarloStressOptions(10, 10, 1, 1_000m, adverseReturnShock: 1m));
        Assert.Throws<ArgumentException>(() => MonteCarloStressEvaluator.Evaluate(
            Array.Empty<decimal>(),
            new MonteCarloStressOptions(10, 10, 1, 1_000m)));
    }

    [Fact]
    public void RuinIsReportedForCertainTotalLossReturns()
    {
        var result = MonteCarloStressEvaluator.Evaluate(
            [-1m],
            new MonteCarloStressOptions(20, 3, seed: 1, initialEquity: 100m));

        Assert.Equal(1m, result.ProbabilityOfRuin);
        Assert.Equal(1m, result.ProbabilityOfLoss);
        Assert.Equal(0m, result.FinalEquityP50);
        Assert.Equal(1m, result.WorstLossFraction);
    }
}
