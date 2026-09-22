using System.Globalization;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Portfolio;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class PortfolioNettingEngineTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void EmptyIntentsReturnsZeroProposal()
    {
        var proposal = PortfolioNettingEngine.NetIntents(BtcUsdt, []);

        Assert.Null(proposal.ActionSide);
        Assert.Equal(0m, proposal.NettedQuantity);
        Assert.Equal(0m, proposal.SavedTurnoverNotional);
        Assert.Equal(0m, proposal.EstimatedFeeSavings);
    }

    [Fact]
    public void OpposingIntentsNetOutAndCalculateTurnoverAndFeeSavings()
    {
        // Strategy A wants to buy 1.5 BTC at 60,000 USDT.
        // Strategy B wants to sell 1.0 BTC at 60,000 USDT.
        var intentA = new StrategyOrderIntent("trend", BtcUsdt, OrderSide.Buy, 1.5m, 60_000m, TestTime);
        var intentB = new StrategyOrderIntent("mean-rev", BtcUsdt, OrderSide.Sell, 1.0m, 60_000m, TestTime);

        var proposal = PortfolioNettingEngine.NetIntents(BtcUsdt, [intentA, intentB], feeRate: 0.001m);

        // Netting results: Buy 0.5 BTC.
        Assert.Equal(OrderSide.Buy, proposal.ActionSide);
        Assert.Equal(0.5m, proposal.NettedQuantity);
        Assert.Equal(1.5m, proposal.GrossBuyQuantity);
        Assert.Equal(1.0m, proposal.GrossSellQuantity);

        // Saved turnover = 2 * 1.0 BTC * 60,000 = 120,000 USDT avoided from round-trip exchange execution.
        Assert.Equal(120_000m, proposal.SavedTurnoverNotional);
        // Saved fees = 120,000 * 0.001 = 120 USDT.
        Assert.Equal(120m, proposal.EstimatedFeeSavings);
    }

    [Fact]
    public void DynamicRiskBudgetAllocatorZeroesBudgetInChop()
    {
        var allocations = DynamicRiskBudgetAllocator.Allocate(MarketRegime.HighVolatilityChop, 10_000m);

        Assert.All(allocations, a =>
        {
            Assert.Equal(0m, a.AllocationPercentage);
            Assert.Equal(0m, a.AllocatedNotional);
        });
    }

    [Fact]
    public void DynamicRiskBudgetAllocatorFavorsTrendInBull()
    {
        var allocations = DynamicRiskBudgetAllocator.Allocate(MarketRegime.BullTrend, 10_000m);

        var trend = allocations.First(a => a.StrategyId == "trend-following-btcusdt");
        var meanRev = allocations.First(a => a.StrategyId == "mean-reversion-btcusdt");

        Assert.Equal(0.60m, trend.AllocationPercentage);
        Assert.Equal(6_000m, trend.AllocatedNotional);
        Assert.Equal(0.10m, meanRev.AllocationPercentage);
    }

    [Fact]
    public void DynamicRiskBudgetAllocatorFavorsMeanReversionInRange()
    {
        var allocations = DynamicRiskBudgetAllocator.Allocate(MarketRegime.MeanRevertingRange, 10_000m);

        var meanRev = allocations.First(a => a.StrategyId == "mean-reversion-btcusdt");
        var grid = allocations.First(a => a.StrategyId == "adaptive-grid-btcusdt");

        Assert.Equal(0.50m, meanRev.AllocationPercentage);
        Assert.Equal(5_000m, meanRev.AllocatedNotional);
        Assert.Equal(0.40m, grid.AllocationPercentage);
        Assert.Equal(4_000m, grid.AllocatedNotional);
    }
}
