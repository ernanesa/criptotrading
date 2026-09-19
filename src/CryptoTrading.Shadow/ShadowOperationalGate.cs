using CryptoTrading.Backtesting;

namespace CryptoTrading.Shadow;

/// <summary>Explicit local quality thresholds for a Shadow observation window.</summary>
public sealed record ShadowOperationalThresholds
{
    public int MinimumObservations { get; init; } = 1;

    public decimal MaximumDivergenceRate { get; init; }

    public decimal MaximumStaleRate { get; init; } = 0.05m;

    public decimal MaximumInterventionRate { get; init; } = 0.02m;

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(5);

    public bool RequireRecoveryAfterStale { get; init; } = true;

    public void Validate()
    {
        if (MinimumObservations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumObservations));
        }

        ValidateRate(MaximumDivergenceRate, nameof(MaximumDivergenceRate));
        ValidateRate(MaximumStaleRate, nameof(MaximumStaleRate));
        ValidateRate(MaximumInterventionRate, nameof(MaximumInterventionRate));
        if (MaximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        }
    }

    private static void ValidateRate(decimal value, string name)
    {
        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(name, "The rate must be between zero and one.");
        }
    }
}

/// <summary>Redacted metrics for one local Shadow operational window.</summary>
public sealed record ShadowOperationalMetrics(
    int ObservationCount,
    int DivergenceCount,
    int StaleCount,
    int RecoveryCount,
    int InterventionCount,
    TimeSpan Duration)
{
    public decimal DivergenceRate => Rate(DivergenceCount);

    public decimal StaleRate => Rate(StaleCount);

    public decimal InterventionRate => Rate(InterventionCount);

    public static ShadowOperationalMetrics From(
        ShadowRunResult drift,
        TimeSpan duration,
        int interventionCount)
    {
        ArgumentNullException.ThrowIfNull(drift);
        var metrics = new ShadowOperationalMetrics(
            drift.ObservationCount,
            drift.DivergenceCount,
            drift.StaleCount,
            drift.RecoveryCount,
            interventionCount,
            duration);
        metrics.Validate();
        return metrics;
    }

    public void Validate()
    {
        if (ObservationCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservationCount));
        }

        if (DivergenceCount is < 0 || StaleCount is < 0 || RecoveryCount is < 0 ||
            InterventionCount is < 0 || DivergenceCount > ObservationCount ||
            StaleCount > ObservationCount || RecoveryCount > ObservationCount ||
            InterventionCount > ObservationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(DivergenceCount), "Shadow metric counts are outside the observation bound.");
        }

        if (Duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration));
        }
    }

    private decimal Rate(int count) => ObservationCount == 0 ? 0m : (decimal)count / ObservationCount;
}

public sealed record ShadowOperationalGateResult(
    bool Passed,
    IReadOnlyList<string> FailureReasons);

/// <summary>
/// Evaluates explicit Shadow operational thresholds. It never authorizes an
/// exchange action; a failed gate is evidence for NoTrade/SafeMode only.
/// </summary>
public static class ShadowOperationalGate
{
    public static ShadowOperationalGateResult Evaluate(
        ShadowOperationalMetrics metrics,
        ShadowOperationalThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(thresholds);
        metrics.Validate();
        thresholds.Validate();

        var failures = new List<string>();
        if (metrics.ObservationCount < thresholds.MinimumObservations)
        {
            failures.Add("MINIMUM_OBSERVATIONS");
        }

        if (metrics.DivergenceRate > thresholds.MaximumDivergenceRate)
        {
            failures.Add("DIVERGENCE_RATE");
        }

        if (metrics.StaleRate > thresholds.MaximumStaleRate)
        {
            failures.Add("STALE_RATE");
        }

        if (metrics.InterventionRate > thresholds.MaximumInterventionRate)
        {
            failures.Add("INTERVENTION_RATE");
        }

        if (metrics.Duration > thresholds.MaximumDuration)
        {
            failures.Add("DURATION");
        }

        if (thresholds.RequireRecoveryAfterStale && metrics.StaleCount > 0 && metrics.RecoveryCount == 0)
        {
            failures.Add("RECOVERY_MISSING");
        }

        return new(failures.Count == 0, failures.AsReadOnly());
    }
}
