using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class TrendFollowingBacktestTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void SignalUsesOnlyFollowingBarPriceAndTradeTimestamp()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 60m, 70m);
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);

        var result = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters, new BacktestEvaluationOptions(1_000m, 365));

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(4, result.Trades[0].BarIndex);
        Assert.Equal(bars[4].CloseTimeUtc, result.Trades[0].CloseTimeUtc);
        Assert.Equal(130m, result.Trades[0].ReferencePrice);
        Assert.Equal(6, result.Trades[1].BarIndex);
        Assert.Equal(70m, result.Trades[1].ReferencePrice);
    }

    [Fact]
    public void SignalOnFinalBarHasNoFuturePriceAndCannotFill()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = TrendFollowingBacktestEvaluator.Evaluate(Bars(100m, 90m, 80m, 120m), parameters, new BacktestEvaluationOptions(1_000m, 365));

        Assert.Empty(result.Trades);
        Assert.Equal(1_000m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void WalkForwardAttributesFirstOutOfSampleFillToItsActualBar()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 60m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 1)]);

        Assert.Equal(BacktestExecutionTiming.NextBarClose, result.ExecutionTiming);
        Assert.Equal(1, Assert.Single(result.Windows).TradeCount);
    }

    [Fact]
    public void CausalBenchmarkStartsAtFirstExecutableOosClose()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 240m, 240m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);

        Assert.InRange(result.Windows[0].NetReturn, -0.00000000000000000000000001m, 0.00000000000000000000000001m);
        Assert.InRange(result.Windows[0].Benchmark.BuyAndHold.NetReturn, -0.00000000000000000000000001m, 0.00000000000000000000000001m);
        Assert.Equal(240m, result.Windows[0].Benchmark.BuyAndHold.ReferenceEntryPrice);
    }

    [Fact]
    public void CausalBenchmarkAndOpenStrategyPayOnlyEntryCostsAtTheSameMark()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 240m, 240m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.01m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);

        Assert.Equal(result.Windows[0].NetReturn, result.Windows[0].Benchmark.BuyAndHold.NetReturn);
        Assert.Equal(result.Windows[0].Benchmark.BuyAndHold.NetReturn, result.GetBenchmarkSummary().BuyAndHoldNetReturn);
    }

    [Fact]
    public void CannotCompoundIndependentWindowsWhenAnEarlierPositionWasNotClosed()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 140m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 0, 4, 4, 1), new WalkForwardWindow(2, 0, 5, 5, 1) };
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters, new BacktestEvaluationOptions(1_000m, 365), windows);

        Assert.Throws<InvalidOperationException>(() => result.GetBenchmarkSummary());
        Assert.Throws<InvalidOperationException>(() => BenchmarkComparisonEvaluator.AggregateOutOfSample(result.Windows));
        var relabeled = result with { ExecutionTiming = BacktestExecutionTiming.LegacySignalBarClose };
        Assert.Throws<InvalidOperationException>(() => relabeled.GetBenchmarkSummary());
        var unknown = result.Windows.ToArray();
        unknown[0] = unknown[0] with { EndPositionOpen = null };
        Assert.Throws<InvalidOperationException>(() => BenchmarkComparisonEvaluator.AggregateOutOfSample(unknown));
    }

    [Fact]
    public void EvidenceFingerprintDistinguishesOpenAndClosedWindowEndState()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);
        var open = result with { Windows = [result.Windows[0] with { EndPositionOpen = true }] };

        Assert.Equal(result.Windows[0].NetReturn, open.Windows[0].NetReturn);
        Assert.Throws<InvalidOperationException>(() => open.GetBenchmarkSummary());
    }

    [Fact]
    public void EvidenceRejectsBenchmarkContentsChangedWithoutUpdatingFingerprint()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);
        var window = result.Windows[0];
        var changedBenchmark = window.Benchmark with
        {
            BuyAndHold = window.Benchmark.BuyAndHold with { ReferenceExitPrice = 200m }
        };
        var altered = result with { Windows = [window with { Benchmark = changedBenchmark }] };

        Assert.Throws<InvalidOperationException>(() => altered.GetBenchmarkSummary());
        Assert.Throws<InvalidOperationException>(() => BenchmarkComparisonEvaluator.AggregateOutOfSample(altered.Windows));
    }

    [Fact]
    public void EvidenceRejectsUnboundWindowMetrics()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);
        var changed = result with { Windows = [result.Windows[0] with { MaximumDrawdown = 0.25m }] };
        var changedCount = result with { Windows = [result.Windows[0] with { PeriodCount = 3 }] };

        Assert.Throws<InvalidOperationException>(() => changed.GetBenchmarkSummary());
        Assert.Throws<InvalidOperationException>(() => changedCount.GetBenchmarkSummary());
    }

    [Fact]
    public void EvidenceFingerprintIncludesEvaluationAssumptionsEvenWithoutTrades()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var windows = new[] { new WalkForwardWindow(1, 0, 4, 4, 2) };
        var options = new BacktestEvaluationOptions(1_000m, 365);
        var first = WalkForwardEvaluator.Evaluate(metadata, bars, new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m), options, windows);
        var second = WalkForwardEvaluator.Evaluate(metadata, bars, new TrendFollowingParameters("test", "1", BtcUsdt, 3, 4, 0m, 0m), options, windows);

        Assert.Equal(first.Windows[0].NetReturn, second.Windows[0].NetReturn);
        Assert.NotEqual(first.GetBenchmarkSummary().Fingerprint, second.GetBenchmarkSummary().Fingerprint);
    }

    [Fact]
    public void EvidenceFingerprintIncludesConsumedPricesEvenWhenMetricsMatch()
    {
        var firstBars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var secondBars = Bars(99m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 10, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 0, 4, 4, 2) };
        var options = new BacktestEvaluationOptions(1_000m, 365);
        var first = WalkForwardEvaluator.Evaluate(metadata, firstBars, parameters, options, windows);
        var second = WalkForwardEvaluator.Evaluate(metadata, secondBars, parameters, options, windows);

        Assert.Equal(first.Windows[0].NetReturn, second.Windows[0].NetReturn);
        Assert.NotEqual(first.GetBenchmarkSummary().Fingerprint, second.GetBenchmarkSummary().Fingerprint);
    }

    [Fact]
    public void EvaluationFingerprintDistinguishesLegacySeparatorCollision()
    {
        var bars = Bars(100m, 100m, 100m, 100m);
        var first = new TrendFollowingParameters("a|b", "c", BtcUsdt, 2, 3, 0m, 0m);
        var second = new TrendFollowingParameters("a", "b|c", BtcUsdt, 2, 3, 0m, 0m);
        var options = new BacktestEvaluationOptions(1_000m, 365);

        Assert.Equal(first.ConfigurationFingerprint, second.ConfigurationFingerprint); // Legacy identity is preserved.
        Assert.NotEqual(TrendFollowingBacktestEvaluator.Evaluate(bars, first, options).EvaluationAssumptionsFingerprint,
            TrendFollowingBacktestEvaluator.Evaluate(bars, second, options).EvaluationAssumptionsFingerprint);
    }

    [Fact]
    public void WalkForwardCannotBeRelabeledAsAnotherStrategyOrConfiguration()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);

        Assert.Throws<InvalidOperationException>(() => (result with { StrategyId = "other" }).GetBenchmarkSummary());
        Assert.Throws<InvalidOperationException>(() => (result with { StrategyVersion = "other" }).GetBenchmarkSummary());
        Assert.Throws<InvalidOperationException>(() => (result with { StrategyConfigurationFingerprint = new string('A', 64) }).GetBenchmarkSummary());
    }

    [Fact]
    public void PublicWindowAggregatorRejectsMetricsInconsistentWithItsBenchmark()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 4, 4, 2)]);
        var window = result.Windows[0];
        var inconsistent = new[]
        {
            window with { StartingEquity = 2_000m }, window with { FinalEquity = 2_000m },
            window with { NetReturn = 0.99m }, window with { MaximumDrawdown = 0.5m }, window with { PeriodCount = 3 }
        };
        foreach (var item in inconsistent)
        {
            Assert.Throws<InvalidOperationException>(() => BenchmarkComparisonEvaluator.AggregateOutOfSample([item]));
        }
    }

    [Fact]
    public void SelectingWindowsAfterEvaluationCannotReuseOriginalObservedSeriesIdentity()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 10, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 0, 4, 4, 1), new WalkForwardWindow(2, 0, 5, 5, 1) };
        var result = WalkForwardEvaluator.Evaluate(metadata, bars, parameters, new BacktestEvaluationOptions(1_000m, 365), windows);
        var subset = result with { Windows = [result.Windows[0]] };

        Assert.Throws<InvalidOperationException>(() => subset.GetBenchmarkSummary());
        var stripped = subset with { ObservedSeriesFingerprint = null, ObservedSeriesFingerprintVersion = null };
        Assert.Throws<InvalidOperationException>(() => stripped.GetBenchmarkSummary());
        var alteredWindows = result.Windows.ToArray();
        alteredWindows[0] = alteredWindows[0] with { NetReturn = alteredWindows[0].NetReturn + 0.1m };
        Assert.Throws<InvalidOperationException>(() => (result with { Windows = alteredWindows }).GetBenchmarkSummary());
    }

    [Fact]
    public void HistoricalWalkForwardWithoutNewFingerprintPreservesLegacySummaryIdentity()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var generated = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose),
            [new WalkForwardWindow(1, 0, 4, 4, 2)]);
        var legacy = generated with
        {
            EvaluationAssumptionsFingerprint = null,
            ObservedSeriesFingerprint = null,
            ObservedSeriesFingerprintVersion = null,
            Windows = generated.Windows.Select(window => window with { ObservedWindowFingerprint = null }).ToArray()
        };

        Assert.Equal(BenchmarkComparisonEvaluator.AggregateOutOfSample(legacy.Windows).Fingerprint, legacy.GetBenchmarkSummary().Fingerprint);
    }

    [Fact]
    public void OriginalThreeParameterOptionsConstructorStillExists()
    {
        var original = typeof(BacktestEvaluationOptions).GetConstructor([typeof(decimal), typeof(int), typeof(decimal)]);
        Assert.NotNull(original);
        Assert.Equal(BacktestExecutionTiming.NextBarClose, new BacktestEvaluationOptions(1_000m, 365, 0m).ExecutionTiming);
    }

    [Fact]
    public void WalkForwardDoesNotUsePriceAfterWindowForLastBarSignal()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 60m);
        var changed = Bars(100m, 90m, 80m, 120m, 900m, 1m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 0, 3, 3, 1) };
        var first = WalkForwardEvaluator.Evaluate(metadata, bars, parameters, new BacktestEvaluationOptions(1_000m, 365), windows);
        var second = WalkForwardEvaluator.Evaluate(metadata, changed, parameters, new BacktestEvaluationOptions(1_000m, 365), windows);

        Assert.Equal(0, first.Windows[0].TradeCount);
        Assert.Equal(first.Windows[0].FinalEquity, second.Windows[0].FinalEquity);
        Assert.Equal(first.Windows[0].Benchmark.Fingerprint, second.Windows[0].Benchmark.Fingerprint);
        Assert.Equal(first.GetBenchmarkSummary().Fingerprint, second.GetBenchmarkSummary().Fingerprint);
    }

    [Fact]
    public void MissingTimingOnHistoricalResultRemainsLegacy()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m);
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var current = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters, new BacktestEvaluationOptions(1_000m, 365));
        var historical = new BacktestEvaluationResult(current.StrategyId, current.StrategyVersion,
            current.StrategyConfigurationFingerprint, current.Metrics, current.EquityCurve, current.Trades);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var historicalWalkForward = new WalkForwardEvaluationResult(metadata, current.StrategyId, current.StrategyVersion,
            current.StrategyConfigurationFingerprint, []);

        Assert.Equal(BacktestExecutionTiming.LegacySignalBarClose, historical.ExecutionTiming);
        Assert.Equal(BacktestExecutionTiming.LegacySignalBarClose, historicalWalkForward.ExecutionTiming);
        Assert.Throws<ArgumentException>(() => BenchmarkComparisonEvaluator.Compare(historical, bars, new BacktestEvaluationOptions(1_000m, 365)));
    }

    [Fact]
    public void DeserializedHistoricalResultsWithoutTimingRemainLegacyAndUnfingerprinted()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m);
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var current = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters, new BacktestEvaluationOptions(1_000m, 365));
        var historicalJson = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        historicalJson.Remove(nameof(BacktestEvaluationResult.ExecutionTiming));
        historicalJson.Remove(nameof(BacktestEvaluationResult.EvaluationAssumptionsFingerprint));
        var restored = historicalJson.Deserialize<BacktestEvaluationResult>();

        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var walkForward = new WalkForwardEvaluationResult(metadata, current.StrategyId, current.StrategyVersion,
            current.StrategyConfigurationFingerprint, []);
        var walkForwardJson = JsonNode.Parse(JsonSerializer.Serialize(walkForward))!.AsObject();
        walkForwardJson.Remove(nameof(WalkForwardEvaluationResult.ExecutionTiming));
        walkForwardJson.Remove(nameof(WalkForwardEvaluationResult.EvaluationAssumptionsFingerprint));
        var restoredWalkForward = walkForwardJson.Deserialize<WalkForwardEvaluationResult>();

        Assert.NotNull(restored);
        Assert.Equal(BacktestExecutionTiming.LegacySignalBarClose, restored.ExecutionTiming);
        Assert.Null(restored.EvaluationAssumptionsFingerprint);
        Assert.NotNull(restoredWalkForward);
        Assert.Equal(BacktestExecutionTiming.LegacySignalBarClose, restoredWalkForward.ExecutionTiming);
        Assert.Null(restoredWalkForward.EvaluationAssumptionsFingerprint);
    }

    [Fact]
    public void WalkForwardUsesOnlyDeclaredWarmupAndStartsOutOfSampleInCash()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 140m, 150m, 160m);
        var changedBeforeTraining = Bars(1_000m, 1m, 80m, 120m, 130m, 140m, 150m, 160m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 2, 4, 6, 2) };
        var options = new BacktestEvaluationOptions(1_000m, 365);

        var original = WalkForwardEvaluator.Evaluate(metadata, bars, parameters, options, windows);
        var changed = WalkForwardEvaluator.Evaluate(metadata, changedBeforeTraining, parameters, options, windows);

        Assert.Equal(1_000m, original.Windows[0].StartingEquity);
        Assert.Equal(1_000m, original.Windows[0].FinalEquity);
        Assert.Equal(0, original.Windows[0].TradeCount);
        Assert.Equal(original.Windows[0].Benchmark.Fingerprint, changed.Windows[0].Benchmark.Fingerprint);
    }

    [Fact]
    public void CausalWalkForwardRejectsUndefinedGapBetweenTrainingAndTest()
    {
        var bars = Bars(100m, 90m, 80m, 120m, 130m, 140m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);

        Assert.Throws<ArgumentException>(() => WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365), [new WalkForwardWindow(1, 0, 3, 4, 2)]));
    }

    [Fact]
    public void WalkForwardFingerprintDistinguishesExecutionAssumptionsEvenWithoutTrades()
    {
        var bars = Bars(100m, 100m, 100m, 100m, 100m);
        var metadata = SyntheticMarketDataset.CreateBtcUsdtV1().Metadata;
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var windows = new[] { new WalkForwardWindow(1, 0, 3, 3, 2) };
        var next = WalkForwardEvaluator.Evaluate(metadata, bars, parameters, new BacktestEvaluationOptions(1_000m, 365), windows);
        var legacy = WalkForwardEvaluator.Evaluate(metadata, bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose), windows);

        Assert.Equal(BacktestExecutionTiming.LegacySignalBarClose, legacy.ExecutionTiming);
        Assert.NotEqual(next.Windows[0].Benchmark.Fingerprint, legacy.Windows[0].Benchmark.Fingerprint);
    }

    [Fact]
    public void EvaluationFingerprintSeparatesTimingFromStrategyParameters()
    {
        var bars = Bars(100m, 100m, 100m, 100m);
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var next = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters, new BacktestEvaluationOptions(1_000m, 365));
        var legacy = TrendFollowingBacktestEvaluator.Evaluate(bars, parameters,
            new BacktestEvaluationOptions(1_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose));

        Assert.Equal(next.StrategyConfigurationFingerprint, legacy.StrategyConfigurationFingerprint);
        Assert.NotNull(next.EvaluationAssumptionsFingerprint);
        Assert.NotEqual(next.EvaluationAssumptionsFingerprint, legacy.EvaluationAssumptionsFingerprint);
    }

    [Fact]
    public void EvaluatorProducesDeterministicNetReturnTurnoverAndDrawdown()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var result = TrendFollowingBacktestEvaluator.Evaluate(
            Bars(100m, 90m, 80m, 120m, 130m, 60m),
            parameters,
            new BacktestEvaluationOptions(1_000m, periodsPerYear: 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose));

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
            new BacktestEvaluationOptions(1_000m, periodsPerYear: 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose));

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

    [Fact]
    public void SyntheticDatasetAndWalkForwardEvaluationAreReproducible()
    {
        var first = SyntheticMarketDataset.CreateBtcUsdtV1();
        var second = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(first.Bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);

        var result = WalkForwardEvaluator.Evaluate(
            first.Metadata,
            first.Bars,
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto(),
            windows);

        Assert.Equal("synthetic-btcusdt-oos", first.Metadata.DatasetId);
        Assert.Equal("1.0.0", first.Metadata.Version);
        Assert.Equal(first.Metadata.ContentHash, second.Metadata.ContentHash);
        Assert.Equal(first.Bars.Select(bar => bar.ClosePrice.Value), second.Bars.Select(bar => bar.ClosePrice.Value));
        Assert.Equal(6, result.Windows.Count);
        Assert.All(result.Windows, window => Assert.Equal(20, window.PeriodCount));
        Assert.All(result.Windows, window => Assert.InRange(window.NetReturn, -1m, 1m));
        Assert.All(result.Windows, window =>
        {
            Assert.Equal("NO_TRADE/cash", window.Benchmark.NoTradeCash.Name);
            Assert.Equal("buy-and-hold", window.Benchmark.BuyAndHold.Name);
            Assert.Equal(64, window.Benchmark.Fingerprint.Length);
        });
        Assert.Contains(result.Windows.Take(result.Windows.Count - 1), window => window.EndPositionOpen == true);
        Assert.Throws<InvalidOperationException>(() => result.GetBenchmarkSummary());
        var singleWindowResult = WalkForwardEvaluator.Evaluate(
            first.Metadata, first.Bars, TrendFollowingParameters.BtcUsdtBaseline, BacktestEvaluationOptions.DailyCrypto(),
            windows.Take(1).ToArray());
        var summary = singleWindowResult.GetBenchmarkSummary();
        Assert.Equal(64, summary.Fingerprint.Length);
        Assert.Equal(0m, summary.NoTradeCashNetReturn);
    }

    [Fact]
    public void WalkForwardBenchmarksDoNotReadBarsAfterTheWindow()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);
        var original = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto(),
            windows);

        var changedFuture = dataset.Bars.ToArray();
        for (var index = windows[0].OutOfSampleStartIndex + windows[0].OutOfSampleLength; index < changedFuture.Length; index++)
        {
            var bar = changedFuture[index];
            changedFuture[index] = new CloseBar(bar.Symbol, bar.CloseTimeUtc, bar.ClosePrice.Value * 3m);
        }

        var replay = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            changedFuture,
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto(),
            windows);

        Assert.Equal(original.Windows[0].Benchmark.Fingerprint, replay.Windows[0].Benchmark.Fingerprint);
        Assert.Equal(original.Windows[0].Benchmark.BuyAndHold.NetReturn, replay.Windows[0].Benchmark.BuyAndHold.NetReturn);
    }

    [Fact]
    public void WalkForwardUsesTrainingOnlyAsWarmupAndRejectsOutOfRangeWindows()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var window = new WalkForwardWindow(1, 0, 60, 60, 20);

        var result = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto(),
            [window]);

        Assert.Equal(window, result.Windows[0].Window);
        Assert.Equal(20, result.Windows[0].PeriodCount);
        Assert.Throws<ArgumentException>(() => WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            TrendFollowingParameters.BtcUsdtBaseline,
            BacktestEvaluationOptions.DailyCrypto(),
            [new WalkForwardWindow(1, 0, 60, 170, 20)]));
    }

    private static CloseBar[] Bars(params decimal[] prices) =>
        prices.Select((price, index) => new CloseBar(
                BtcUsdt,
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                price))
            .ToArray();
}
