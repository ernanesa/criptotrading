using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Futures;

public enum ContractType
{
    PerpetualLinear,
    PerpetualInverse,
    DatedFutures,
}

public enum PositionSide
{
    LongSide,
    ShortSide,
}

public enum MarginMode
{
    Isolated,
    Cross,
}

/// <summary>Specification for a futures derivative contract (US-140).</summary>
public sealed record FuturesContractSpec(
    Symbol Symbol,
    ContractType ContractType,
    decimal ContractMultiplier,
    decimal MaxLeverage,
    decimal MaintenanceMarginRatio,
    decimal MinNotional);

/// <summary>Active futures position state with margin, leverage, and liquidation metrics (US-141, US-142).</summary>
public sealed record FuturesPosition(
    Symbol Symbol,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal Leverage,
    MarginMode MarginMode,
    decimal AllocatedMargin)
{
    public decimal PositionNotional => Math.Round(Quantity * MarkPrice, 4);
    
    public decimal UnrealizedPnl => Side switch
    {
        PositionSide.LongSide => Math.Round(Quantity * (MarkPrice - EntryPrice), 4),
        PositionSide.ShortSide => Math.Round(Quantity * (EntryPrice - MarkPrice), 4),
        _ => 0m
    };

    public decimal ReturnOnEquityRate => AllocatedMargin > 0m
        ? Math.Round(UnrealizedPnl / AllocatedMargin, 4)
        : 0m;

    /// <summary>Calculates deterministic liquidation price for isolated perpetual position.</summary>
    public decimal CalculateLiquidationPrice(decimal maintenanceMarginRatio)
    {
        if (Quantity <= 0m || Leverage <= 0m || EntryPrice <= 0m)
        {
            return 0m;
        }

        if (Side == PositionSide.LongSide)
        {
            var liq = EntryPrice * (1.0m - (1.0m / Leverage) + maintenanceMarginRatio);
            return Math.Max(0m, Math.Round(liq, 2));
        }
        else // ShortSide
        {
            var liq = EntryPrice * (1.0m + (1.0m / Leverage) - maintenanceMarginRatio);
            return Math.Max(0m, Math.Round(liq, 2));
        }
    }
}
