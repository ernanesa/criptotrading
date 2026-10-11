namespace CryptoTrading.Backtesting;

/// <summary>Cost breakdown output from dynamic cost modeling.</summary>
public sealed record DynamicCostBreakdown(
    decimal VolatilityRange,
    decimal BaseSpreadRate,
    decimal DynamicSpreadRate,
    decimal BaseSlippageRate,
    decimal DynamicSlippageRate,
    decimal TakerFeeRate,
    decimal FeeReserveBufferRate,
    decimal TotalEffectiveFeeRate,
    decimal TotalEffectiveAdverseRate);

/// <summary>
/// Dynamic execution cost model that scales spread and slippage with intrabar
/// volatility range and turnover, with a 10 bps taker fee and 25 bps reserve buffer.
/// </summary>
public sealed class DynamicExecutionCostModel
{
    public const decimal DefaultTakerFeeRate = 0.0010m; // 10 bps
    public const decimal DefaultFeeReserveBufferRate = 0.0025m; // 25 bps
    public const decimal DefaultTotalFeeRate = DefaultTakerFeeRate + DefaultFeeReserveBufferRate; // 35 bps

    public DynamicExecutionCostModel(
        decimal baseHalfSpreadRate = 0.0005m,
        decimal baseSlippageRate = 0.0005m,
        decimal takerFeeRate = DefaultTakerFeeRate,
        decimal feeReserveBufferRate = DefaultFeeReserveBufferRate)
    {
        BaseHalfSpreadRate = baseHalfSpreadRate;
        BaseSlippageRate = baseSlippageRate;
        TakerFeeRate = takerFeeRate;
        FeeReserveBufferRate = feeReserveBufferRate;
    }

    public decimal BaseHalfSpreadRate { get; }
    public decimal BaseSlippageRate { get; }
    public decimal TakerFeeRate { get; }
    public decimal FeeReserveBufferRate { get; }
    public decimal TotalEffectiveFeeRate => TakerFeeRate + FeeReserveBufferRate;

    public DynamicCostBreakdown CalculateCosts(ResearchBar bar, decimal orderNotional = 0m)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var low = bar.LowPrice.Value;
        var high = bar.HighPrice.Value;
        var volatilityRange = low > 0m ? (high - low) / low : 0m;

        var barVolumeNotional = bar.Volume * bar.ClosePrice.Value;
        var turnoverRatio = barVolumeNotional > 0m ? Math.Min(1m, orderNotional / barVolumeNotional) : 0m;

        var dynamicSpread = BaseHalfSpreadRate * (1m + (volatilityRange * 5m));
        var dynamicSlippage = BaseSlippageRate * (1m + (volatilityRange * 5m) + (turnoverRatio * 2m));
        var totalAdverse = dynamicSpread + dynamicSlippage;

        return new DynamicCostBreakdown(
            volatilityRange,
            BaseHalfSpreadRate,
            dynamicSpread,
            BaseSlippageRate,
            dynamicSlippage,
            TakerFeeRate,
            FeeReserveBufferRate,
            TotalEffectiveFeeRate,
            totalAdverse);
    }
}

/// <summary>Result of checking candidate strategy viability under a micro-budget ceiling.</summary>
public sealed record BudgetCapacityResult(
    bool IsEligible,
    decimal TotalBudgetQuote,
    decimal PerOrderQuoteNotional,
    decimal MinimumOrderNotionalQuote,
    string? ExclusionReason = null);

/// <summary>
/// Capacity gate for micro-live campaigns enforcing R$ 100 / <= 20 USDT ceiling
/// and exchange MIN_NOTIONAL constraints, excluding multi-level grid slices below 5 USDT.
/// </summary>
public static class StrategyBudgetCapacityGate
{
    public const decimal DefaultMaxBudgetBrl = 100m;
    public const decimal DefaultMaxBudgetUsdt = 20m;
    public const decimal DefaultMinOrderNotionalUsdt = 5m;

    public static BudgetCapacityResult Evaluate(
        string strategyId,
        decimal totalBudgetQuote,
        decimal minimumOrderNotionalQuote = DefaultMinOrderNotionalUsdt,
        decimal budgetCeilingQuote = DefaultMaxBudgetUsdt,
        decimal gridSliceQuoteFraction = 0.10m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        if (totalBudgetQuote > budgetCeilingQuote)
        {
            return new BudgetCapacityResult(
                IsEligible: false,
                TotalBudgetQuote: totalBudgetQuote,
                PerOrderQuoteNotional: totalBudgetQuote,
                MinimumOrderNotionalQuote: minimumOrderNotionalQuote,
                ExclusionReason: $"Total budget {totalBudgetQuote} USDT exceeds capacity ceiling of {budgetCeilingQuote} USDT.");
        }

        decimal perOrderQuoteNotional;
        if (strategyId.StartsWith("adaptive-grid", StringComparison.OrdinalIgnoreCase))
        {
            perOrderQuoteNotional = totalBudgetQuote * gridSliceQuoteFraction;
        }
        else
        {
            perOrderQuoteNotional = totalBudgetQuote;
        }

        if (perOrderQuoteNotional < minimumOrderNotionalQuote)
        {
            return new BudgetCapacityResult(
                IsEligible: false,
                TotalBudgetQuote: totalBudgetQuote,
                PerOrderQuoteNotional: perOrderQuoteNotional,
                MinimumOrderNotionalQuote: minimumOrderNotionalQuote,
                ExclusionReason: $"Per-order slice notional {perOrderQuoteNotional:F2} USDT is below minimum exchange notional {minimumOrderNotionalQuote:F2} USDT.");
        }

        return new BudgetCapacityResult(
            IsEligible: true,
            TotalBudgetQuote: totalBudgetQuote,
            PerOrderQuoteNotional: perOrderQuoteNotional,
            MinimumOrderNotionalQuote: minimumOrderNotionalQuote);
    }
}

