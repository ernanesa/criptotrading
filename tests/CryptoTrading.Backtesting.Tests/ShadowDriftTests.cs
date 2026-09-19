using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ShadowDriftTests
{
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
