namespace CryptoTrading.Domain.Research;

/// <summary>Type of drift measured in production and shadow trading environments.</summary>
public enum DriftType
{
    ExecutionSlippageDrift,
    RealizedVsExpectedReturnDrift,
    VolatilityRegimeDrift,
}

/// <summary>Severity classification resulting from drift evaluation.</summary>
public enum DriftSeverity
{
    WithinTolerance,
    ElevatedWarning,
    BreachedThreshold,
}

/// <summary>Point-in-time observation of expected versus realized financial or model behavior.</summary>
public sealed record DriftObservation(
    string StrategyId,
    DriftType Type,
    decimal ExpectedValue,
    decimal ObservedValue,
    decimal AllowedDeviationThreshold,
    DateTimeOffset ObservedAtUtc)
{
    public decimal AbsoluteDeviation => Math.Abs(ObservedValue - ExpectedValue);
}

/// <summary>Actionable diagnosis resulting from drift assessment.</summary>
public sealed record DriftEvaluationResult(
    DriftSeverity Severity,
    decimal AbsoluteDeviation,
    decimal RelativeDeviationRate,
    bool RequiresQuarantine,
    string Message);

/// <summary>
/// Deterministic drift monitoring engine (US-174).
/// Detects execution drift, return degradation, and regime shifts before risk limits are breached.
/// </summary>
public static class DriftMonitoringEngine
{
    public static DriftEvaluationResult Evaluate(DriftObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.AllowedDeviationThreshold <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "Allowed deviation threshold must be strictly positive.");
        }

        var absDiff = observation.AbsoluteDeviation;
        var relativeRate = observation.ExpectedValue != 0m
            ? absDiff / Math.Abs(observation.ExpectedValue)
            : 0m;

        if (absDiff > observation.AllowedDeviationThreshold)
        {
            return new DriftEvaluationResult(
                DriftSeverity.BreachedThreshold,
                absDiff,
                relativeRate,
                RequiresQuarantine: true,
                $"DRIFT_BREACH: {observation.Type} deviation {absDiff:G4} exceeded allowed threshold {observation.AllowedDeviationThreshold:G4}. Quarantine recommended.");
        }

        if (absDiff > observation.AllowedDeviationThreshold * 0.70m)
        {
            return new DriftEvaluationResult(
                DriftSeverity.ElevatedWarning,
                absDiff,
                relativeRate,
                RequiresQuarantine: false,
                $"DRIFT_WARNING: {observation.Type} deviation {absDiff:G4} approached threshold ({absDiff / observation.AllowedDeviationThreshold:P1} of limit).");
        }

        return new DriftEvaluationResult(
            DriftSeverity.WithinTolerance,
            absDiff,
            relativeRate,
            RequiresQuarantine: false,
            $"DRIFT_OK: {observation.Type} deviation {absDiff:G4} is within normal tolerance.");
    }
}
