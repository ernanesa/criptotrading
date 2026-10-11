using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class StrategyBacktestEvaluatorTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EntrySignalFillsAtNextBarCloseAndChargesFee()
    {
        var bars = Bars(100m, 110m);
        var signals = Signals(2,
            new StrategyExecutionSignal(0, StrategyExecutionAction.EnterLong),
            new StrategyExecutionSignal(1, StrategyExecutionAction.NoTrade));

        var result = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(1_000m, 365, feeRate: 0.01m, halfSpreadRate: 0m, slippageRate: 0m));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(0, trade.DecisionBarIndex);
        Assert.Equal(1, trade.FillBarIndex);
        Assert.Equal(110m, trade.ReferencePrice);
        Assert.Equal(110m, trade.ExecutedPrice);
        Assert.Equal(StrategyExecutionAction.EnterLong, trade.Action);
        Assert.Equal(1_000m, result.Metrics.InitialCapital);
        Assert.Equal(1, result.Metrics.FilledOrderCount);
        Assert.Equal(0, result.Metrics.UnfilledSignalCount);
        Assert.InRange(result.Metrics.FinalEquity, 990.09m, 990.10m);
        Assert.Equal(0m, result.Metrics.TotalSpreadCost);
        Assert.Equal(0m, result.Metrics.TotalSlippageCost);
    }

    [Fact]
    public void SignalOnLastBarIsNotFilledUsingSameBarClose()
    {
        var bars = Bars(100m, 101m);
        var signals = Signals(2,
            new StrategyExecutionSignal(0, StrategyExecutionAction.NoTrade),
            new StrategyExecutionSignal(1, StrategyExecutionAction.EnterLong));

        var result = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(1_000m, 365, feeRate: 0m, halfSpreadRate: 0m, slippageRate: 0m));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.Metrics.UnfilledSignalCount);
        Assert.Equal(1_000m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void EntryAndExitUseAdverseCostsAtTheirNextBarCloses()
    {
        var bars = Bars(100m, 110m, 120m);
        var signals = Signals(3,
            new StrategyExecutionSignal(0, StrategyExecutionAction.EnterLong),
            new StrategyExecutionSignal(1, StrategyExecutionAction.ExitLong),
            new StrategyExecutionSignal(2, StrategyExecutionAction.NoTrade));

        var result = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(1_000m, 365, feeRate: 0m, halfSpreadRate: 0.01m, slippageRate: 0.02m));

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(110m * 1.03m, result.Trades[0].ExecutedPrice);
        Assert.Equal(120m * 0.97m, result.Trades[1].ExecutedPrice);
        Assert.True(result.Metrics.TotalSpreadCost > 0m);
        Assert.True(result.Metrics.TotalSlippageCost > 0m);
    }

    [Fact]
    public void RejectsSignalCollectionsThatDoNotMatchTheBarSequence()
    {
        Assert.Throws<ArgumentException>(() => StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", Bars(100m, 101m),
            [new StrategyExecutionSignal(0, StrategyExecutionAction.NoTrade)],
            new StrategyBacktestOptions(1_000m, 365, 0m, 0m, 0m)));
    }

    [Fact]
    public void FingerprintChangesWhenAnnualRiskFreeAssumptionChanges()
    {
        var bars = Bars(100m, 110m, 120m);
        var signals = Signals(3,
            new StrategyExecutionSignal(0, StrategyExecutionAction.EnterLong),
            new StrategyExecutionSignal(1, StrategyExecutionAction.ExitLong),
            new StrategyExecutionSignal(2, StrategyExecutionAction.NoTrade));
        var zeroRate = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(1_000m, 365, 0m, 0m, 0m, annualRiskFreeRate: 0m));
        var nonzeroRate = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(1_000m, 365, 0m, 0m, 0m, annualRiskFreeRate: 0.05m));

        Assert.NotEqual(zeroRate.EvaluationFingerprint, nonzeroRate.EvaluationFingerprint);
        Assert.NotEqual(zeroRate.Metrics.SharpeRatio, nonzeroRate.Metrics.SharpeRatio);
    }

    [Fact]
    public void ReportsPartialFixedQuantityFillAndMarksInventoryStateAsDivergent()
    {
        var result = StrategyBacktestEvaluator.Evaluate(
            "grid", "1.0", "fingerprint", Bars(100m, 110m),
            Signals(2,
                new StrategyExecutionSignal(0, StrategyExecutionAction.BuyBaseQuantity, baseQuantity: 2m),
                new StrategyExecutionSignal(1, StrategyExecutionAction.NoTrade)),
            new StrategyBacktestOptions(100m, 365, 0m, 0m, 0m));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(2m, trade.RequestedBaseQuantity);
        Assert.True(trade.IsPartialFill);
        Assert.Equal(1, result.Metrics.PartialFillCount);
        Assert.True(result.Metrics.HasPartialFixedQuantityFills);
    }

    [Fact]
    public void RejectsOrdersBelowConfiguredMinimumNotional()
    {
        var result = StrategyBacktestEvaluator.Evaluate(
            "grid", "1.0", "fingerprint", Bars(100m, 110m),
            Signals(2,
                new StrategyExecutionSignal(0, StrategyExecutionAction.BuyBaseQuantity, baseQuantity: 0.01m),
                new StrategyExecutionSignal(1, StrategyExecutionAction.NoTrade)),
            new StrategyBacktestOptions(
                100m, 365, 0m, 0m, 0m, minimumOrderNotionalQuote: 5m, maximumPositionNotionalAtBuyQuote: 100m));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.Metrics.RejectedMinimumNotionalSignalCount);
        Assert.Equal(1, result.Metrics.UnfilledSignalCount);
        Assert.Equal(100m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void WinningTradesDoNotIncreaseConfiguredMaximumExposure()
    {
        var bars = Bars(100m, 200m, 300m, 400m);
        var signals = Signals(4,
            new StrategyExecutionSignal(0, StrategyExecutionAction.EnterLong),
            new StrategyExecutionSignal(1, StrategyExecutionAction.ExitLong),
            new StrategyExecutionSignal(2, StrategyExecutionAction.EnterLong),
            new StrategyExecutionSignal(3, StrategyExecutionAction.NoTrade));

        var result = StrategyBacktestEvaluator.Evaluate(
            "test", "1.0", "fingerprint", bars, signals,
            new StrategyBacktestOptions(100m, 365, 0m, 0m, 0m, maximumPositionNotionalAtBuyQuote: 100m));

        var secondEntry = result.Trades[^1];
        Assert.Equal(StrategyExecutionAction.EnterLong, secondEntry.Action);
        Assert.InRange(secondEntry.BaseQuantity * secondEntry.ReferencePrice, 99.99m, 100.01m);
        Assert.InRange(result.Metrics.MaximumGrossExposureQuote, 99.99m, 100.01m);
    }

    private static CloseBar[] Bars(params decimal[] closes) => closes
        .Select((close, index) => new CloseBar(BtcUsdt, Start.AddDays(index + 1), close))
        .ToArray();

    private static StrategyExecutionSignal[] Signals(int count, params StrategyExecutionSignal[] values)
    {
        var byIndex = values.ToDictionary(static signal => signal.DecisionBarIndex);
        return Enumerable.Range(0, count)
            .Select(index => byIndex.GetValueOrDefault(index) ?? new StrategyExecutionSignal(index, StrategyExecutionAction.NoTrade))
            .ToArray();
    }
}
