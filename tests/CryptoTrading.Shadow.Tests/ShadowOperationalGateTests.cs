using CryptoTrading.Backtesting;
using Xunit;

namespace CryptoTrading.Shadow.Tests;

public sealed class ShadowOperationalGateTests
{
    [Fact]
    [Trait("Category", "ShadowOperational")]
    public void PassesWhenExplicitShadowQualityThresholdsAreMet()
    {
        var metrics = ShadowOperationalMetrics.From(
            new ShadowRunResult(100, 0, 2, 2, 0m, 0.02m),
            TimeSpan.FromSeconds(4),
            interventionCount: 1);

        var result = ShadowOperationalGate.Evaluate(
            metrics,
            new ShadowOperationalThresholds
            {
                MinimumObservations = 100,
                MaximumStaleRate = 0.05m,
                MaximumInterventionRate = 0.02m,
                MaximumDuration = TimeSpan.FromSeconds(5),
            });

        Assert.True(result.Passed);
        Assert.Empty(result.FailureReasons);
        Assert.Equal(0.01m, metrics.InterventionRate);
    }

    [Fact]
    [Trait("Category", "ShadowOperational")]
    public void ReportsEveryFailedQualityGateWithoutAuthorizingExecution()
    {
        var metrics = ShadowOperationalMetrics.From(
            new ShadowRunResult(10, 2, 3, 0, 0.2m, 0.3m),
            TimeSpan.FromSeconds(10),
            interventionCount: 3);

        var result = ShadowOperationalGate.Evaluate(
            metrics,
            new ShadowOperationalThresholds
            {
                MaximumDivergenceRate = 0m,
                MaximumStaleRate = 0.1m,
                MaximumInterventionRate = 0.1m,
                MaximumDuration = TimeSpan.FromSeconds(5),
            });

        Assert.False(result.Passed);
        Assert.Contains("DIVERGENCE_RATE", result.FailureReasons);
        Assert.Contains("STALE_RATE", result.FailureReasons);
        Assert.Contains("INTERVENTION_RATE", result.FailureReasons);
        Assert.Contains("DURATION", result.FailureReasons);
        Assert.Contains("RECOVERY_MISSING", result.FailureReasons);
    }

    [Fact]
    [Trait("Category", "ShadowOperational")]
    public void InvalidMetricCountsAndThresholdsFailAtTheBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowOperationalMetrics.From(
            new ShadowRunResult(2, 3, 0, 0, 0m, 0m),
            TimeSpan.Zero,
            interventionCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShadowOperationalThresholds
        {
            MaximumInterventionRate = 1.1m,
        }.Validate());
    }
}
