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
