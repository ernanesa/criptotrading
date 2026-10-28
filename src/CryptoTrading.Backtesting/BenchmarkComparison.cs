using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

public sealed record BenchmarkResult(
    string Name,
    decimal InitialCapital,
    decimal FinalEquity,
    decimal NetReturn,
    decimal MaximumDrawdown,
    int PeriodCount,
    string CostModel,
    decimal? ReferenceEntryPrice = null,
    decimal? ReferenceExitPrice = null,
    decimal FeeRate = 0m,
    decimal HalfSpreadRate = 0m,
    decimal SlippageRate = 0m)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MarkedOpenAtEnd { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrategyConfigurationFingerprint { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvaluationAssumptionsFingerprint { get; init; }
}

public sealed record BenchmarkComparison(
    BenchmarkResult Strategy,
    BenchmarkResult NoTradeCash,
    BenchmarkResult BuyAndHold,
    bool BeatsNoTradeCash,
    bool BeatsBuyAndHold,
    string Fingerprint);

/// <summary>
/// Computes preregistered cash and buy-and-hold references from the same bars.
/// It never selects a winner or grants promotion; it only makes the comparison
/// explicit and reproducible.
/// </summary>
public static class BenchmarkComparisonEvaluator
{
    public static WalkForwardBenchmarkSummary AggregateOutOfSample(
        IReadOnlyList<OutOfSampleWindowResult> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Take(Math.Max(0, windows.Count - 1)).Any(static window => window.EndPositionOpen != false))
        {
            throw new InvalidOperationException("Independent OOS windows cannot be compounded when an earlier ending position is open or unknown.");
        }

        foreach (var window in windows)
        {
            var strategy = window.Benchmark.Strategy;
            if (window.StartingEquity != strategy.InitialCapital || window.FinalEquity != strategy.FinalEquity ||
                window.NetReturn != strategy.NetReturn || window.MaximumDrawdown != strategy.MaximumDrawdown ||
                window.PeriodCount != strategy.PeriodCount)
            {
                throw new InvalidOperationException("Window metrics do not match their recorded benchmark.");
            }
        }

        return AggregateOutOfSampleLegacy(windows);
    }

    internal static WalkForwardBenchmarkSummary AggregateOutOfSampleLegacy(
        IReadOnlyList<OutOfSampleWindowResult> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return AggregateOutOfSample(windows.Select(static window =>
            (window.Window, window.NetReturn, window.Benchmark)).ToArray());
    }

    public static WalkForwardBenchmarkSummary AggregateOutOfSample(
        IReadOnlyList<StrategyComparisonWindowResult> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return AggregateOutOfSample(windows.Select(static window =>
            (window.Window, window.NetReturn, window.Benchmark)).ToArray());
    }

    private static WalkForwardBenchmarkSummary AggregateOutOfSample(
        (WalkForwardWindow Window, decimal NetReturn, BenchmarkComparison Benchmark)[] windows)
    {
        if (windows.Length == 0)
        {
            throw new ArgumentException("At least one OOS window is required.", nameof(windows));
        }

        foreach (var window in windows)
        {
            ValidateComparison(window.Benchmark);
        }

        var strategyReturn = Compound(windows.Select(static window => window.NetReturn));
        var noTradeReturn = Compound(windows.Select(static window => window.Benchmark.NoTradeCash.NetReturn));
        ValidateContiguousWindows(windows);
        var buyHoldReturn = CalculateContinuousBuyAndHoldReturn(
            windows[0].Benchmark.BuyAndHold,
            windows[^1].Benchmark.BuyAndHold);
        var payload = JsonSerializer.Serialize(new BenchmarkAggregateFingerprintPayload(
            windows.Select(static window => new BenchmarkWindowFingerprintPayload(window.Window, window.Benchmark.Fingerprint)).ToArray(),
            strategyReturn, noTradeReturn, buyHoldReturn), BenchmarkFingerprintJsonContext.Default.BenchmarkAggregateFingerprintPayload);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new(
            strategyReturn,
            noTradeReturn,
            buyHoldReturn,
            strategyReturn > noTradeReturn,
            strategyReturn > buyHoldReturn,
            fingerprint);
    }

    public static BenchmarkComparison CompareOutOfSample(
        string strategyName,
        decimal initialCapital,
        decimal finalEquity,
        decimal strategyNetReturn,
        decimal strategyMaximumDrawdown,
        int periodCount,
        IReadOnlyList<CloseBar> bars,
        decimal? benchmarkEntryPrice = null,
        decimal feeRate = 0m,
        decimal halfSpreadRate = 0m,
        decimal slippageRate = 0m) => CompareOutOfSampleWithTiming(strategyName, initialCapital, finalEquity,
            strategyNetReturn, strategyMaximumDrawdown, periodCount, bars, benchmarkEntryPrice, feeRate,
            halfSpreadRate, slippageRate, "prior-close-entry", false, null, null);

    internal static BenchmarkComparison CompareOutOfSampleWithTiming(
        string strategyName,
        decimal initialCapital,
        decimal finalEquity,
        decimal strategyNetReturn,
        decimal strategyMaximumDrawdown,
        int periodCount,
        IReadOnlyList<CloseBar> bars,
        decimal? benchmarkEntryPrice,
        decimal feeRate,
        decimal halfSpreadRate,
        decimal slippageRate,
        string entryConvention,
        bool markOpenAtEnd,
        string? strategyConfigurationFingerprint,
        string? evaluationAssumptionsFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentNullException.ThrowIfNull(bars);
        if (initialCapital <= 0m || finalEquity < 0m || periodCount != bars.Count || bars.Count == 0)
        {
            throw new ArgumentException("OOS benchmark inputs must have positive capital and a complete timeline.");
        }

        ValidateBenchmarkCosts(feeRate, halfSpreadRate, slippageRate);

        var strategy = new BenchmarkResult(
            strategyName,
            initialCapital,
            finalEquity,
            strategyNetReturn,
            strategyMaximumDrawdown,
            periodCount,
            "strategy-evaluator-costs-included")
        {
            StrategyConfigurationFingerprint = strategyConfigurationFingerprint,
            EvaluationAssumptionsFingerprint = evaluationAssumptionsFingerprint
        };
        var noTrade = new BenchmarkResult(
            "NO_TRADE/cash",
            initialCapital,
            initialCapital,
            0m,
            0m,
            periodCount,
            "cash-no-costs");
        var entryReferencePrice = benchmarkEntryPrice ?? bars[0].ClosePrice.Value;
        if (entryReferencePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(benchmarkEntryPrice));
        }

        var (lastPrice, _) = ValidateAndMeasurePrices(bars, entryReferencePrice);
        var adverseRate = halfSpreadRate + slippageRate;
        var entryPrice = entryReferencePrice * (1m + adverseRate);
        var quantity = initialCapital / (entryPrice * (1m + feeRate));
        var exitPrice = markOpenAtEnd ? lastPrice : lastPrice * (1m - adverseRate);
        var exitNotional = quantity * exitPrice;
        var buyHoldFinal = markOpenAtEnd ? exitNotional : exitNotional * (1m - feeRate);
        var maxDrawdown = CalculateBuyAndHoldDrawdown(bars, initialCapital, entryReferencePrice, quantity,
            markOpenAtEnd ? null : buyHoldFinal);
        var buyHold = new BenchmarkResult(
            "buy-and-hold",
            initialCapital,
            buyHoldFinal,
            buyHoldFinal / initialCapital - 1m,
            maxDrawdown,
            periodCount,
            $"{entryConvention}{(markOpenAtEnd ? "; mark-open" : string.Empty)}; fee={feeRate.ToString("0.00%", CultureInfo.InvariantCulture)}; " +
            $"half-spread={halfSpreadRate.ToString("0.00%", CultureInfo.InvariantCulture)}; " +
            $"slippage={slippageRate.ToString("0.00%", CultureInfo.InvariantCulture)}",
            entryReferencePrice,
            lastPrice,
            feeRate,
            halfSpreadRate,
            slippageRate)
        { MarkedOpenAtEnd = markOpenAtEnd };
        return CreateComparison(strategy, noTrade, buyHold);
    }

    public static BenchmarkComparison Compare(
        BacktestEvaluationResult strategy,
        IReadOnlyList<CloseBar> bars,
        BacktestEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);
        if (strategy.ExecutionTiming != options.ExecutionTiming ||
            strategy.Metrics.InitialCapital != options.InitialCapital ||
            strategy.Metrics.PeriodsPerYear != options.PeriodsPerYear ||
            strategy.Metrics.AnnualRiskFreeRate != options.AnnualRiskFreeRate)
        {
            throw new ArgumentException("Benchmark options must match the strategy evaluation assumptions.", nameof(options));
        }
        if (bars.Count == 0 || strategy.EquityCurve.Count != bars.Count)
        {
            throw new ArgumentException("Benchmark comparison requires one equity point per bar.", nameof(bars));
        }

        for (var index = 0; index < bars.Count; index++)
        {
            if (strategy.EquityCurve[index].CloseTimeUtc != bars[index].CloseTimeUtc)
            {
                throw new InvalidOperationException("Strategy and benchmark timelines do not match.");
            }

            if (bars[index].ClosePrice.Value != strategy.EquityCurve[index].ReferencePrice)
            {
                throw new ArgumentException("Benchmark prices must match the evaluated strategy prices.", nameof(bars));
            }

            if (bars[index].ClosePrice.Value <= 0m)
            {
                throw new ArgumentException("Benchmark prices must be positive.", nameof(bars));
            }
        }

        var noTrade = new BenchmarkResult("NO_TRADE/cash", options.InitialCapital, options.InitialCapital, 0m, 0m,
            bars.Count, "cash-no-costs");
        var (lastPrice, maxDrawdown) = ValidateAndMeasurePrices(bars);
        var firstPrice = bars[0].ClosePrice.Value;

        var buyHoldFinal = options.InitialCapital * lastPrice / firstPrice;
        var buyHold = new BenchmarkResult("buy-and-hold", options.InitialCapital, buyHoldFinal,
            buyHoldFinal / options.InitialCapital - 1m, maxDrawdown, bars.Count, "entry-at-first-close-no-costs",
            firstPrice, lastPrice);
        var strategyResult = new BenchmarkResult(
            FormatStrategyName(strategy.StrategyId, strategy.StrategyVersion, strategy.ExecutionTiming),
            strategy.Metrics.InitialCapital,
            strategy.Metrics.FinalEquity,
            strategy.Metrics.NetReturn,
            strategy.Metrics.MaximumDrawdown,
            strategy.Metrics.PeriodCount,
            "strategy-evaluator-costs-included")
        {
            StrategyConfigurationFingerprint = strategy.EvaluationAssumptionsFingerprint is null ? null : strategy.StrategyConfigurationFingerprint,
            EvaluationAssumptionsFingerprint = strategy.EvaluationAssumptionsFingerprint
        };
        return CreateComparison(strategyResult, noTrade, buyHold);
    }

    internal static string FormatStrategyName(string strategyId, string version, BacktestExecutionTiming timing) =>
        timing == BacktestExecutionTiming.LegacySignalBarClose
            ? strategyId + "/" + version
            : strategyId + "/" + version + "|execution:next-bar-close-v1";

    private static BenchmarkComparison CreateComparison(
        BenchmarkResult strategy,
        BenchmarkResult noTrade,
        BenchmarkResult buyHold)
    {
        var fingerprintPayload = JsonSerializer.Serialize(new BenchmarkFingerprintPayload(strategy, noTrade, buyHold),
            BenchmarkFingerprintJsonContext.Default.BenchmarkFingerprintPayload);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload)));
        return new(strategy, noTrade, buyHold,
            strategy.NetReturn > noTrade.NetReturn,
            strategy.NetReturn > buyHold.NetReturn,
            fingerprint);
    }

    private static void ValidateComparison(BenchmarkComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var expected = CreateComparison(comparison.Strategy, comparison.NoTradeCash, comparison.BuyAndHold);
        if (!string.Equals(expected.Fingerprint, comparison.Fingerprint, StringComparison.Ordinal) ||
            expected.BeatsNoTradeCash != comparison.BeatsNoTradeCash || expected.BeatsBuyAndHold != comparison.BeatsBuyAndHold)
        {
            throw new InvalidOperationException("Benchmark comparison does not match its recorded fingerprint.");
        }
    }

    private static decimal Compound(IEnumerable<decimal> returns)
    {
        var compounded = 1m;
        foreach (var value in returns)
        {
            compounded *= 1m + value;
        }

        return compounded - 1m;
    }

    private static (decimal LastPrice, decimal MaximumDrawdown) ValidateAndMeasurePrices(
        IReadOnlyList<CloseBar> bars,
        decimal? initialPrice = null)
    {
        var firstPrice = initialPrice ?? bars[0].ClosePrice.Value;
        var lastPrice = bars[^1].ClosePrice.Value;
        var peak = firstPrice;
        var maxDrawdown = 0m;
        foreach (var bar in bars)
        {
            if (bar.ClosePrice.Value <= 0m)
            {
                throw new ArgumentException("Benchmark prices must be positive.", nameof(bars));
            }

            peak = Math.Max(peak, bar.ClosePrice.Value);
            maxDrawdown = Math.Max(maxDrawdown, 1m - bar.ClosePrice.Value / peak);
        }

        return (lastPrice, maxDrawdown);
    }

    private static void ValidateBenchmarkCosts(decimal feeRate, decimal halfSpreadRate, decimal slippageRate)
    {
        if (feeRate is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(feeRate));
        }

        if (halfSpreadRate is < 0m or > 0.5m)
        {
            throw new ArgumentOutOfRangeException(nameof(halfSpreadRate));
        }

        if (slippageRate is < 0m or > 0.5m || halfSpreadRate + slippageRate >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(slippageRate));
        }
    }

    private static void ValidateContiguousWindows(
        (WalkForwardWindow Window, decimal NetReturn, BenchmarkComparison Benchmark)[] windows)
    {
        for (var index = 1; index < windows.Length; index++)
        {
            var previous = windows[index - 1].Window;
            var current = windows[index].Window;
            if (current.OutOfSampleStartIndex != previous.OutOfSampleStartIndex + previous.OutOfSampleLength)
            {
                throw new ArgumentException("Aggregate OOS windows must be ordered, contiguous and non-overlapping.", nameof(windows));
            }
        }
    }

    private static decimal CalculateContinuousBuyAndHoldReturn(BenchmarkResult firstWindow, BenchmarkResult lastWindow)
    {
        if (firstWindow.ReferenceEntryPrice is not { } entryReferencePrice ||
            lastWindow.ReferenceExitPrice is not { } exitReferencePrice ||
            firstWindow.InitialCapital <= 0m ||
            firstWindow.FeeRate != lastWindow.FeeRate ||
            firstWindow.HalfSpreadRate != lastWindow.HalfSpreadRate ||
            firstWindow.SlippageRate != lastWindow.SlippageRate ||
            firstWindow.MarkedOpenAtEnd != lastWindow.MarkedOpenAtEnd)
        {
            throw new InvalidOperationException("Continuous buy-and-hold aggregation requires matching endpoint and cost metadata.");
        }

        var adverseRate = firstWindow.HalfSpreadRate + firstWindow.SlippageRate;
        var entryPrice = entryReferencePrice * (1m + adverseRate);
        var quantity = firstWindow.InitialCapital / (entryPrice * (1m + firstWindow.FeeRate));
        var exitPrice = lastWindow.MarkedOpenAtEnd ? exitReferencePrice : exitReferencePrice * (1m - adverseRate);
        var finalEquity = lastWindow.MarkedOpenAtEnd ? quantity * exitPrice : quantity * exitPrice * (1m - firstWindow.FeeRate);
        return (finalEquity / firstWindow.InitialCapital) - 1m;
    }

    private static decimal CalculateBuyAndHoldDrawdown(
        IReadOnlyList<CloseBar> bars,
        decimal initialCapital,
        decimal entryReferencePrice,
        decimal quantity,
        decimal? liquidationEquity)
    {
        var peak = initialCapital;
        var maximumDrawdown = 1m - ((quantity * entryReferencePrice) / peak);
        foreach (var bar in bars)
        {
            var equity = quantity * bar.ClosePrice.Value;
            peak = Math.Max(peak, equity);
            maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - equity / peak);
        }

        if (liquidationEquity is { } finalEquity)
        {
            maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - finalEquity / peak);
        }

        return maximumDrawdown;
    }
}
