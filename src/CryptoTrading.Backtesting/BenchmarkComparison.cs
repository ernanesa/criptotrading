using System.Security.Cryptography;
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
    string CostModel);

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
        var firstPrice = bars[0].ClosePrice.Value;
        var lastPrice = bars[^1].ClosePrice.Value;
        var peak = firstPrice;
        var maxDrawdown = 0m;
        foreach (var bar in bars)
        {
            peak = Math.Max(peak, bar.ClosePrice.Value);
            maxDrawdown = Math.Max(maxDrawdown, 1m - bar.ClosePrice.Value / peak);
        }

        var buyHoldFinal = options.InitialCapital * lastPrice / firstPrice;
        var buyHold = new BenchmarkResult("buy-and-hold", options.InitialCapital, buyHoldFinal,
            buyHoldFinal / options.InitialCapital - 1m, maxDrawdown, bars.Count, "entry-at-first-close-no-costs");
        var strategyResult = new BenchmarkResult(
            strategy.StrategyId + "/" + strategy.StrategyVersion,
            strategy.Metrics.InitialCapital,
            strategy.Metrics.FinalEquity,
            strategy.Metrics.NetReturn,
            strategy.Metrics.MaximumDrawdown,
            strategy.Metrics.PeriodCount,
            "strategy-evaluator-costs-included");
        var fingerprintPayload = JsonSerializer.Serialize(new { strategy = strategyResult, no_trade = noTrade, buy_hold = buyHold });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload)));
        return new(strategyResult, noTrade, buyHold,
            strategyResult.NetReturn > noTrade.NetReturn,
            strategyResult.NetReturn > buyHold.NetReturn,
            fingerprint);
    }
}
