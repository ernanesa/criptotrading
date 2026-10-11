using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

public enum StrategyExecutionAction
{
    NoTrade,
    EnterLong,
    ExitLong,
    BuyBaseQuantity,
    SellBaseQuantity,
}

/// <summary>A strategy decision made after a closed bar; fills occur only on a later bar.</summary>
public sealed record StrategyExecutionSignal
{
    public StrategyExecutionSignal(
        int decisionBarIndex,
        StrategyExecutionAction action,
        decimal? baseQuantity = null,
        string? reasonCode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decisionBarIndex);
        var quantityAction = action is StrategyExecutionAction.BuyBaseQuantity or StrategyExecutionAction.SellBaseQuantity;
        if (quantityAction != baseQuantity.HasValue || baseQuantity is <= 0m)
        {
            throw new ArgumentException("Only fixed-quantity actions require a positive base quantity.", nameof(baseQuantity));
        }

        DecisionBarIndex = decisionBarIndex;
        Action = action;
        BaseQuantity = baseQuantity;
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim();
    }

    public int DecisionBarIndex { get; }
    public StrategyExecutionAction Action { get; }
    public decimal? BaseQuantity { get; }
    public string? ReasonCode { get; }
}

/// <summary>Explicit fill proxy and costs for close-only research data.</summary>
public sealed record StrategyBacktestOptions
{
    public StrategyBacktestOptions(
        decimal initialCapital,
        int periodsPerYear,
        decimal feeRate,
        decimal halfSpreadRate,
        decimal slippageRate,
        decimal annualRiskFreeRate = 0m,
        int executionDelayBars = 1,
        decimal minimumOrderNotionalQuote = 0m,
        decimal? maximumPositionNotionalAtBuyQuote = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapital);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodsPerYear);

        ValidateRate(feeRate, nameof(feeRate), maximum: 1m);
        ValidateRate(halfSpreadRate, nameof(halfSpreadRate), maximum: 0.5m);
        ValidateRate(slippageRate, nameof(slippageRate), maximum: 0.5m);
        if (halfSpreadRate + slippageRate >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(slippageRate), "Adverse spread and slippage must remain below 100%.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(annualRiskFreeRate, -1m);
        ArgumentOutOfRangeException.ThrowIfLessThan(executionDelayBars, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumOrderNotionalQuote);
        var maximumPositionNotionalAtBuy = maximumPositionNotionalAtBuyQuote ?? initialCapital;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPositionNotionalAtBuy);

        InitialCapital = initialCapital;
        PeriodsPerYear = periodsPerYear;
        FeeRate = feeRate;
        HalfSpreadRate = halfSpreadRate;
        SlippageRate = slippageRate;
        AnnualRiskFreeRate = annualRiskFreeRate;
        ExecutionDelayBars = executionDelayBars;
        MinimumOrderNotionalQuote = minimumOrderNotionalQuote;
        MaximumPositionNotionalAtBuyQuote = maximumPositionNotionalAtBuy;
    }

    public decimal InitialCapital { get; }
    public int PeriodsPerYear { get; }
    public decimal FeeRate { get; }
    public decimal HalfSpreadRate { get; }
    public decimal SlippageRate { get; }
    public decimal AnnualRiskFreeRate { get; }
    public int ExecutionDelayBars { get; }
    public decimal MinimumOrderNotionalQuote { get; }
    public decimal MaximumPositionNotionalAtBuyQuote { get; }

    private static void ValidateRate(decimal value, string parameterName, decimal maximum)
    {
        if (value is < 0m || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public sealed record StrategyBacktestTrade(
    int DecisionBarIndex,
    int FillBarIndex,
    DateTimeOffset FillTimeUtc,
    StrategyExecutionAction Action,
    string? ReasonCode,
    decimal ReferencePrice,
    decimal ExecutedPrice,
    decimal BaseQuantity,
    decimal RequestedBaseQuantity,
    bool IsPartialFill,
    decimal Fee,
    decimal SpreadCost,
    decimal SlippageCost);

public sealed record StrategyBacktestMetrics(
    decimal InitialCapital,
    decimal FinalEquity,
    decimal NetReturn,
    decimal TotalFees,
    decimal TotalSpreadCost,
    decimal TotalSlippageCost,
    decimal TurnoverNotional,
    decimal TurnoverRatio,
    decimal MaximumDrawdown,
    double? SharpeRatio,
    int PeriodCount,
    int FilledOrderCount,
    int PartialFillCount,
    int RejectedMinimumNotionalSignalCount,
    int UnfilledFixedQuantitySignalCount,
    int UnfilledSignalCount,
    decimal TimeInMarketRatio,
    bool HasOpenPositionAtEnd,
    bool HasPartialFixedQuantityFills,
    bool HasFixedQuantityStateDivergence,
    bool HasSignalStateDivergence,
    decimal MaximumGrossExposureQuote);

public sealed record StrategyBacktestEvaluationResult(
    string StrategyId,
    string StrategyVersion,
    string StrategyConfigurationFingerprint,
    string EvaluationFingerprint,
    string ExecutionAssumption,
    StrategyBacktestOptions Options,
    StrategyBacktestMetrics Metrics,
    IReadOnlyList<EquityCurvePoint> EquityCurve,
    IReadOnlyList<StrategyBacktestTrade> Trades);

/// <summary>
/// Simulates long-only signals with an explicit next-bar-close proxy. It does not
/// represent queue position, intrabar OHLC execution, exchange filters or live fills.
/// </summary>
public static class StrategyBacktestEvaluator
{
    public static string DescribeExecutionAssumption(StrategyBacktestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"closed-bar signal; {options.ExecutionDelayBars.ToString(CultureInfo.InvariantCulture)}-bar delay; " +
            "fill at delayed bar close proxy; adverse fee/half-spread/slippage; no live orders";
    }

    public static StrategyBacktestEvaluationResult Evaluate(
        string strategyId,
        string strategyVersion,
        string strategyConfigurationFingerprint,
        IReadOnlyList<CloseBar> bars,
        IReadOnlyList<StrategyExecutionSignal> signals,
        StrategyBacktestOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyConfigurationFingerprint);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);
        ValidateInputs(bars, signals);

        var equityCurve = new List<EquityCurvePoint>(bars.Count);
        var trades = new List<StrategyBacktestTrade>();
        var cash = options.InitialCapital;
        var baseQuantity = 0m;
        var previousEquity = options.InitialCapital;
        var peakEquity = options.InitialCapital;
        var maxDrawdown = 0m;
        var fees = 0m;
        var spreadCosts = 0m;
        var slippageCosts = 0m;
        var turnover = 0m;
        var inMarketBars = 0;
        var unfilledSignals = 0;
        var unfilledFixedQuantitySignals = 0;
        var rejectedMinimumNotionalSignals = 0;
        var maximumGrossExposure = 0m;
        var returns = new List<double>(bars.Count);

        for (var fillIndex = 0; fillIndex < bars.Count; fillIndex++)
        {
            var decisionIndex = fillIndex - options.ExecutionDelayBars;
            if (decisionIndex >= 0)
            {
                var signal = signals[decisionIndex];
                if (signal.Action != StrategyExecutionAction.NoTrade)
                {
                    if (!TryFill(signal, decisionIndex, fillIndex, bars[fillIndex], options,
                        ref cash, ref baseQuantity, out var trade, out var rejectedByMinimumNotional))
                    {
                        unfilledSignals++;
                        if (signal.Action is StrategyExecutionAction.BuyBaseQuantity or StrategyExecutionAction.SellBaseQuantity)
                        {
                            unfilledFixedQuantitySignals++;
                        }

                        if (rejectedByMinimumNotional)
                        {
                            rejectedMinimumNotionalSignals++;
                        }
                    }
                    else
                    {
                        trades.Add(trade);
                        fees += trade.Fee;
                        spreadCosts += trade.SpreadCost;
                        slippageCosts += trade.SlippageCost;
                        turnover += trade.BaseQuantity * trade.ReferencePrice;
                    }
                }
            }

            var referencePrice = bars[fillIndex].ClosePrice.Value;
            maximumGrossExposure = Math.Max(maximumGrossExposure, baseQuantity * referencePrice);
            var equity = cash + (baseQuantity * referencePrice);
            var periodReturn = previousEquity == 0m ? 0m : (equity / previousEquity) - 1m;
            peakEquity = Math.Max(peakEquity, equity);
            var drawdown = peakEquity == 0m ? 0m : 1m - (equity / peakEquity);
            maxDrawdown = Math.Max(maxDrawdown, drawdown);
            if (baseQuantity > 0m)
            {
                inMarketBars++;
            }

            returns.Add((double)periodReturn);
            equityCurve.Add(new EquityCurvePoint(
                bars[fillIndex].CloseTimeUtc,
                equity,
                cash,
                baseQuantity,
                referencePrice,
                baseQuantity > 0m,
                periodReturn));
            previousEquity = equity;
        }

        for (var decisionIndex = Math.Max(0, bars.Count - options.ExecutionDelayBars); decisionIndex < signals.Count; decisionIndex++)
        {
            if (signals[decisionIndex].Action != StrategyExecutionAction.NoTrade)
            {
                unfilledSignals++;
                if (signals[decisionIndex].Action is StrategyExecutionAction.BuyBaseQuantity or StrategyExecutionAction.SellBaseQuantity)
                {
                    unfilledFixedQuantitySignals++;
                }
            }
        }

        var finalEquity = equityCurve.Count == 0 ? options.InitialCapital : equityCurve[^1].Equity;
        var metrics = new StrategyBacktestMetrics(
            options.InitialCapital,
            finalEquity,
            (finalEquity / options.InitialCapital) - 1m,
            fees,
            spreadCosts,
            slippageCosts,
            turnover,
            turnover / options.InitialCapital,
            maxDrawdown,
            CalculateSharpe(returns, options),
            returns.Count,
            trades.Count,
            trades.Count(static trade => trade.IsPartialFill),
            rejectedMinimumNotionalSignals,
            unfilledFixedQuantitySignals,
            unfilledSignals,
            returns.Count == 0 ? 0m : (decimal)inMarketBars / returns.Count,
            baseQuantity > 0m,
            trades.Any(static trade => trade.IsPartialFill),
            trades.Any(static trade => trade.IsPartialFill) || unfilledFixedQuantitySignals > 0,
            trades.Any(static trade => trade.IsPartialFill) || unfilledSignals > 0,
            maximumGrossExposure);
        var evaluationFingerprint = CreateFingerprint(
            strategyId,
            strategyVersion,
            strategyConfigurationFingerprint,
            bars,
            signals,
            options);

        return new StrategyBacktestEvaluationResult(
            strategyId,
            strategyVersion,
            strategyConfigurationFingerprint,
            evaluationFingerprint,
            DescribeExecutionAssumption(options),
            options,
            metrics,
            equityCurve,
            trades);
    }

    private static bool TryFill(
        StrategyExecutionSignal signal,
        int decisionIndex,
        int fillIndex,
        CloseBar fillBar,
        StrategyBacktestOptions options,
        ref decimal cash,
        ref decimal position,
        out StrategyBacktestTrade trade,
        out bool rejectedByMinimumNotional)
    {
        trade = null!;
        rejectedByMinimumNotional = false;
        var referencePrice = fillBar.ClosePrice.Value;
        var adverseRate = options.HalfSpreadRate + options.SlippageRate;
        if (signal.Action is StrategyExecutionAction.EnterLong or StrategyExecutionAction.BuyBaseQuantity)
        {
            if (signal.Action == StrategyExecutionAction.EnterLong && position > 0m)
            {
                return false;
            }

            var executedPrice = referencePrice * (1m + adverseRate);
            var maxAffordableQuantity = cash / (executedPrice * (1m + options.FeeRate));
            var positionNotionalHeadroom = Math.Max(0m, options.MaximumPositionNotionalAtBuyQuote - (position * referencePrice));
            var maximumExecutableQuantity = Math.Min(maxAffordableQuantity, positionNotionalHeadroom / executedPrice);
            var requestedQuantity = signal.Action == StrategyExecutionAction.EnterLong
                ? maximumExecutableQuantity
                : signal.BaseQuantity!.Value;
            var quantity = Math.Min(requestedQuantity, maximumExecutableQuantity);
            if (quantity <= 0m)
            {
                return false;
            }

            var executedNotional = quantity * executedPrice;
            if (executedNotional < options.MinimumOrderNotionalQuote)
            {
                rejectedByMinimumNotional = true;
                return false;
            }

            var fee = executedNotional * options.FeeRate;
            var spreadCost = quantity * referencePrice * options.HalfSpreadRate;
            var slippageCost = quantity * referencePrice * options.SlippageRate;
            cash -= executedNotional + fee;
            position += quantity;
            trade = new(
                decisionIndex,
                fillIndex,
                fillBar.CloseTimeUtc,
                signal.Action,
                signal.ReasonCode,
                referencePrice,
                executedPrice,
                quantity,
                requestedQuantity,
                signal.Action == StrategyExecutionAction.BuyBaseQuantity && quantity < requestedQuantity,
                fee,
                spreadCost,
                slippageCost);
            return true;
        }

        if (signal.Action is StrategyExecutionAction.ExitLong or StrategyExecutionAction.SellBaseQuantity)
        {
            var requestedQuantity = signal.Action == StrategyExecutionAction.ExitLong
                ? position
                : signal.BaseQuantity!.Value;
            var quantity = Math.Min(requestedQuantity, position);
            if (quantity <= 0m)
            {
                return false;
            }

            var executedPrice = referencePrice * (1m - adverseRate);
            var executedNotional = quantity * executedPrice;
            if (executedNotional < options.MinimumOrderNotionalQuote)
            {
                rejectedByMinimumNotional = true;
                return false;
            }

            var fee = executedNotional * options.FeeRate;
            var spreadCost = quantity * referencePrice * options.HalfSpreadRate;
            var slippageCost = quantity * referencePrice * options.SlippageRate;
            cash += executedNotional - fee;
            position -= quantity;
            trade = new(
                decisionIndex,
                fillIndex,
                fillBar.CloseTimeUtc,
                signal.Action,
                signal.ReasonCode,
                referencePrice,
                executedPrice,
                quantity,
                requestedQuantity,
                signal.Action == StrategyExecutionAction.SellBaseQuantity && quantity < requestedQuantity,
                fee,
                spreadCost,
                slippageCost);
            return true;
        }

        return false;
    }

    private static void ValidateInputs(
        IReadOnlyList<CloseBar> bars,
        IReadOnlyList<StrategyExecutionSignal> signals)
    {
        if (bars.Count == 0 || signals.Count != bars.Count)
        {
            throw new ArgumentException("At least one bar and exactly one signal per bar are required.", nameof(signals));
        }

        var symbol = bars[0].Symbol;
        DateTimeOffset? previousCloseTime = null;
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index] ?? throw new ArgumentException("Bar collection cannot contain null values.", nameof(bars));
            var signal = signals[index] ?? throw new ArgumentException("Signal collection cannot contain null values.", nameof(signals));
            if (bar.Symbol != symbol || (previousCloseTime.HasValue && bar.CloseTimeUtc <= previousCloseTime.Value))
            {
                throw new ArgumentException("Bars must use one symbol and strictly increasing close times.", nameof(bars));
            }

            if (signal.DecisionBarIndex != index)
            {
                throw new ArgumentException("Each signal index must match its bar index.", nameof(signals));
            }

            previousCloseTime = bar.CloseTimeUtc;
        }
    }

    private static double? CalculateSharpe(List<double> returns, StrategyBacktestOptions options)
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

    private static string CreateFingerprint(
        string strategyId,
        string strategyVersion,
        string strategyConfigurationFingerprint,
        IReadOnlyList<CloseBar> bars,
        IReadOnlyList<StrategyExecutionSignal> signals,
        StrategyBacktestOptions options)
    {
        var canonical = new StringBuilder()
            .Append(strategyId.Trim()).Append('|')
            .Append(strategyVersion.Trim()).Append('|')
            .Append(strategyConfigurationFingerprint.Trim()).Append('|')
            .Append(options.InitialCapital.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.PeriodsPerYear.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(options.FeeRate.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.HalfSpreadRate.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.SlippageRate.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.AnnualRiskFreeRate.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.MinimumOrderNotionalQuote.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.MaximumPositionNotionalAtBuyQuote.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(options.ExecutionDelayBars.ToString(CultureInfo.InvariantCulture)).Append('\n');

        for (var index = 0; index < bars.Count; index++)
        {
            canonical.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(bars[index].Symbol.Value).Append('|')
                .Append(bars[index].CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(bars[index].ClosePrice.Value.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
                .Append(signals[index].Action).Append('|')
                .Append(signals[index].BaseQuantity?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
