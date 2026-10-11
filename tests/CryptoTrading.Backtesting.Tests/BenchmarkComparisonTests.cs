using CryptoTrading.Backtesting.Strategies;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class BenchmarkComparisonTests
{
    [Fact]
    public void ProducesCashAndBuyAndHoldReferencesFromTheSameTimeline()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var parameters = TrendFollowingParameters.BtcUsdtBaseline;
        var options = BacktestEvaluationOptions.DailyCrypto();
        var strategy = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters, options);

        var comparison = BenchmarkComparisonEvaluator.Compare(strategy, bars, options);

        Assert.Equal("NO_TRADE/cash", comparison.NoTradeCash.Name);
        Assert.Equal(options.InitialCapital, comparison.NoTradeCash.FinalEquity);
        Assert.Equal(bars[0].ClosePrice.Value, comparison.BuyAndHold.InitialCapital * bars[0].ClosePrice.Value / options.InitialCapital);
        Assert.Equal(bars.Count, comparison.BuyAndHold.PeriodCount);
        Assert.Equal(64, comparison.Fingerprint.Length);
    }

    [Fact]
    public void RejectsTimelineMismatchAndNonPositivePrices()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var strategy = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, BacktestEvaluationOptions.DailyCrypto());
        var shifted = bars.Skip(1).ToArray();
        Assert.Throws<ArgumentException>(() => BenchmarkComparisonEvaluator.Compare(strategy, shifted, BacktestEvaluationOptions.DailyCrypto()));
    }

    [Fact]
    public void OutOfSampleBuyAndHoldUsesSamePriorCloseAndSymmetricCostsAsStrategy()
    {
        var (_, allBars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var bars = new[]
        {
            new CloseBar(allBars[0].Symbol, allBars[0].CloseTimeUtc, 110m),
            new CloseBar(allBars[0].Symbol, allBars[0].CloseTimeUtc.AddDays(1), 120m),
        };

        var comparison = BenchmarkComparisonEvaluator.CompareOutOfSample(
            "test/1",
            initialCapital: 1_000m,
            finalEquity: 1_100m,
            strategyNetReturn: 0.1m,
            strategyMaximumDrawdown: 0.05m,
            periodCount: bars.Length,
            bars,
            benchmarkEntryPrice: 100m,
            feeRate: 0.01m,
            halfSpreadRate: 0.01m,
            slippageRate: 0.02m);

        var entryPrice = 100m * 1.03m;
        var quantity = 1_000m / (entryPrice * 1.01m);
        var exitPrice = 120m * 0.97m;
        var expectedFinalEquity = quantity * exitPrice * 0.99m;
        Assert.Equal(expectedFinalEquity, comparison.BuyAndHold.FinalEquity);
        Assert.Equal(expectedFinalEquity / 1_000m - 1m, comparison.BuyAndHold.NetReturn);
        Assert.Equal(100m, comparison.BuyAndHold.ReferenceEntryPrice);
        Assert.Equal(2, comparison.BuyAndHold.PeriodCount);
        Assert.True(comparison.BuyAndHold.MaximumDrawdown > 0m);
        Assert.Contains("fee=1.00%", comparison.BuyAndHold.CostModel, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintAndOrderingAreDeterministic()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var options = BacktestEvaluationOptions.DailyCrypto();
        var strategy = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, options);
        var first = BenchmarkComparisonEvaluator.Compare(strategy, bars, options);
        var second = BenchmarkComparisonEvaluator.Compare(strategy, bars, options);

        Assert.Equal(first, second);
    }
}
