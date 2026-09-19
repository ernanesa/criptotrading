using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

/// <summary>Explicit assumptions for one deterministic trend-following evaluation.</summary>
public sealed record BacktestEvaluationOptions
{
    public BacktestEvaluationOptions(decimal initialCapital, int periodsPerYear, decimal annualRiskFreeRate = 0m)
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

        InitialCapital = initialCapital;
        PeriodsPerYear = periodsPerYear;
        AnnualRiskFreeRate = annualRiskFreeRate;
    }

    public decimal InitialCapital { get; }

    /// <summary>Annualization factor for the supplied bar frequency (for example, 365 for daily crypto bars).</summary>
    public int PeriodsPerYear { get; }

    public decimal AnnualRiskFreeRate { get; }

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
    IReadOnlyList<SimulatedTrade> Trades);

/// <summary>
/// Bounded long-only evaluator for the baseline strategy. A signal is filled at the same closed-bar
/// reference price with adverse slippage, as an intentionally explicit approximation until an OHLCV
/// execution simulator is introduced. It never submits exchange orders.
/// </summary>
public sealed class TrendFollowingBacktestEvaluator
{
    public static BacktestEvaluationResult Evaluate(
        IReadOnlyList<CloseBar> bars,
        TrendFollowingParameters parameters,
        BacktestEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);

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
            var decision = decisions[index];
            var referencePrice = bars[index].ClosePrice.Value;

            if (decision.Signal == TrendFollowingSignal.EnterLong && quantity == 0m)
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
                    decision.Signal,
                    referencePrice,
                    executedPrice,
                    quantityToBuy,
                    quantityToBuy * referencePrice,
                    executedNotional,
                    fee,
                    slippageCost));
            }
            else if (decision.Signal == TrendFollowingSignal.ExitLong && quantity > 0m)
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
                    decision.Signal,
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
            trades);
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
