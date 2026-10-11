using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    decimal SlippageRate = 0m);

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

        var strategyReturn = Compound(windows.Select(static window => window.NetReturn));
        var noTradeReturn = Compound(windows.Select(static window => window.Benchmark.NoTradeCash.NetReturn));
        ValidateContiguousWindows(windows);
        var buyHoldReturn = CalculateContinuousBuyAndHoldReturn(
            windows[0].Benchmark.BuyAndHold,
            windows[^1].Benchmark.BuyAndHold);
        var payload = JsonSerializer.Serialize(new
        {
            windows = windows.Select(static window => new
            {
                window = window.Window,
                benchmark = window.Benchmark.Fingerprint,
            }),
            strategy_return = strategyReturn,
            no_trade_return = noTradeReturn,
            buy_hold_return = buyHoldReturn,
        });
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
        decimal slippageRate = 0m)
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
            "strategy-evaluator-costs-included");
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
        var exitPrice = lastPrice * (1m - adverseRate);
        var exitNotional = quantity * exitPrice;
        var buyHoldFinal = exitNotional * (1m - feeRate);
        var maxDrawdown = CalculateBuyAndHoldDrawdown(bars, initialCapital, entryReferencePrice, quantity);
        var buyHold = new BenchmarkResult(
            "buy-and-hold",
            initialCapital,
            buyHoldFinal,
            buyHoldFinal / initialCapital - 1m,
            maxDrawdown,
            periodCount,
            $"prior-close-entry; fee={feeRate.ToString("0.00%", CultureInfo.InvariantCulture)}; " +
            $"half-spread={halfSpreadRate.ToString("0.00%", CultureInfo.InvariantCulture)}; " +
            $"slippage={slippageRate.ToString("0.00%", CultureInfo.InvariantCulture)}",
            entryReferencePrice,
            lastPrice,
            feeRate,
            halfSpreadRate,
            slippageRate);
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
            strategy.StrategyId + "/" + strategy.StrategyVersion,
            strategy.Metrics.InitialCapital,
            strategy.Metrics.FinalEquity,
            strategy.Metrics.NetReturn,
            strategy.Metrics.MaximumDrawdown,
            strategy.Metrics.PeriodCount,
            "strategy-evaluator-costs-included");
        return CreateComparison(strategyResult, noTrade, buyHold);
    }

    private static BenchmarkComparison CreateComparison(
        BenchmarkResult strategy,
        BenchmarkResult noTrade,
        BenchmarkResult buyHold)
    {
        var fingerprintPayload = JsonSerializer.Serialize(new { strategy, no_trade = noTrade, buy_hold = buyHold });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload)));
        return new(strategy, noTrade, buyHold,
            strategy.NetReturn > noTrade.NetReturn,
            strategy.NetReturn > buyHold.NetReturn,
            fingerprint);
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
            firstWindow.SlippageRate != lastWindow.SlippageRate)
        {
            throw new InvalidOperationException("Continuous buy-and-hold aggregation requires matching endpoint and cost metadata.");
        }

        var adverseRate = firstWindow.HalfSpreadRate + firstWindow.SlippageRate;
        var entryPrice = entryReferencePrice * (1m + adverseRate);
        var quantity = firstWindow.InitialCapital / (entryPrice * (1m + firstWindow.FeeRate));
        var exitPrice = exitReferencePrice * (1m - adverseRate);
        var finalEquity = quantity * exitPrice * (1m - firstWindow.FeeRate);
        return (finalEquity / firstWindow.InitialCapital) - 1m;
    }

    private static decimal CalculateBuyAndHoldDrawdown(
        IReadOnlyList<CloseBar> bars,
        decimal initialCapital,
        decimal entryReferencePrice,
        decimal quantity)
    {
        var peak = initialCapital;
        var maximumDrawdown = 1m - ((quantity * entryReferencePrice) / peak);
        foreach (var bar in bars)
        {
            var equity = quantity * bar.ClosePrice.Value;
            peak = Math.Max(peak, equity);
            maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - equity / peak);
        }

        return maximumDrawdown;
    }
}
