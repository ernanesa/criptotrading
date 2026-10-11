using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting;

public sealed record StrategyComparisonStrategyResult(
    string StrategyId,
    string StrategyVersion,
    string StrategyConfigurationFingerprint,
    string DatasetContentHash,
    StrategyBacktestEvaluationResult FullPeriod,
    IReadOnlyList<StrategyComparisonWindowResult> RollingWindows,
    IReadOnlyList<StrategyMarketRegimeAttribution> MarketRegimeAttribution,
    WalkForwardBenchmarkSummary AggregateBenchmarks);

public enum StrategyMarketRegime
{
    Up,
    Down,
    Flat,
}

/// <summary>Ex-post attribution using each score window's Buy-and-Hold return sign.</summary>
public sealed record StrategyMarketRegimeAttribution(
    StrategyMarketRegime Regime,
    int WindowCount,
    decimal AverageStrategyReturn,
    decimal MedianStrategyReturn,
    decimal AverageBuyAndHoldReturn,
    int WindowsOutperformingBuyAndHold,
    decimal AverageMaximumDrawdown);

public sealed record StrategyComparisonWindowResult(
    WalkForwardWindow Window,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    decimal StartingEquity,
    decimal FinalEquity,
    decimal NetReturn,
    decimal MaximumDrawdown,
    decimal TotalFees,
    decimal TotalSpreadCost,
    decimal TotalSlippageCost,
    int TradeCount,
    int PartialFillCount,
    int PeriodCount,
    BenchmarkComparison Benchmark);

public sealed record StrategyComparisonReport(
    DatasetMetadata Dataset,
    string Symbol,
    int BarCount,
    string ExecutionAssumption,
    string Methodology,
    string PositionSizingAssumption,
    string CapitalAllocationAssumption,
    bool PromotionAuthorized,
    IReadOnlyList<StrategyComparisonStrategyResult> Strategies);

public sealed record StrategySensitivityReport(
    string DatasetContentHash,
    string Symbol,
    string Methodology,
    string CapitalAllocationAssumption,
    bool PromotionAuthorized,
    bool WinnerSelected,
    IReadOnlyList<StrategyComparisonStrategyResult> Variants);

/// <summary>
/// Runs fixed, versioned baseline parameters for all implemented strategies on
/// the same chronological bars. It never fits parameters, chooses a winner,
/// accesses an exchange, or authorizes promotion.
/// </summary>
public static class StrategyComparisonEvaluator
{
    public static StrategyComparisonReport Evaluate(
        DatasetMetadata dataset,
        IReadOnlyList<CloseBar> bars,
        StrategyBacktestOptions options,
        IReadOnlyList<WalkForwardWindow> windows)
    {
        var (symbol, referencePrice) = ValidateComparisonInputs(dataset, bars, options, windows);
        var definitions = CreateBaselineDefinitions(symbol, options, referencePrice);
        var results = definitions
            .Select(definition => EvaluateDefinition(dataset.ContentHash, bars, options, windows, definition))
            .ToArray();

        return new StrategyComparisonReport(
            dataset,
            symbol.Value,
            bars.Count,
            StrategyBacktestEvaluator.DescribeExecutionAssumption(options),
            $"Fixed baseline parameters; {DescribeWindowMethodology(windows)}; no fitting, tuning or winner selection. Regime labels are assigned retrospectively by each window's Buy-and-Hold return sign.",
            "Signal strategies allocate available quote cash on entry; grid uses 10% normalized quote capital per level and caps inventory at 50%, based on the first close. Research sizing only; not an active risk policy.",
            "Each strategy is simulated independently with the full initial quote capital; results are not concurrent allocations and must not be summed as a portfolio.",
            PromotionAuthorized: false,
            results.OrderBy(static result => result.StrategyId, StringComparer.Ordinal).ToArray());
    }

    public static StrategySensitivityReport EvaluateSensitivity(
        DatasetMetadata dataset,
        IReadOnlyList<CloseBar> bars,
        StrategyBacktestOptions options,
        IReadOnlyList<WalkForwardWindow> windows)
    {
        var (symbol, referencePrice) = ValidateComparisonInputs(dataset, bars, options, windows);
        var definitions = CreateSensitivityDefinitions(symbol, options, referencePrice);
        var variants = definitions
            .Select(definition => EvaluateDefinition(dataset.ContentHash, bars, options, windows, definition))
            .OrderBy(static result => result.StrategyId, StringComparer.Ordinal)
            .ToArray();
        return new StrategySensitivityReport(
            dataset.ContentHash,
            symbol.Value,
            $"Fixed-in-code local perturbation grid around four baselines; fixed parameters per run; {DescribeWindowMethodology(windows)}; no in-sample winner chosen and no promotion authority. This is not formally preregistered.",
            "Each variant is simulated independently with the full initial quote capital; results are not concurrent allocations and must not be summed as a portfolio.",
            PromotionAuthorized: false,
            WinnerSelected: false,
            variants);
    }

    private static (Symbol Symbol, decimal ReferencePrice) ValidateComparisonInputs(
        DatasetMetadata dataset,
        IReadOnlyList<CloseBar> bars,
        StrategyBacktestOptions options,
        IReadOnlyList<WalkForwardWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(windows);
        if (bars.Count == 0 || windows.Count == 0)
        {
            throw new ArgumentException("Bars and rolling evaluation windows are required.");
        }

        ValidateWindows(windows, bars.Count);
        var symbol = bars[0].Symbol;
        if (bars.Any(bar => bar.Symbol != symbol))
        {
            throw new ArgumentException("All comparison bars must have the same symbol.", nameof(bars));
        }

        return (symbol, bars[0].ClosePrice.Value);
    }

    private static StrategyComparisonStrategyResult EvaluateDefinition(
        string datasetContentHash,
        IReadOnlyList<CloseBar> bars,
        StrategyBacktestOptions options,
        IReadOnlyList<WalkForwardWindow> windows,
        StrategyDefinition definition)
    {
        var signals = definition.GenerateSignals(bars);
        var fullPeriod = StrategyBacktestEvaluator.Evaluate(
            definition.StrategyId,
            definition.StrategyVersion,
            definition.ConfigurationFingerprint,
            bars,
            signals,
            options);
        var rolling = EvaluateWindows(definition.StrategyId, fullPeriod, bars, windows, options);
        return new(
            definition.StrategyId,
            definition.StrategyVersion,
            definition.ConfigurationFingerprint,
            datasetContentHash,
            fullPeriod,
            rolling,
            CreateMarketRegimeAttribution(rolling),
            BenchmarkComparisonEvaluator.AggregateOutOfSample(rolling));
    }

    private static List<StrategyComparisonWindowResult> EvaluateWindows(
        string strategyName,
        StrategyBacktestEvaluationResult evaluation,
        IReadOnlyList<CloseBar> bars,
        IReadOnlyList<WalkForwardWindow> windows,
        StrategyBacktestOptions options)
    {
        var results = new List<StrategyComparisonWindowResult>(windows.Count);
        foreach (var window in windows)
        {
            var endExclusive = checked(window.OutOfSampleStartIndex + window.OutOfSampleLength);
            var points = evaluation.EquityCurve
                .Skip(window.OutOfSampleStartIndex)
                .Take(window.OutOfSampleLength)
                .ToArray();
            var startingEquity = window.OutOfSampleStartIndex == 0
                ? options.InitialCapital
                : evaluation.EquityCurve[window.OutOfSampleStartIndex - 1].Equity;
            var finalEquity = points[^1].Equity;
            var peak = startingEquity;
            var maximumDrawdown = 0m;
            foreach (var point in points)
            {
                peak = Math.Max(peak, point.Equity);
                maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - point.Equity / peak);
            }

            var trades = evaluation.Trades
                .Where(trade => trade.FillBarIndex >= window.OutOfSampleStartIndex && trade.FillBarIndex < endExclusive)
                .ToArray();
            var netReturn = startingEquity == 0m ? 0m : finalEquity / startingEquity - 1m;
            var oosBars = bars.Skip(window.OutOfSampleStartIndex).Take(window.OutOfSampleLength).ToArray();
            results.Add(new StrategyComparisonWindowResult(
                window,
                oosBars[0].CloseTimeUtc,
                oosBars[^1].CloseTimeUtc,
                startingEquity,
                finalEquity,
                netReturn,
                maximumDrawdown,
                trades.Sum(static trade => trade.Fee),
                trades.Sum(static trade => trade.SpreadCost),
                trades.Sum(static trade => trade.SlippageCost),
                trades.Length,
                trades.Count(static trade => trade.IsPartialFill),
                points.Length,
                BenchmarkComparisonEvaluator.CompareOutOfSample(
                    strategyName,
                    startingEquity,
                    finalEquity,
                    netReturn,
                    maximumDrawdown,
                    points.Length,
                    oosBars,
                    benchmarkEntryPrice: window.OutOfSampleStartIndex == 0
                        ? oosBars[0].ClosePrice.Value
                        : bars[window.OutOfSampleStartIndex - 1].ClosePrice.Value,
                    feeRate: options.FeeRate,
                    halfSpreadRate: options.HalfSpreadRate,
                    slippageRate: options.SlippageRate)));
        }

        return results;
    }

    private static void ValidateWindows(IReadOnlyList<WalkForwardWindow> windows, int barCount)
    {
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            _ = window.Split;
            var endExclusive = checked(window.OutOfSampleStartIndex + window.OutOfSampleLength);
            if (window.OutOfSampleStartIndex < 0 || window.OutOfSampleLength <= 0 || endExclusive > barCount)
            {
                throw new ArgumentException("Rolling evaluation window is outside the dataset.", nameof(windows));
            }

            if (index > 0)
            {
                var previous = windows[index - 1];
                var previousEndExclusive = checked(previous.OutOfSampleStartIndex + previous.OutOfSampleLength);
                if (window.OutOfSampleStartIndex != previousEndExclusive)
                {
                    throw new ArgumentException("Compounded rolling windows must be ordered, contiguous and non-overlapping.", nameof(windows));
                }

                var inSampleStep = window.InSampleStartIndex - previous.InSampleStartIndex;
                var outOfSampleStep = window.OutOfSampleStartIndex - previous.OutOfSampleStartIndex;
                if (window.InSampleLength != windows[0].InSampleLength ||
                    window.OutOfSampleLength != windows[0].OutOfSampleLength ||
                    inSampleStep != outOfSampleStep)
                {
                    throw new ArgumentException("Rolling methodology requires uniform train/test lengths and equal chronological step sizes.", nameof(windows));
                }
            }
        }
    }

    private static StrategyMarketRegimeAttribution[] CreateMarketRegimeAttribution(
        IReadOnlyList<StrategyComparisonWindowResult> windows) => windows
        .GroupBy(static window => window.Benchmark.BuyAndHold.NetReturn > 0m
            ? StrategyMarketRegime.Up
            : window.Benchmark.BuyAndHold.NetReturn < 0m
                ? StrategyMarketRegime.Down
                : StrategyMarketRegime.Flat)
        .OrderBy(static group => group.Key)
        .Select(static group =>
        {
            var strategyReturns = group.Select(static window => window.NetReturn).Order().ToArray();
            var median = strategyReturns.Length % 2 == 0
                ? (strategyReturns[(strategyReturns.Length / 2) - 1] + strategyReturns[strategyReturns.Length / 2]) / 2m
                : strategyReturns[strategyReturns.Length / 2];
            return new StrategyMarketRegimeAttribution(
                group.Key,
                strategyReturns.Length,
                strategyReturns.Average(),
                median,
                group.Average(static window => window.Benchmark.BuyAndHold.NetReturn),
                group.Count(static window => window.NetReturn > window.Benchmark.BuyAndHold.NetReturn),
                group.Average(static window => window.MaximumDrawdown));
        })
        .ToArray();

    private static string DescribeWindowMethodology(IReadOnlyList<WalkForwardWindow> windows)
    {
        var first = windows[0];
        var step = windows.Count > 1
            ? windows[1].OutOfSampleStartIndex - first.OutOfSampleStartIndex
            : first.OutOfSampleLength;
        return $"rolling {first.InSampleLength} in-sample / {first.OutOfSampleLength} out-of-sample, " +
            $"step {step}, scored on one continuous simulation";
    }

    private static IReadOnlyList<StrategyDefinition> CreateBaselineDefinitions(
        Symbol symbol,
        StrategyBacktestOptions options,
        decimal referencePrice)
    {
#pragma warning disable CA1308 // Lowercase is the stable strategy-id suffix format, not an equality operation.
        var suffix = symbol.Value.ToLowerInvariant();
#pragma warning restore CA1308
        var trend = new TrendFollowingParameters($"trend-following-{suffix}", "1.0.0", symbol, 20, 50,
            options.FeeRate, options.SlippageRate);
        var breakout = new BreakoutParameters($"breakout-momentum-{suffix}", "1.0.0", symbol, 20, 10,
            options.FeeRate, options.SlippageRate);
        var meanReversion = new MeanReversionParameters($"mean-reversion-{suffix}", "1.0.0", symbol, 20,
            -1.8m, 0m, 0.05m, options.FeeRate, options.SlippageRate);
        const decimal gridQuoteOrderFraction = 0.10m;
        const decimal gridQuoteInventoryFraction = 0.50m;
        var grid = new AdaptiveGridParameters(
            $"adaptive-grid-{suffix}",
            "1.0.0",
            symbol,
            0.015m,
            5,
            (options.InitialCapital * gridQuoteOrderFraction) / referencePrice,
            (options.InitialCapital * gridQuoteInventoryFraction) / referencePrice,
            options.FeeRate,
            options.SlippageRate);

        return
        [
            new(trend.StrategyId, trend.Version, trend.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromTrendFollowing(new TrendFollowingStrategy(trend).Evaluate(source))),
            new(breakout.StrategyId, breakout.Version, breakout.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromBreakout(new BreakoutStrategy(breakout).Evaluate(source))),
            new(meanReversion.StrategyId, meanReversion.Version, meanReversion.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromMeanReversion(new MeanReversionStrategy(meanReversion).Evaluate(source))),
            new(grid.StrategyId, grid.Version, grid.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromAdaptiveGrid(new AdaptiveGridStrategy(grid).Evaluate(source))),
        ];
    }

    private static StrategyDefinition[] CreateSensitivityDefinitions(
        Symbol symbol,
        StrategyBacktestOptions options,
        decimal referencePrice)
    {
#pragma warning disable CA1308 // Lowercase is the stable strategy-id suffix format, not an equality operation.
        var suffix = symbol.Value.ToLowerInvariant();
#pragma warning restore CA1308
        const string version = "1.0.0";
        const decimal gridQuoteOrderFraction = 0.10m;
        const decimal gridQuoteInventoryFraction = 0.50m;

        StrategyDefinition Trend(int fast, int slow, string label)
        {
            var parameters = new TrendFollowingParameters(
                $"trend-following-{suffix}-{label}", version, symbol, fast, slow, options.FeeRate, options.SlippageRate);
            return new(parameters.StrategyId, parameters.Version, parameters.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromTrendFollowing(new TrendFollowingStrategy(parameters).Evaluate(source)));
        }

        StrategyDefinition Breakout(int lookback, int confirmation, string label)
        {
            var parameters = new BreakoutParameters(
                $"breakout-momentum-{suffix}-{label}", version, symbol, lookback, confirmation, options.FeeRate, options.SlippageRate);
            return new(parameters.StrategyId, parameters.Version, parameters.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromBreakout(new BreakoutStrategy(parameters).Evaluate(source)));
        }

        StrategyDefinition MeanReversion(int lookback, decimal entryZScore, string label)
        {
            var parameters = new MeanReversionParameters(
                $"mean-reversion-{suffix}-{label}", version, symbol, lookback, entryZScore, 0m, 0.05m,
                options.FeeRate, options.SlippageRate);
            return new(parameters.StrategyId, parameters.Version, parameters.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromMeanReversion(new MeanReversionStrategy(parameters).Evaluate(source)));
        }

        StrategyDefinition Grid(decimal spacing, int levels, string label)
        {
            var parameters = new AdaptiveGridParameters(
                $"adaptive-grid-{suffix}-{label}", version, symbol, spacing, levels,
                (options.InitialCapital * gridQuoteOrderFraction) / referencePrice,
                (options.InitialCapital * gridQuoteInventoryFraction) / referencePrice,
                options.FeeRate,
                options.SlippageRate);
            return new(parameters.StrategyId, parameters.Version, parameters.ConfigurationFingerprint,
                source => StrategySignalAdapter.FromAdaptiveGrid(new AdaptiveGridStrategy(parameters).Evaluate(source)));
        }

        return
        [
            Trend(20, 50, "baseline-20-50"),
            Trend(10, 50, "fast-10-slow-50"),
            Trend(20, 100, "fast-20-slow-100"),
            Breakout(20, 10, "baseline-20-10"),
            Breakout(10, 5, "lookback-10-confirm-5"),
            Breakout(40, 10, "lookback-40-confirm-10"),
            MeanReversion(20, -1.8m, "baseline-20-z-1-8"),
            MeanReversion(10, -1.5m, "lookback-10-z-1-5"),
            MeanReversion(30, -2.5m, "lookback-30-z-2-5"),
            Grid(0.015m, 5, "baseline-spacing-1-5-levels-5"),
            Grid(0.01m, 3, "spacing-1-levels-3"),
            Grid(0.02m, 5, "spacing-2-levels-5"),
        ];
    }

    private sealed record StrategyDefinition(
        string StrategyId,
        string StrategyVersion,
        string ConfigurationFingerprint,
        Func<IReadOnlyList<CloseBar>, IReadOnlyList<StrategyExecutionSignal>> GenerateSignals);
}

public static class StrategySignalAdapter
{
    public static StrategyExecutionSignal[] FromTrendFollowing(IReadOnlyList<TrendFollowingDecision> decisions) =>
        decisions.Select(static decision => new StrategyExecutionSignal(
            decision.BarIndex,
            decision.Signal switch
            {
                TrendFollowingSignal.NoTrade => StrategyExecutionAction.NoTrade,
                TrendFollowingSignal.EnterLong => StrategyExecutionAction.EnterLong,
                TrendFollowingSignal.ExitLong => StrategyExecutionAction.ExitLong,
                _ => throw new ArgumentOutOfRangeException(nameof(decisions)),
            },
            reasonCode: decision.Reason.ToString())).ToArray();

    public static StrategyExecutionSignal[] FromBreakout(IReadOnlyList<BreakoutDecision> decisions) =>
        decisions.Select(static decision => new StrategyExecutionSignal(
            decision.BarIndex,
            decision.Signal switch
            {
                BreakoutSignal.NoTrade => StrategyExecutionAction.NoTrade,
                BreakoutSignal.EnterLong => StrategyExecutionAction.EnterLong,
                BreakoutSignal.ExitLong => StrategyExecutionAction.ExitLong,
                _ => throw new ArgumentOutOfRangeException(nameof(decisions)),
            },
            reasonCode: decision.Reason.ToString())).ToArray();

    public static StrategyExecutionSignal[] FromMeanReversion(IReadOnlyList<MeanReversionDecision> decisions) =>
        decisions.Select(static decision => new StrategyExecutionSignal(
            decision.BarIndex,
            decision.Signal switch
            {
                MeanReversionSignal.NoTrade => StrategyExecutionAction.NoTrade,
                MeanReversionSignal.EnterLong => StrategyExecutionAction.EnterLong,
                MeanReversionSignal.ExitLong => StrategyExecutionAction.ExitLong,
                _ => throw new ArgumentOutOfRangeException(nameof(decisions)),
            },
            reasonCode: decision.Reason.ToString())).ToArray();

    public static StrategyExecutionSignal[] FromAdaptiveGrid(IReadOnlyList<AdaptiveGridDecision> decisions) =>
        decisions.Select(static decision => decision.Signal switch
        {
            GridSignal.NoTrade => new StrategyExecutionSignal(
                decision.BarIndex, StrategyExecutionAction.NoTrade, reasonCode: decision.Reason.ToString()),
            GridSignal.BuyGridLevel => new StrategyExecutionSignal(
                decision.BarIndex, StrategyExecutionAction.BuyBaseQuantity, decision.TargetQuantity, decision.Reason.ToString()),
            GridSignal.SellGridLevel => new StrategyExecutionSignal(
                decision.BarIndex, StrategyExecutionAction.SellBaseQuantity, decision.TargetQuantity, decision.Reason.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(decisions)),
        }).ToArray();
}
