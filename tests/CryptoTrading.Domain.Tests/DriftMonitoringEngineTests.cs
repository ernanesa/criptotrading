using System.Globalization;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class DriftMonitoringEngineTests
{
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void SmallDeviationEvaluatesWithinTolerance()
    {
        // Expected slippage 5 bps (0.0005), observed 5.1 bps (0.00051), threshold 2 bps (0.0002)
        var obs = new DriftObservation(
            "strat-1",
            DriftType.ExecutionSlippageDrift,
            0.0005m,
            0.00051m,
            0.0002m,
            TestTime);

        var result = DriftMonitoringEngine.Evaluate(obs);

        Assert.Equal(DriftSeverity.WithinTolerance, result.Severity);
        Assert.False(result.RequiresQuarantine);
    }

    [Fact]
    public void ModerateDeviationEvaluatesElevatedWarning()
    {
        // Expected 5 bps, observed 6.6 bps (diff 1.6 bps, threshold 2.0 bps: 80% of threshold -> warning)
        var obs = new DriftObservation(
            "strat-1",
            DriftType.ExecutionSlippageDrift,
            0.0005m,
            0.00066m,
            0.0002m,
            TestTime);

        var result = DriftMonitoringEngine.Evaluate(obs);

        Assert.Equal(DriftSeverity.ElevatedWarning, result.Severity);
        Assert.False(result.RequiresQuarantine);
    }

    [Fact]
    public void LargeDeviationTriggersBreachedThresholdAndRequiresQuarantine()
    {
        // Expected 5 bps, observed 8 bps (diff 3 bps > threshold 2 bps)
        var obs = new DriftObservation(
            "strat-1",
            DriftType.ExecutionSlippageDrift,
            0.0005m,
            0.0008m,
            0.0002m,
            TestTime);

        var result = DriftMonitoringEngine.Evaluate(obs);

        Assert.Equal(DriftSeverity.BreachedThreshold, result.Severity);
        Assert.True(result.RequiresQuarantine);
        Assert.Contains("Quarantine recommended", result.Message, StringComparison.Ordinal);
    }
}
