using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class AdaptiveGridStrategyTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void BaselineHasVersionedParametersAndExplicitCosts()
    {
        var parameters = AdaptiveGridParameters.BtcUsdtBaseline;

        Assert.Equal("adaptive-grid-btcusdt", parameters.StrategyId);
        Assert.Equal("1.0.0", parameters.Version);
        Assert.Equal(BtcUsdt, parameters.Symbol);
        Assert.Equal(0.015m, parameters.GridSpacingRate);
        Assert.Equal(5, parameters.MaxLevels);
        Assert.Equal(0.01m, parameters.QuantityPerLevel);
        Assert.Equal(0.05m, parameters.MaxInventoryQuantity);
        Assert.Equal(0.001m, parameters.FeeRate);
        Assert.Equal(0.0005m, parameters.SlippageRate);
        Assert.Matches("^[0-9A-F]{64}$", parameters.ConfigurationFingerprint);
    }

    [Fact]
    public void InitialBarInitializesGridAtZeroLevel()
    {
        var parameters = new AdaptiveGridParameters("grid-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.03m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bars = Bars(100m);

        var decisions = strategy.Evaluate(bars);

        Assert.Single(decisions);
        Assert.Equal(GridSignal.NoTrade, decisions[0].Signal);
        Assert.Equal(GridReason.Initialized, decisions[0].Reason);
        Assert.Equal(0, decisions[0].CurrentGridLevel);
        Assert.Equal(0m, decisions[0].CurrentInventory);
    }

    [Fact]
    public void DroppingPriceBuysGridLevelsAndRespectsInventoryCap()
    {
        // Spacing: 2% (0.02m). Anchor is 100m.
        // Level -1: <= 98m
        // Level -2: <= 96m
        // Level -3: <= 94m
        // Max inventory: 0.02m (2 levels of 0.01m).
        var parameters = new AdaptiveGridParameters("grid-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.02m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bars = Bars(100m, 97.5m, 95.5m, 93m);

        var decisions = strategy.Evaluate(bars);

        // Bar 0 (100): init
        Assert.Equal(GridSignal.NoTrade, decisions[0].Signal);

        // Bar 1 (97.5): drops into level -2 (floor of -2.5 / 2 = -2). Buys level!
        Assert.Equal(GridSignal.BuyGridLevel, decisions[1].Signal);
        Assert.Equal(GridReason.BuyLevelTriggered, decisions[1].Reason);
        Assert.Equal(0.01m, decisions[1].CurrentInventory);

        // Bar 2 (95.5): drops into level -3 (floor of -4.5 / 2 = -3). Buys level!
        Assert.Equal(GridSignal.BuyGridLevel, decisions[2].Signal);
        Assert.Equal(0.02m, decisions[2].CurrentInventory);

        // Bar 3 (93): drops into level -4 (floor of -7 / 2 = -4). Inventory is already 0.02 (max)!
        Assert.Equal(GridSignal.NoTrade, decisions[3].Signal);
        Assert.Equal(GridReason.InventoryCapReached, decisions[3].Reason);
        Assert.Equal(0.02m, decisions[3].CurrentInventory);
    }

    [Fact]
    public void RisingPriceSellsGridLevelAndReducesInventory()
    {
        // Spacing: 2%.
        // Bar 0: 100
        // Bar 1: 97.5 -> buys 0.01m, active level -2
        // Bar 2: 100 -> rises back into level 0! Sells 0.01m!
        var parameters = new AdaptiveGridParameters("grid-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.05m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bars = Bars(100m, 97.5m, 100m);

        var decisions = strategy.Evaluate(bars);

        Assert.Equal(GridSignal.BuyGridLevel, decisions[1].Signal);
        Assert.Equal(0.01m, decisions[1].CurrentInventory);

        Assert.Equal(GridSignal.SellGridLevel, decisions[2].Signal);
        Assert.Equal(GridReason.SellLevelTriggered, decisions[2].Reason);
        Assert.Equal(0m, decisions[2].CurrentInventory);
    }

    [Theory]
    [InlineData(0.0, 5, 0.01, 0.05)] // spacing <= 0
    [InlineData(0.02, 0, 0.01, 0.05)] // levels < 1
    [InlineData(0.02, 5, 0.0, 0.05)] // qty <= 0
    [InlineData(0.02, 5, 0.02, 0.01)] // max inventory < qty
    public void ConstructorThrowsOnInvalidParameters(decimal spacing, int levels, decimal qty, decimal maxInv)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new AdaptiveGridParameters("test", "1.0", BtcUsdt, spacing, levels, qty, maxInv, 0.001m, 0.0005m));
    }

    private static List<CloseBar> Bars(params decimal[] prices)
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);
        var list = new List<CloseBar>(prices.Length);
        for (var i = 0; i < prices.Length; i++)
        {
            list.Add(new CloseBar(BtcUsdt, start.AddMinutes(i), prices[i]));
        }

        return list;
    }
}
