using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

/// <summary>Timing assumptions for simulated fills; neither represents an exchange fill guarantee.</summary>
public enum BacktestExecutionTiming
{
    NextBarClose,
    LegacySignalBarClose
}

/// <summary>Explicit assumptions for one deterministic trend-following evaluation.</summary>
public sealed record BacktestEvaluationOptions
{
    public BacktestEvaluationOptions(decimal initialCapital, int periodsPerYear, decimal annualRiskFreeRate = 0m)
        : this(initialCapital, periodsPerYear, annualRiskFreeRate, BacktestExecutionTiming.NextBarClose)
    {
    }

    public BacktestEvaluationOptions(decimal initialCapital, int periodsPerYear, BacktestExecutionTiming executionTiming)
        : this(initialCapital, periodsPerYear, 0m, executionTiming)
    {
    }

    public BacktestEvaluationOptions(decimal initialCapital, int periodsPerYear, decimal annualRiskFreeRate,
        BacktestExecutionTiming executionTiming)
    {
        if (initialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapital), "Initial capital must be greater than zero.");
        }

        if (periodsPerYear <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(periodsPerYear), "Periods per year must be greater than zero.");
        }

        if (annualRiskFreeRate <= -1m)
        {
            throw new ArgumentOutOfRangeException(nameof(annualRiskFreeRate), "Annual risk-free rate must be greater than -100%.");
        }

        if (executionTiming is not (BacktestExecutionTiming.NextBarClose or BacktestExecutionTiming.LegacySignalBarClose))
        {
            throw new ArgumentOutOfRangeException(nameof(executionTiming));
        }

        InitialCapital = initialCapital;
        PeriodsPerYear = periodsPerYear;
        AnnualRiskFreeRate = annualRiskFreeRate;
        ExecutionTiming = executionTiming;
    }

    public decimal InitialCapital { get; }

    /// <summary>Annualization factor for the supplied bar frequency (for example, 365 for daily crypto bars).</summary>
    public int PeriodsPerYear { get; }

    public decimal AnnualRiskFreeRate { get; }

    public BacktestExecutionTiming ExecutionTiming { get; }

    public static BacktestEvaluationOptions DailyCrypto(decimal initialCapital = 10_000m) =>
        new(initialCapital, periodsPerYear: 365);
}

/// <summary>One marked-to-close equity observation after the bar's signal and fill assumption.</summary>
public sealed record EquityCurvePoint(
    DateTimeOffset CloseTimeUtc,
    decimal Equity,
    decimal Cash,
    decimal BaseAssetQuantity,
    decimal ReferencePrice,
    bool InPosition,
    decimal PeriodReturn);

/// <summary>One simulated fill. All notional values use the bar close as the reference market price.</summary>
public sealed record SimulatedTrade(
    int BarIndex,
    DateTimeOffset CloseTimeUtc,
    TrendFollowingSignal Signal,
    decimal ReferencePrice,
    decimal ExecutedPrice,
    decimal Quantity,
    decimal ReferenceNotional,
    decimal ExecutedNotional,
    decimal Fee,
    decimal SlippageCost);

/// <summary>
/// Deterministic performance summary. Sharpe is null when its sample standard deviation is zero
/// or when fewer than two observations exist; this avoids manufacturing a finite score from no risk data.
/// </summary>
public sealed record BacktestMetrics(
    decimal InitialCapital,
    decimal FinalEquity,
    decimal NetReturn,
    decimal TotalFees,
    decimal TotalSlippageCost,
    decimal TurnoverNotional,
    decimal TurnoverRatio,
    decimal MaximumDrawdown,
    double? SharpeRatio,
    int PeriodCount,
    int TradeCount,
    decimal TimeInMarketRatio,
    int PeriodsPerYear,
    decimal AnnualRiskFreeRate);

public sealed record BacktestEvaluationResult(
    string StrategyId,
    string StrategyVersion,
    string StrategyConfigurationFingerprint,
    BacktestMetrics Metrics,
    IReadOnlyList<EquityCurvePoint> EquityCurve,
    IReadOnlyList<SimulatedTrade> Trades)
{
    public BacktestExecutionTiming ExecutionTiming { get; init; } = BacktestExecutionTiming.LegacySignalBarClose;
    public string? EvaluationAssumptionsFingerprint { get; init; }
}

/// <summary>
/// Bounded long-only evaluator. By default a signal uses the next closed-bar reference price;
/// the non-causal legacy timing is available only as an explicitly labeled reproducibility option.
/// Neither timing proves an exchange fill. It never submits exchange orders.
/// </summary>
public sealed class TrendFollowingBacktestEvaluator
{
    public static BacktestEvaluationResult Evaluate(
        IReadOnlyList<CloseBar> bars,
        TrendFollowingParameters parameters,
        BacktestEvaluationOptions options) => EvaluateCore(bars, parameters, options, 0);

    internal static BacktestEvaluationResult EvaluateWindow(
        IReadOnlyList<CloseBar> bars,
        TrendFollowingParameters parameters,
        BacktestEvaluationOptions options,
        int firstExecutableBar) => EvaluateCore(bars, parameters, options, firstExecutableBar);

    private static BacktestEvaluationResult EvaluateCore(
        IReadOnlyList<CloseBar> bars,
        TrendFollowingParameters parameters,
        BacktestEvaluationOptions options,
        int firstExecutableBar)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);
        if (firstExecutableBar < 0 || firstExecutableBar > bars.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstExecutableBar));
        }

        var decisions = new TrendFollowingStrategy(parameters).Evaluate(bars);
        var equityCurve = new List<EquityCurvePoint>(bars.Count);
        var trades = new List<SimulatedTrade>();
        var cash = options.InitialCapital;
        var quantity = 0m;
        var previousEquity = options.InitialCapital;
        var peakEquity = options.InitialCapital;
        var maximumDrawdown = 0m;
        var totalFees = 0m;
        var totalSlippageCost = 0m;
        var turnoverNotional = 0m;
        var inMarketBars = 0;
        var returns = new List<double>(bars.Count);

        for (var index = 0; index < bars.Count; index++)
        {
            var signalIndex = options.ExecutionTiming == BacktestExecutionTiming.NextBarClose ? index - 1 : index;
            var signal = index < firstExecutableBar || signalIndex < 0 ? TrendFollowingSignal.NoTrade : decisions[signalIndex].Signal;
            var referencePrice = bars[index].ClosePrice.Value;

            if (signal == TrendFollowingSignal.EnterLong && quantity == 0m)
            {
                var executedPrice = referencePrice * (1m + parameters.SlippageRate);
                var quantityToBuy = cash / (executedPrice * (1m + parameters.FeeRate));
                var executedNotional = quantityToBuy * executedPrice;
                var fee = executedNotional * parameters.FeeRate;
                var slippageCost = quantityToBuy * referencePrice * parameters.SlippageRate;
                cash -= executedNotional + fee;
                quantity = quantityToBuy;
                totalFees += fee;
                totalSlippageCost += slippageCost;
                turnoverNotional += quantityToBuy * referencePrice;
                trades.Add(new SimulatedTrade(
                    index,
                    bars[index].CloseTimeUtc,
                    signal,
                    referencePrice,
                    executedPrice,
                    quantityToBuy,
                    quantityToBuy * referencePrice,
                    executedNotional,
                    fee,
                    slippageCost));
            }
            else if (signal == TrendFollowingSignal.ExitLong && quantity > 0m)
            {
                var quantityToSell = quantity;
                var executedPrice = referencePrice * (1m - parameters.SlippageRate);
                var executedNotional = quantityToSell * executedPrice;
                var fee = executedNotional * parameters.FeeRate;
                var slippageCost = quantityToSell * referencePrice * parameters.SlippageRate;
                cash += executedNotional - fee;
                quantity = 0m;
                totalFees += fee;
                totalSlippageCost += slippageCost;
                turnoverNotional += quantityToSell * referencePrice;
                trades.Add(new SimulatedTrade(
                    index,
                    bars[index].CloseTimeUtc,
                    signal,
                    referencePrice,
                    executedPrice,
                    quantityToSell,
                    quantityToSell * referencePrice,
                    executedNotional,
                    fee,
                    slippageCost));
            }

            var equity = cash + (quantity * referencePrice);
            var periodReturn = previousEquity == 0m ? 0m : (equity / previousEquity) - 1m;
            peakEquity = Math.Max(peakEquity, equity);
            var drawdown = peakEquity == 0m ? 0m : 1m - (equity / peakEquity);
            maximumDrawdown = Math.Max(maximumDrawdown, drawdown);
            if (quantity > 0m)
            {
                inMarketBars++;
            }

            returns.Add((double)periodReturn);
            equityCurve.Add(new EquityCurvePoint(
                bars[index].CloseTimeUtc,
                equity,
                cash,
                quantity,
                referencePrice,
                quantity > 0m,
                periodReturn));
            previousEquity = equity;
        }

        var finalEquity = equityCurve.Count == 0 ? options.InitialCapital : equityCurve[^1].Equity;
        var metrics = new BacktestMetrics(
            options.InitialCapital,
            finalEquity,
            (finalEquity / options.InitialCapital) - 1m,
            totalFees,
            totalSlippageCost,
            turnoverNotional,
            turnoverNotional / options.InitialCapital,
            maximumDrawdown,
            CalculateSharpe(returns, options),
            returns.Count,
            trades.Count,
            returns.Count == 0 ? 0m : (decimal)inMarketBars / returns.Count,
            options.PeriodsPerYear,
            options.AnnualRiskFreeRate);

        return new BacktestEvaluationResult(
            parameters.StrategyId,
            parameters.Version,
            parameters.ConfigurationFingerprint,
            metrics,
            equityCurve,
            trades)
        {
            ExecutionTiming = options.ExecutionTiming,
            EvaluationAssumptionsFingerprint = CalculateEvaluationAssumptionsFingerprint(parameters, options, "single-series-v1")
        };
    }

    internal static string CalculateEvaluationAssumptionsFingerprint(
        TrendFollowingParameters parameters, BacktestEvaluationOptions options, string scope)
    {
        var canonical = string.Join('\n',
            "trend-evaluation-assumptions-v2",
            scope,
            parameters.StrategyId.Length.ToString(CultureInfo.InvariantCulture) + ":" + parameters.StrategyId,
            parameters.Version.Length.ToString(CultureInfo.InvariantCulture) + ":" + parameters.Version,
            parameters.ConfigurationFingerprint,
            options.InitialCapital.ToString("G29", CultureInfo.InvariantCulture),
            options.PeriodsPerYear.ToString(CultureInfo.InvariantCulture),
            options.AnnualRiskFreeRate.ToString("G29", CultureInfo.InvariantCulture),
            options.ExecutionTiming.ToString());
        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(canonical)));
    }

    private static double? CalculateSharpe(List<double> returns, BacktestEvaluationOptions options)
    {
        if (returns.Count < 2)
        {
            return null;
        }

        var periodicRiskFree = Math.Pow(1d + (double)options.AnnualRiskFreeRate, 1d / options.PeriodsPerYear) - 1d;
        var excessReturns = returns.Select(value => value - periodicRiskFree).ToArray();
        var mean = excessReturns.Average();
        var variance = excessReturns.Sum(value => Math.Pow(value - mean, 2d)) / (excessReturns.Length - 1);
        var standardDeviation = Math.Sqrt(variance);
        return standardDeviation == 0d ? null : (mean / standardDeviation) * Math.Sqrt(options.PeriodsPerYear);
    }
}
