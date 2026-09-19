using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class TrendFollowingStrategyTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void BaselineHasVersionedBtcUsdtParametersAndExplicitCosts()
    {
        var parameters = TrendFollowingParameters.BtcUsdtBaseline;

        Assert.Equal("trend-following-btcusdt", parameters.StrategyId);
        Assert.Equal("1.0.0", parameters.Version);
        Assert.Equal(BtcUsdt, parameters.Symbol);
        Assert.Equal(20, parameters.FastPeriod);
        Assert.Equal(50, parameters.SlowPeriod);
        Assert.Equal(0.001m, parameters.FeeRate);
        Assert.Equal(0.0005m, parameters.SlippageRate);
        Assert.Matches("^[0-9A-F]{64}$", parameters.ConfigurationFingerprint);
    }

    [Fact]
    public void NoTradeIsEmittedUntilSlowEmaHasAClosedBarAndPreviousValue()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var decisions = new TrendFollowingStrategy(parameters).Evaluate(Bars(100m, 90m, 80m, 120m));

        Assert.All(decisions.Take(3), decision =>
        {
            Assert.Equal(TrendFollowingSignal.NoTrade, decision.Signal);
            Assert.Equal(TrendFollowingReason.InsufficientHistory, decision.Reason);
        });
    }

    [Fact]
    public void RisingCrossoverEmitsEnterLongOnlyAtTheClosedCrossoverBar()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var decisions = new TrendFollowingStrategy(parameters).Evaluate(Bars(100m, 90m, 80m, 120m));

        Assert.Equal(TrendFollowingSignal.EnterLong, decisions[3].Signal);
        Assert.Equal(TrendFollowingReason.FastCrossedAboveSlow, decisions[3].Reason);
        Assert.Equal(0.003m, decisions[3].EstimatedRoundTripCostRate);
        Assert.Equal(0.001m, decisions[3].FeeRate);
        Assert.Equal(0.0005m, decisions[3].SlippageRate);
    }

    [Fact]
    public void FallingCrossoverEmitsExitLong()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var decisions = new TrendFollowingStrategy(parameters).Evaluate(Bars(100m, 110m, 120m, 80m));

        Assert.Equal(TrendFollowingSignal.ExitLong, decisions[3].Signal);
        Assert.Equal(TrendFollowingReason.FastCrossedBelowSlow, decisions[3].Reason);
    }

    [Fact]
    public void ReplayingTheSameBarsProducesTheSameDecisions()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var strategy = new TrendFollowingStrategy(parameters);

        var first = strategy.Evaluate(Bars(100m, 90m, 80m, 120m));
        var second = strategy.Evaluate(Bars(100m, 90m, 80m, 120m));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AppendingFutureBarsDoesNotChangeEarlierDecisions()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var strategy = new TrendFollowingStrategy(parameters);
        var prefix = Bars(100m, 90m, 80m, 120m);
        var extended = prefix.Append(new CloseBar(BtcUsdt, DateTimeOffset.UnixEpoch.AddMinutes(4), 130m)).ToArray();

        var prefixDecisions = strategy.Evaluate(prefix);
        var extendedDecisions = strategy.Evaluate(extended);

        Assert.Equal(prefixDecisions, extendedDecisions.Take(prefix.Length).ToArray());
    }

    [Fact]
    public void ConfigurationFingerprintChangesWhenCostAssumptionChanges()
    {
        var first = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.001m, 0.0005m);
        var second = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0.002m, 0.0005m);

        Assert.NotEqual(first.ConfigurationFingerprint, second.ConfigurationFingerprint);
    }

    [Fact]
    public void InvalidParametersAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrendFollowingParameters("test", "1", BtcUsdt, 1, 3, 0m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrendFollowingParameters("test", "1", BtcUsdt, 3, 3, 0m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, -0.001m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 1.001m));
    }

    [Fact]
    public void OutOfOrderOrWrongSymbolBarsAreRejected()
    {
        var parameters = new TrendFollowingParameters("test", "1", BtcUsdt, 2, 3, 0m, 0m);
        var strategy = new TrendFollowingStrategy(parameters);

        Assert.Throws<ArgumentException>(() => strategy.Evaluate([
            new(BtcUsdt, DateTimeOffset.Parse("2026-01-01T00:00:02Z", CultureInfo.InvariantCulture), 100m),
            new(BtcUsdt, DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture), 101m)
        ]));

        Assert.Throws<ArgumentException>(() => strategy.Evaluate([
            new(new Symbol("ETHUSDT"), DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture), 100m)
        ]));
    }

    private static CloseBar[] Bars(params decimal[] prices) =>
        prices.Select((price, index) => new CloseBar(
                BtcUsdt,
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                price))
            .ToArray();
}
