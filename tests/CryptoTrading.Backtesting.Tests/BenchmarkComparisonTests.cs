using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public void RejectsBenchmarkOptionsWithDifferentExecutionTiming()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var result = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, BacktestEvaluationOptions.DailyCrypto());
        var legacy = new BacktestEvaluationOptions(10_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose);

        Assert.Throws<ArgumentException>(() => BenchmarkComparisonEvaluator.Compare(result, bars, legacy));
    }

    [Fact]
    public void RejectsBenchmarkOptionsWithDifferentInitialCapital()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var result = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, new BacktestEvaluationOptions(1_000m, 365));

        Assert.Throws<ArgumentException>(() => BenchmarkComparisonEvaluator.Compare(result, bars, new BacktestEvaluationOptions(10_000m, 365)));
    }

    [Fact]
    public void RejectsDifferentPricesOnTheSameTimeline()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var options = BacktestEvaluationOptions.DailyCrypto();
        var result = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, options);
        var changed = bars.ToArray();
        changed[0] = new CloseBar(bars[0].Symbol, bars[0].CloseTimeUtc, bars[0].ClosePrice.Value + 1m);

        Assert.Throws<ArgumentException>(() => BenchmarkComparisonEvaluator.Compare(result, changed, options));
    }

    [Fact]
    public void LiquidatedBuyAndHoldDrawdownIncludesExitFee()
    {
        var (_, source) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var bars = source.Take(3).Select(bar => new CloseBar(bar.Symbol, bar.CloseTimeUtc, 100m)).ToArray();
        var result = BenchmarkComparisonEvaluator.CompareOutOfSample("test", 1_000m, 1_000m, 0m, 0m, bars.Length, bars, feeRate: 0.01m);

        Assert.Equal((1_000m - result.BuyAndHold.FinalEquity) / 1_000m, result.BuyAndHold.MaximumDrawdown);
    }

    [Fact]
    public void IndividualBenchmarkFingerprintIncludesAssumptionsEvenWhenReturnsMatch()
    {
        var (_, source) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var bars = source.Take(6).Select(bar => new CloseBar(bar.Symbol, bar.CloseTimeUtc, 100m)).ToArray();
        var options = new BacktestEvaluationOptions(1_000m, 365);
        var first = TrendFollowingBacktestEvaluator.Evaluate(bars, new TrendFollowingParameters("test", "1", bars[0].Symbol, 2, 3, 0m, 0m), options);
        var second = TrendFollowingBacktestEvaluator.Evaluate(bars, new TrendFollowingParameters("test", "1", bars[0].Symbol, 3, 4, 0m, 0m), options);

        Assert.Equal(first.Metrics.NetReturn, second.Metrics.NetReturn);
        Assert.NotEqual(BenchmarkComparisonEvaluator.Compare(first, bars, options).Fingerprint,
            BenchmarkComparisonEvaluator.Compare(second, bars, options).Fingerprint);
    }

    [Fact]
    public void ComparisonFingerprintMatchesCanonicalHistoricalJsonShape()
    {
        var (_, bars) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var options = BacktestEvaluationOptions.DailyCrypto();
        var result = TrendFollowingBacktestEvaluator.Evaluate(bars, TrendFollowingParameters.BtcUsdtBaseline, options);
        var comparison = BenchmarkComparisonEvaluator.Compare(result, bars, options);
        var json = JsonSerializer.Serialize(new
        {
            strategy = comparison.Strategy,
            no_trade = comparison.NoTradeCash,
            buy_hold = comparison.BuyAndHold
        });

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), comparison.Fingerprint);
    }

    [Theory]
    [InlineData("strategy/\u00e9")]
    [InlineData("strategy/quote\"slash\\line\n")]
    public void GeneratedBenchmarkSerializationPreservesCanonicalEscaping(string name)
    {
        var (_, source) = SyntheticMarketDataset.CreateBtcUsdtV1();
        var bars = source.Take(3).ToArray();
        var comparison = BenchmarkComparisonEvaluator.CompareOutOfSample(name, 1_000m, 1_001m, 0.001m, 0m, bars.Length, bars);
        var json = JsonSerializer.Serialize(new { strategy = comparison.Strategy, no_trade = comparison.NoTradeCash, buy_hold = comparison.BuyAndHold });

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), comparison.Fingerprint);
    }

    [Fact]
    public void AggregateFingerprintMatchesCanonicalHistoricalJsonShape()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray();
        var evaluation = WalkForwardEvaluator.Evaluate(dataset.Metadata, dataset.Bars,
            TrendFollowingParameters.BtcUsdtBaseline, BacktestEvaluationOptions.DailyCrypto(), windows);
        var summary = BenchmarkComparisonEvaluator.AggregateOutOfSample(evaluation.Windows);
        var json = JsonSerializer.Serialize(new
        {
            windows = evaluation.Windows.Select(window => new { window = window.Window, benchmark = window.Benchmark.Fingerprint }),
            strategy_return = summary.StrategyNetReturn,
            no_trade_return = summary.NoTradeCashNetReturn,
            buy_hold_return = summary.BuyAndHoldNetReturn
        });

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), summary.Fingerprint);
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
