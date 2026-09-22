using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MeanReversionStrategyTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void BaselineHasVersionedParametersAndExplicitCosts()
    {
        var parameters = MeanReversionParameters.BtcUsdtBaseline;

        Assert.Equal("mean-reversion-btcusdt", parameters.StrategyId);
        Assert.Equal("1.0.0", parameters.Version);
        Assert.Equal(BtcUsdt, parameters.Symbol);
        Assert.Equal(20, parameters.Period);
        Assert.Equal(-1.8m, parameters.EntryZScoreThreshold);
        Assert.Equal(0.0m, parameters.ExitZScoreThreshold);
        Assert.Equal(0.05m, parameters.MaxAllowedVolatilityRate);
        Assert.Equal(0.001m, parameters.FeeRate);
        Assert.Equal(0.0005m, parameters.SlippageRate);
        Assert.Matches("^[0-9A-F]{64}$", parameters.ConfigurationFingerprint);
    }

    [Fact]
    public void NoTradeEmittedUntilPeriodIsSaturated()
    {
        var parameters = new MeanReversionParameters("mr-test", "1.0", BtcUsdt, 5, -1.5m, 0.0m, 0.10m, 0.001m, 0.0005m);
        var strategy = new MeanReversionStrategy(parameters);
        var bars = Bars(100m, 101m, 100m, 99m, 95m);

        var decisions = strategy.Evaluate(bars);

        Assert.Equal(5, decisions.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(MeanReversionSignal.NoTrade, decisions[i].Signal);
            Assert.Equal(MeanReversionReason.InsufficientHistory, decisions[i].Reason);
            Assert.Null(decisions[i].ZScore);
        }

        Assert.NotNull(decisions[4].ZScore);
    }

    [Fact]
    public void OversoldZScoreEmitsEnterLongWhenRegimeGuardIsClear()
    {
        // 5 periods. Prices: 100, 100, 100, 100. Average is 100, std dev is small. Then a dip to 95.
        // Volatility is (stdDev / mean) which is well under 0.20m (20%).
        var parameters = new MeanReversionParameters("mr-test", "1.0", BtcUsdt, 5, -1.5m, 0.0m, 0.20m, 0.001m, 0.0005m);
        var strategy = new MeanReversionStrategy(parameters);
        var bars = Bars(100m, 100m, 100m, 100m, 95m);

        var decisions = strategy.Evaluate(bars);
        var lastDecision = decisions[4];

        Assert.True(lastDecision.ZScore < -1.5m, $"ZScore {lastDecision.ZScore} should be < -1.5");
        Assert.False(lastDecision.RegimeGuardTriggered);
        Assert.Equal(MeanReversionSignal.EnterLong, lastDecision.Signal);
        Assert.Equal(MeanReversionReason.ZScoreOversold, lastDecision.Reason);
    }

    [Fact]
    public void RegimeGuardBlocksEnterLongWhenVolatilityIsExcessive()
    {
        // Very tight maxAllowedVolatilityRate (e.g. 0.001 = 0.1%).
        // A drop to 95 will create volatility > 0.1%, triggering the guard.
        var parameters = new MeanReversionParameters("mr-test", "1.0", BtcUsdt, 5, -1.5m, 0.0m, 0.001m, 0.001m, 0.0005m);
        var strategy = new MeanReversionStrategy(parameters);
        var bars = Bars(100m, 100m, 100m, 100m, 95m);

        var decisions = strategy.Evaluate(bars);
        var lastDecision = decisions[4];

        Assert.True(lastDecision.ZScore < -1.5m);
        Assert.True(lastDecision.RegimeGuardTriggered);
        Assert.Equal(MeanReversionSignal.NoTrade, lastDecision.Signal);
        Assert.Equal(MeanReversionReason.RegimeGuardActive, lastDecision.Reason);
    }

    [Fact]
    public void ExitLongEmittedWhenPriceRevertsBackToMean()
    {
        // Setup a 5-bar window where we enter at bar 4 (price 95), then bar 5 price jumps back to 101.
        var parameters = new MeanReversionParameters("mr-test", "1.0", BtcUsdt, 5, -1.5m, 0.0m, 0.20m, 0.001m, 0.0005m);
        var strategy = new MeanReversionStrategy(parameters);
        var bars = Bars(100m, 100m, 100m, 100m, 95m, 101m);

        var decisions = strategy.Evaluate(bars);

        Assert.Equal(MeanReversionSignal.EnterLong, decisions[4].Signal);
        Assert.Equal(MeanReversionSignal.ExitLong, decisions[5].Signal);
        Assert.Equal(MeanReversionReason.ZScoreRevertedToMean, decisions[5].Reason);
    }

    [Theory]
    [InlineData(2, -1.5, 0.0, 0.05)] // period < 3
    [InlineData(5, 0.5, 0.0, 0.05)]  // entry threshold >= exit threshold
    [InlineData(5, -1.5, 0.0, 0.0)]  // max volatility <= 0
    public void ConstructorThrowsOnInvalidParameters(int period, decimal entry, decimal exit, decimal maxVol)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new MeanReversionParameters("test", "1.0", BtcUsdt, period, entry, exit, maxVol, 0.001m, 0.0005m));
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
