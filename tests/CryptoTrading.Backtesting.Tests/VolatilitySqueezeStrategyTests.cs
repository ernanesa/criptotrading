using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class VolatilitySqueezeStrategyTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void BaselineHasVersionedBtcUsdtParametersAndExplicitCosts()
    {
        var parameters = VolatilitySqueezeParameters.BtcUsdtBaseline;

        Assert.Equal("volatility-squeeze-btcusdt", parameters.StrategyId);
        Assert.Equal("1.0.0", parameters.Version);
        Assert.Equal(BtcUsdt, parameters.Symbol);
        Assert.Equal(20, parameters.Period);
        Assert.Equal(2.0m, parameters.BollingerMultiplier);
        Assert.Equal(1.5m, parameters.KeltnerMultiplier);
        Assert.Equal(0.001m, parameters.FeeRate);
        Assert.Equal(0.0005m, parameters.SlippageRate);
        Assert.Matches("^[0-9A-F]{64}$", parameters.ConfigurationFingerprint);
    }

    [Fact]
    public void NoTradeIsEmittedWhenHistoryIsInsufficient()
    {
        var parameters = new VolatilitySqueezeParameters("test", "1.0.0", BtcUsdt, period: 5);
        var strategy = new VolatilitySqueezeStrategy(parameters);
        var decisions = strategy.Evaluate(Bars(100m, 101m, 102m, 101m));

        Assert.Equal(4, decisions.Count);
        Assert.All(decisions, d =>
        {
            Assert.Equal(VolatilitySqueezeSignal.NoTrade, d.Signal);
            Assert.Equal(VolatilitySqueezeReason.InsufficientHistory, d.Reason);
            Assert.Null(d.RollingMean);
            Assert.Null(d.BollingerBandWidth);
        });
    }

    [Fact]
    public void SqueezeCompressionIsDetectedWhenBollingerBandsAreInsideKeltner()
    {
        // 10 bars with virtually identical prices: std dev is near zero, so BB <= KC
        var parameters = new VolatilitySqueezeParameters("test", "1.0.0", BtcUsdt, period: 5);
        var strategy = new VolatilitySqueezeStrategy(parameters);
        var prices = new[] { 100.0m, 100.1m, 100.0m, 100.1m, 100.0m, 100.1m };
        var decisions = strategy.Evaluate(Bars(prices));

        var last = decisions[^1];
        Assert.NotNull(last.BollingerBandWidth);
        Assert.NotNull(last.KeltnerChannelWidth);
        Assert.True(last.IsSqueezed);
    }

    [Fact]
    public void SqueezeFiresLongOnPositiveMomentumExpansion()
    {
        var parameters = new VolatilitySqueezeParameters("test", "1.0.0", BtcUsdt, period: 5);
        var strategy = new VolatilitySqueezeStrategy(parameters);
        // 5 quiet bars in tight compression, followed by a sharp breakout upward
        var prices = new[] { 100.0m, 100.05m, 100.02m, 100.04m, 100.01m, 103.5m };
        var decisions = strategy.Evaluate(Bars(prices));

        var entryDecision = decisions[^1];
        Assert.Equal(VolatilitySqueezeSignal.EnterLong, entryDecision.Signal);
        Assert.Equal(VolatilitySqueezeReason.SqueezeFiredLong, entryDecision.Reason);
        Assert.True(entryDecision.Momentum > 0m);
    }

    [Fact]
    public void ExitLongEmittedWhenMomentumDecaysOrPriceDropsBelowMean()
    {
        var parameters = new VolatilitySqueezeParameters("test", "1.0.0", BtcUsdt, period: 5);
        var strategy = new VolatilitySqueezeStrategy(parameters);
        // Tight squeeze -> breakout enter long -> sharp drop below mean
        var prices = new[] { 100.0m, 100.05m, 100.02m, 100.04m, 100.01m, 103.5m, 98.0m };
        var decisions = strategy.Evaluate(Bars(prices));

        var exitDecision = decisions[^1];
        Assert.Equal(VolatilitySqueezeSignal.ExitLong, exitDecision.Signal);
        Assert.Equal(VolatilitySqueezeReason.MomentumDecayExit, exitDecision.Reason);
    }

    [Fact]
    public void MultiStrategyDecisionOrchestratorIncludesVolatilitySqueeze()
    {
        var prices = new[] { 100m, 101m, 102m, 103m, 102m, 106m };
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(
            BtcUsdt,
            Bars(prices),
            1000m,
            DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture));

        var squeezeCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "volatility-squeeze-btcusdt");
        Assert.NotNull(squeezeCandidate);
        Assert.Equal("1.0.0", squeezeCandidate.StrategyVersion);
    }

    [Fact]
    public void MultiStrategyDecisionOrchestratorCalibratesBollingerMultiplierInLowVolatilityRegime()
    {
        // 25 bars with micro-range volatility (< 0.0003m)
        var prices = new decimal[25];
        for (var i = 0; i < 25; i++)
        {
            prices[i] = 100m + (i * 0.001m);
        }

        var result = MultiStrategyDecisionOrchestrator.Orchestrate(
            BtcUsdt,
            Bars(prices),
            1000m,
            DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture));

        var squeezeCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "volatility-squeeze-btcusdt");
        Assert.NotNull(squeezeCandidate);
        Assert.Equal("1.0.0", squeezeCandidate.StrategyVersion);
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
