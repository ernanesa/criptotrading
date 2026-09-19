using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ShadowSoakTests
{
    [Fact]
    [Trait("Category", "ShadowSoak")]
    public void DeterministicSoakCapturesExpectedDriftAndRecovery()
    {
        const int observationCount = 64;
        var symbol = new Symbol("BTCUSDT");
        var prices = Enumerable.Range(0, observationCount)
            .Select(index => 100m + ((index % 8) * 3m) + (index / 8m))
            .ToArray();
        var bars = prices.Select((price, index) => new CloseBar(
            symbol,
            DateTimeOffset.UnixEpoch.AddMinutes(index),
            price)).ToArray();
        var parameters = new TrendFollowingParameters("shadow-soak", "1.0.0", symbol, 2, 3, 0m, 0m);
        var decisions = new TrendFollowingStrategy(parameters).Evaluate(bars);
        var observations = decisions.Select(decision => new ShadowExecutionObservation(
            decision.BarIndex,
            decision.CloseTimeUtc,
            decision.BarIndex % 17 == 0 ? "synthetic-drift" : ShadowRunRunner.Fingerprint(decision),
            IsStale: decision.BarIndex % 11 == 0,
            Recovered: decision.BarIndex > 0 && (decision.BarIndex - 1) % 11 == 0)).ToArray();

        var result = ShadowRunRunner.Run(decisions, observations);

        Assert.Equal(observationCount, result.ObservationCount);
        Assert.Equal(4, result.DivergenceCount);
        Assert.Equal(6, result.StaleCount);
        Assert.Equal(6, result.RecoveryCount);
        Assert.Equal(4m / observationCount, result.DivergenceRate);
        Assert.Equal(6m / observationCount, result.StaleRate);
    }
}
