using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class StopRiskSizingPolicyTests
{
    [Fact]
    public void PlannedStopFeesAndRoundTripSlippageOnlyReduceTheRequestedNotional()
    {
        var policy = new StopRiskSizingPolicy("sizing-v1", 1m);
        Assert.Equal(50m, policy.CapEntryNotional(1000m, 100m, 20m, 80m));
        Assert.Equal(10m, policy.CapEntryNotional(10m, 100m, 20m, 80m));
    }

    [Fact]
    public void FractionalCapRoundsDownAndDoesNotOverflowForLargeValidBudgets()
    {
        var policy = new StopRiskSizingPolicy("sizing-v1", 1m);
        var capped = policy.CapEntryNotional(1000m, 100m, 20m, 50m);
        Assert.Equal(58.82352941m, capped);
        Assert.True(capped * 170m / 10_000m <= 1m);
        Assert.Equal(decimal.MaxValue, new StopRiskSizingPolicy("large", decimal.MaxValue)
            .CapEntryNotional(decimal.MaxValue, 100m, 20m, 50m));
    }

    [Fact]
    public void PolicyCannotInventAnUnversionedOrNonpositiveRiskBudget()
    {
        Assert.Throws<ArgumentException>(() => new StopRiskSizingPolicy(" ", 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StopRiskSizingPolicy("x", 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StopRiskSizingPolicy("x", 1m).CapEntryNotional(10m, 0m, 1m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StopRiskSizingPolicy("x", 1m).CapEntryNotional(10m, 100m, -1m, 1m));
    }

    [Fact]
    public void QuantityFloorCannotRoundAboveTheNotionalCeiling()
    {
        var quantity = StopRiskSizingPolicy.FloorQuantity(1.00000001m, 3m, 0.00000001m);
        Assert.Equal(0.33333333m, quantity);
        Assert.True(quantity * 3m <= 1.00000001m);
    }
}
