using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ShadowDriftTests
{
    private static readonly decimal[] ShadowPrices = [100m, 90m, 80m, 120m];

    [Fact]
    public void ComputesDivergenceStaleAndRecoveryRates()
    {
        var result = ShadowDriftEvaluator.Evaluate([
            Observation(0, "same", "same", stale: false, recovered: false),
            Observation(1, "expected-2", "observed-2", stale: true, recovered: true),
            Observation(2, "same-3", "same-3", stale: false, recovered: false),
            ]);

        Assert.Equal(3, result.ObservationCount);
        Assert.Equal(1, result.DivergenceCount);
        Assert.Equal(1, result.StaleCount);
        Assert.Equal(1, result.RecoveryCount);
        Assert.Equal(1m / 3m, result.DivergenceRate);
        Assert.Equal(1m / 3m, result.StaleRate);
    }

    [Fact]
    public void EmptyRunHasZeroRates()
    {
        var result = ShadowDriftEvaluator.Evaluate([]);

        Assert.Equal(0, result.ObservationCount);
        Assert.Equal(0m, result.DivergenceRate);
        Assert.Equal(0m, result.StaleRate);
    }

    [Fact]
    public void RejectsNonMonotonicSequence()
    {
        var observations = new[]
        {
            Observation(1, "a", "a", false, false),
            Observation(1, "b", "b", false, false),
        };

        Assert.Throws<InvalidOperationException>(() => ShadowDriftEvaluator.Evaluate(observations));
    }

    [Fact]
    public void RejectsNonUtcObservation()
    {
        var observation = new ShadowObservation(
            0,
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(-3)),
            "a",
            "a",
            false,
            false);

        Assert.Throws<ArgumentException>(() => ShadowDriftEvaluator.Evaluate([observation]));
    }

    [Fact]
    public void RunnerAlignsStrategyDecisionsWithExecutionObservations()
    {
        var bars = Enumerable.Range(0, 4)
            .Select(index => new CloseBar(
                new Symbol("BTCUSDT"),
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                ShadowPrices[index]))
            .ToArray();
        var parameters = new TrendFollowingParameters("shadow", "1", new Symbol("BTCUSDT"), 2, 3, 0m, 0m);
        var decisions = new TrendFollowingStrategy(parameters).Evaluate(bars);
        var observations = decisions.Select(decision => new ShadowExecutionObservation(
            decision.BarIndex,
            decision.CloseTimeUtc,
            ShadowRunRunner.Fingerprint(decision),
            IsStale: decision.BarIndex == 2,
            Recovered: decision.BarIndex == 3)).ToArray();

        var result = ShadowRunRunner.Run(decisions, observations);

        Assert.Equal(4, result.ObservationCount);
        Assert.Equal(0, result.DivergenceCount);
        Assert.Equal(1, result.StaleCount);
        Assert.Equal(1, result.RecoveryCount);
    }

    [Fact]
    public void RunnerRejectsMisalignedExecutionObservation()
    {
        var decisions = new TrendFollowingStrategy(
                new TrendFollowingParameters("shadow", "1", new Symbol("BTCUSDT"), 2, 3, 0m, 0m))
            .Evaluate([
                new(new Symbol("BTCUSDT"), DateTimeOffset.UnixEpoch, 100m),
                new(new Symbol("BTCUSDT"), DateTimeOffset.UnixEpoch.AddMinutes(1), 90m),
                new(new Symbol("BTCUSDT"), DateTimeOffset.UnixEpoch.AddMinutes(2), 80m),
                new(new Symbol("BTCUSDT"), DateTimeOffset.UnixEpoch.AddMinutes(3), 120m),
            ]);
        var decision = decisions[0];

        Assert.Throws<InvalidOperationException>(() => ShadowRunRunner.Run(
            [decision],
            [new ShadowExecutionObservation(9, decision.CloseTimeUtc, "fingerprint", false, false)]));
    }

    private static ShadowObservation Observation(
        long sequence,
        string expected,
        string observed,
        bool stale,
        bool recovered) => new(
            sequence,
            new DateTimeOffset(2026, 9, 19, 15, 0, checked((int)sequence), TimeSpan.Zero),
            expected,
            observed,
            stale,
            recovered);
}
