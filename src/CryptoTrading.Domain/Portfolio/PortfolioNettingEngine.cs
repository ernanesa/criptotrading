using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;

namespace CryptoTrading.Domain.Portfolio;

/// <summary>Intent emitted by an individual strategy during multi-strategy portfolio coordination.</summary>
public sealed record StrategyOrderIntent(
    string StrategyId,
    Symbol Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal ReferencePrice,
    DateTimeOffset OccurredAtUtc)
{
    public decimal Notional => Quantity * ReferencePrice;
}

/// <summary>Consolidated netted order proposal minimizing turnover and exchange execution costs.</summary>
public sealed record NettedOrderProposal(
    Symbol Symbol,
    OrderSide? ActionSide,
    decimal NettedQuantity,
    decimal GrossBuyQuantity,
    decimal GrossSellQuantity,
    decimal SavedTurnoverNotional,
    decimal EstimatedFeeSavings,
    IReadOnlyList<string> ContributingStrategies);

/// <summary>
/// Deterministic netting engine consolidating competing strategy signals.
/// Eliminates conflicting intra-portfolio turnover before orders touch the OMS.
/// </summary>
public static class PortfolioNettingEngine
{
    public static NettedOrderProposal NetIntents(
        Symbol symbol,
        IReadOnlyList<StrategyOrderIntent> intents,
        decimal feeRate = 0.001m)
    {
        ArgumentNullException.ThrowIfNull(intents);
        if (intents.Count == 0)
        {
            return new NettedOrderProposal(
                symbol,
                ActionSide: null,
                NettedQuantity: 0m,
                GrossBuyQuantity: 0m,
                GrossSellQuantity: 0m,
                SavedTurnoverNotional: 0m,
                EstimatedFeeSavings: 0m,
                ContributingStrategies: []);
        }

        decimal grossBuy = 0m;
        decimal grossSell = 0m;
        decimal referencePrice = 0m;
        var strategies = new List<string>(intents.Count);

        for (var i = 0; i < intents.Count; i++)
        {
            var intent = intents[i];
            if (intent.Symbol != symbol)
            {
                throw new ArgumentException($"Intent symbol {intent.Symbol} mismatch with {symbol}.", nameof(intents));
            }

            if (intent.Quantity <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(intents), "Intent quantity must be positive.");
            }

            referencePrice = intent.ReferencePrice;
            strategies.Add(intent.StrategyId);

            if (intent.Side == OrderSide.Buy)
            {
                grossBuy += intent.Quantity;
            }
            else
            {
                grossSell += intent.Quantity;
            }
        }

        var netDiff = grossBuy - grossSell;
        OrderSide? actionSide = null;
        decimal nettedQty = 0m;

        if (netDiff > 0m)
        {
            actionSide = OrderSide.Buy;
            nettedQty = netDiff;
        }
        else if (netDiff < 0m)
        {
            actionSide = OrderSide.Sell;
            nettedQty = Math.Abs(netDiff);
        }

        var offsetQty = Math.Min(grossBuy, grossSell);
        var savedTurnover = 2m * offsetQty * referencePrice;
        var savedFee = savedTurnover * feeRate;

        return new NettedOrderProposal(
            symbol,
            actionSide,
            nettedQty,
            grossBuy,
            grossSell,
            savedTurnover,
            savedFee,
            strategies);
    }
}

/// <summary>Allocation share for an individual strategy.</summary>
public sealed record StrategyBudgetShare(
    string StrategyId,
    decimal AllocationPercentage,
    decimal AllocatedNotional);

/// <summary>
/// Allocates risk capital budget across strategies depending on active market regime.
/// </summary>
public static class DynamicRiskBudgetAllocator
{
    public static IReadOnlyList<StrategyBudgetShare> Allocate(
        MarketRegime regime,
        decimal totalRiskBudgetNotional)
    {
        if (totalRiskBudgetNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRiskBudgetNotional), "Risk budget notional cannot be negative.");
        }

        if (regime is MarketRegime.HighVolatilityChop or MarketRegime.IlliquidOrStale || totalRiskBudgetNotional == 0m)
        {
            return
            [
                new StrategyBudgetShare("trend-following-btcusdt", 0.0m, 0m),
                new StrategyBudgetShare("breakout-momentum-btcusdt", 0.0m, 0m),
                new StrategyBudgetShare("mean-reversion-btcusdt", 0.0m, 0m),
                new StrategyBudgetShare("adaptive-grid-btcusdt", 0.0m, 0m),
            ];
        }

        if (regime == MarketRegime.BullTrend)
        {
            return
            [
                new StrategyBudgetShare("trend-following-btcusdt", 0.60m, totalRiskBudgetNotional * 0.60m),
                new StrategyBudgetShare("breakout-momentum-btcusdt", 0.30m, totalRiskBudgetNotional * 0.30m),
                new StrategyBudgetShare("mean-reversion-btcusdt", 0.10m, totalRiskBudgetNotional * 0.10m),
                new StrategyBudgetShare("adaptive-grid-btcusdt", 0.00m, 0m),
            ];
        }

        if (regime == MarketRegime.BearTrend)
        {
            return
            [
                new StrategyBudgetShare("trend-following-btcusdt", 0.40m, totalRiskBudgetNotional * 0.40m),
                new StrategyBudgetShare("breakout-momentum-btcusdt", 0.20m, totalRiskBudgetNotional * 0.20m),
                new StrategyBudgetShare("mean-reversion-btcusdt", 0.00m, 0m),
                new StrategyBudgetShare("adaptive-grid-btcusdt", 0.00m, 0m),
            ];
        }

        // MarketRegime.MeanRevertingRange
        return
        [
            new StrategyBudgetShare("mean-reversion-btcusdt", 0.50m, totalRiskBudgetNotional * 0.50m),
            new StrategyBudgetShare("adaptive-grid-btcusdt", 0.40m, totalRiskBudgetNotional * 0.40m),
            new StrategyBudgetShare("trend-following-btcusdt", 0.10m, totalRiskBudgetNotional * 0.10m),
            new StrategyBudgetShare("breakout-momentum-btcusdt", 0.00m, 0m),
        ];
    }
}
