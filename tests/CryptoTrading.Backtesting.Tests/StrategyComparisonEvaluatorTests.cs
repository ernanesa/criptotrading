using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class StrategyComparisonEvaluatorTests
{
    [Fact]
    public void EvaluatesEveryBaselineOnTheSameDatasetWithoutAuthorizingPromotion()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);
        var options = new StrategyBacktestOptions(
            initialCapital: 10_000m,
            periodsPerYear: 365,
            feeRate: 0.001m,
            halfSpreadRate: 0m,
            slippageRate: 0.0005m);

        var result = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, dataset.Bars, options, windows);

        Assert.False(result.PromotionAuthorized);
        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.Equal(4, result.Strategies.Count);
        Assert.Equal(
            ["adaptive-grid-btcusdt", "breakout-momentum-btcusdt", "mean-reversion-btcusdt", "trend-following-btcusdt"],
            result.Strategies.Select(static item => item.StrategyId));
        Assert.All(result.Strategies, item =>
        {
            Assert.Equal(dataset.Metadata.ContentHash, item.DatasetContentHash);
            Assert.Equal(dataset.Bars.Count, item.FullPeriod.Metrics.PeriodCount);
            Assert.Equal(windows.Count, item.RollingWindows.Count);
            Assert.Equal(windows.Count, item.MarketRegimeAttribution.Sum(static regime => regime.WindowCount));
            Assert.Equal(64, item.AggregateBenchmarks.Fingerprint.Length);
            Assert.Equal(64, item.FullPeriod.EvaluationFingerprint.Length);
            for (var windowIndex = 0; windowIndex < windows.Count; windowIndex++)
            {
                var window = windows[windowIndex];
                Assert.Equal(dataset.Bars[window.OutOfSampleStartIndex].CloseTimeUtc, item.RollingWindows[windowIndex].StartTimeUtc);
                Assert.Equal(dataset.Bars[window.OutOfSampleStartIndex + window.OutOfSampleLength - 1].CloseTimeUtc, item.RollingWindows[windowIndex].EndTimeUtc);
            }
        });

        var repeated = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, dataset.Bars, options, windows);
        Assert.Equal(
            result.Strategies.Select(static item => item.FullPeriod.EvaluationFingerprint),
            repeated.Strategies.Select(static item => item.FullPeriod.EvaluationFingerprint));

        var trend = result.Strategies.Single(static item => item.StrategyId == "trend-following-btcusdt");
        var oosStart = windows[0].OutOfSampleStartIndex;
        var oosEndExclusive = windows[^1].OutOfSampleStartIndex + windows[^1].OutOfSampleLength;
        var startingEquity = trend.RollingWindows[0].StartingEquity;
        var entryReferencePrice = dataset.Bars[oosStart - 1].ClosePrice.Value;
        var exitReferencePrice = dataset.Bars[oosEndExclusive - 1].ClosePrice.Value;
        var entryPrice = entryReferencePrice * 1.0005m;
        var buyHoldQuantity = startingEquity / (entryPrice * 1.001m);
        var expectedBuyAndHoldFinal = buyHoldQuantity * exitReferencePrice * 0.9995m * 0.999m;
        var expectedBuyAndHoldReturn = expectedBuyAndHoldFinal / startingEquity - 1m;
        Assert.InRange(trend.AggregateBenchmarks.BuyAndHoldNetReturn, expectedBuyAndHoldReturn - 0.000000000000000000000000001m,
            expectedBuyAndHoldReturn + 0.000000000000000000000000001m);
    }

    [Fact]
    public void StrategyMatrixUsesDatasetSymbolRatherThanHardCodedBtcParameters()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var eth = new Symbol("ETHUSDT");
        var ethBars = dataset.Bars.Select(bar => new CloseBar(eth, bar.CloseTimeUtc, bar.ClosePrice.Value)).ToArray();
        var windows = WalkForwardPlan.Create(ethBars.Length, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);
        var options = new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m);

        var result = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, ethBars, options, windows);

        Assert.Equal("ETHUSDT", result.Symbol);
        Assert.Equal(4, result.Strategies.Count);
        Assert.All(result.Strategies, strategy => Assert.Contains("ethusdt", strategy.StrategyId, StringComparison.Ordinal));
    }

    [Fact]
    public void GridSizingIsNormalizedInQuoteUnitsAcrossDifferentBasePrices()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var eth = new Symbol("ETHUSDT");
        var ethBars = dataset.Bars.Select(bar =>
            new CloseBar(eth, bar.CloseTimeUtc, bar.ClosePrice.Value / 20m)).ToArray();
        var btcWindows = WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20);
        var ethWindows = WalkForwardPlan.Create(ethBars.Length, 60, 20, 20);
        var options = new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m);

        var btcReport = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, dataset.Bars, options, btcWindows);
        var ethReport = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, ethBars, options, ethWindows);
        var btcGridBuy = Assert.Single(btcReport.Strategies.Single(item => item.StrategyId == "adaptive-grid-btcusdt")
            .FullPeriod.Trades.Where(static trade => trade.Action == StrategyExecutionAction.BuyBaseQuantity).Take(1));
        var ethGridBuy = Assert.Single(ethReport.Strategies.Single(item => item.StrategyId == "adaptive-grid-ethusdt")
            .FullPeriod.Trades.Where(static trade => trade.Action == StrategyExecutionAction.BuyBaseQuantity).Take(1));

        Assert.InRange(btcGridBuy.BaseQuantity * dataset.Bars[0].ClosePrice.Value, 999.99m, 1_000.01m);
        Assert.InRange(ethGridBuy.BaseQuantity * ethBars[0].ClosePrice.Value, 999.99m, 1_000.01m);
    }

    [Fact]
    public void RejectsOosWindowsThatWouldMakeCompoundedAggregateNonContiguous()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).ToArray();
        var nonContiguousWindows = windows.Where(static window => window.WindowNumber != 2).ToArray();

        Assert.Throws<ArgumentException>(() => StrategyComparisonEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m),
            nonContiguousWindows));
    }

    [Fact]
    public void RejectsNonUniformTrainingOrOutOfSampleWindowLengths()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        WalkForwardWindow[] nonUniformWindows =
        [
            new(1, 0, 60, 60, 20),
            new(2, 20, 60, 80, 10),
            new(3, 30, 60, 90, 20),
        ];

        Assert.Throws<ArgumentException>(() => StrategyComparisonEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m),
            nonUniformWindows));
    }

    [Fact]
    public void MinimumNotionalRejectsResearchGridOrdersBelowSmallQuoteBudget()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20);
        var quoteBudget = 19.92m;
        var options = new StrategyBacktestOptions(
            quoteBudget,
            365,
            0.001m,
            0m,
            0.0005m,
            minimumOrderNotionalQuote: 5m,
            maximumPositionNotionalAtBuyQuote: quoteBudget);

        var result = StrategyComparisonEvaluator.Evaluate(dataset.Metadata, dataset.Bars, options, windows);
        var grid = result.Strategies.Single(static item => item.StrategyId == "adaptive-grid-btcusdt");

        Assert.Equal(0, grid.FullPeriod.Metrics.FilledOrderCount);
        Assert.True(grid.FullPeriod.Metrics.RejectedMinimumNotionalSignalCount > 0);
        Assert.True(grid.FullPeriod.Metrics.HasFixedQuantityStateDivergence);
        Assert.True(grid.FullPeriod.Metrics.HasSignalStateDivergence);
    }

    [Fact]
    public void SensitivityStudyRetainsEveryPreregisteredVariantAndNeverSelectsAWinner()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20);
        var options = new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m);

        var sensitivity = StrategyComparisonEvaluator.EvaluateSensitivity(dataset.Metadata, dataset.Bars, options, windows);

        Assert.False(sensitivity.PromotionAuthorized);
        Assert.False(sensitivity.WinnerSelected);
        Assert.Contains("independently", sensitivity.CapitalAllocationAssumption, StringComparison.Ordinal);
        Assert.Equal(12, sensitivity.Variants.Count);
        Assert.Equal(12, sensitivity.Variants.Select(static variant => variant.StrategyConfigurationFingerprint).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sensitivity.Variants, variant => Assert.Equal(windows.Count, variant.RollingWindows.Count));
    }

    [Fact]
    public void SensitivityMethodologyReportsTheActualRequestedWindowLengths()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, 30, 10, 10);

        var sensitivity = StrategyComparisonEvaluator.EvaluateSensitivity(
            dataset.Metadata,
            dataset.Bars,
            new StrategyBacktestOptions(10_000m, 365, 0.001m, 0m, 0.0005m),
            windows);

        Assert.Contains("30 in-sample / 10 out-of-sample", sensitivity.Methodology, StringComparison.Ordinal);
    }
}
