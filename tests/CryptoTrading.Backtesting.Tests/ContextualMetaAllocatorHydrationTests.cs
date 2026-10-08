using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ContextualMetaAllocatorHydrationTests
{
    [Fact]
    public void HydratedDurablePerformancesInfluenceContextualWeights()
    {
        var allocator = new ContextualMetaAllocator();
        allocator.LoadPerformances(
        [
            new StrategyRegimePerformance("strategy-winner", MarketRegime.BullTrend, 8, 8m, 1m),
            new StrategyRegimePerformance("strategy-loser", MarketRegime.BullTrend, 8, -4m, -0.5m),
        ]);

        var weights = allocator.ComputeWeights(
            MarketRegime.BullTrend,
            ["strategy-winner", "strategy-loser"],
            totalCapital: 100m,
            explorationFactor: 0m);

        Assert.Equal(75m, weights.Single(weight => weight.StrategyId == "strategy-winner").Weight * 100m);
        Assert.Equal(20m, weights.Single(weight => weight.StrategyId == "strategy-loser").Weight * 100m);
    }
}
