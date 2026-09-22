using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class BreakoutStrategyTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void BaselineHasVersionedParametersAndExplicitCosts()
    {
        var parameters = BreakoutParameters.BtcUsdtBaseline;

        Assert.Equal("breakout-momentum-btcusdt", parameters.StrategyId);
        Assert.Equal("1.0.0", parameters.Version);
        Assert.Equal(BtcUsdt, parameters.Symbol);
        Assert.Equal(20, parameters.EntryPeriod);
        Assert.Equal(10, parameters.ExitPeriod);
        Assert.Equal(0.001m, parameters.FeeRate);
        Assert.Equal(0.0005m, parameters.SlippageRate);
        Assert.Matches("^[0-9A-F]{64}$", parameters.ConfigurationFingerprint);
    }

    [Fact]
    public void NoTradeEmittedUntilEntryPeriodIsSaturated()
    {
        var parameters = new BreakoutParameters("bo-test", "1.0", BtcUsdt, 5, 2, 0.001m, 0.0005m);
        var strategy = new BreakoutStrategy(parameters);
        var bars = Bars(100m, 102m, 101m, 103m, 102m);

        var decisions = strategy.Evaluate(bars);

        Assert.Equal(5, decisions.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(BreakoutSignal.NoTrade, decisions[i].Signal);
            Assert.Equal(BreakoutReason.InsufficientHistory, decisions[i].Reason);
        }
    }

    [Fact]
    public void BreakoutAboveChannelEmitsEnterLong()
    {
        // 5 bars lookback: 100, 102, 101, 103, 102. High of past 5 is 103.
        // Bar 5 (6th bar) breaks above with 105.
        var parameters = new BreakoutParameters("bo-test", "1.0", BtcUsdt, 5, 2, 0.001m, 0.0005m);
        var strategy = new BreakoutStrategy(parameters);
        var bars = Bars(100m, 102m, 101m, 103m, 102m, 105m);

        var decisions = strategy.Evaluate(bars);
        var lastDecision = decisions[5];

        Assert.Equal(103m, lastDecision.EntryChannelHigh);
        Assert.Equal(BreakoutSignal.EnterLong, lastDecision.Signal);
        Assert.Equal(BreakoutReason.ChannelBreakoutAbove, lastDecision.Reason);
    }

    [Fact]
    public void DroppingBelowExitChannelEmitsExitLong()
    {
        // 5 bars lookback for entry, 2 bars for exit.
        // Bars: 100, 102, 101, 103, 102 (high is 103).
        // Bar 5: 105 -> EnterLong. Exit channel low of previous 2 bars (102, 105) is 102.
        // Bar 6: 101 (drops below 102) -> ExitLong.
        var parameters = new BreakoutParameters("bo-test", "1.0", BtcUsdt, 5, 2, 0.001m, 0.0005m);
        var strategy = new BreakoutStrategy(parameters);
        var bars = Bars(100m, 102m, 101m, 103m, 102m, 105m, 101m);

        var decisions = strategy.Evaluate(bars);

        Assert.Equal(BreakoutSignal.EnterLong, decisions[5].Signal);
        Assert.Equal(BreakoutSignal.ExitLong, decisions[6].Signal);
        Assert.Equal(BreakoutReason.ChannelDroppedBelowExit, decisions[6].Reason);
    }

    [Theory]
    [InlineData(2, 1)] // entry period < 3
    [InlineData(5, 1)] // exit period < 2
    [InlineData(5, 5)] // exit period >= entry period
    public void ConstructorThrowsOnInvalidPeriodParameters(int entry, int exit)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new BreakoutParameters("test", "1.0", BtcUsdt, entry, exit, 0.001m, 0.0005m));
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
