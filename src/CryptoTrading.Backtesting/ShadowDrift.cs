namespace CryptoTrading.Backtesting;

/// <summary>One redacted observation emitted by a local Shadow/Paper comparison.</summary>
public sealed record ShadowObservation(
    long Sequence,
    DateTimeOffset EventTimeUtc,
    string ExpectedDecisionFingerprint,
    string ObservedExecutionFingerprint,
    bool IsStale,
    bool Recovered)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Sequence);
        if (EventTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Shadow event time must be UTC.", nameof(EventTimeUtc));
        }

        Require(ExpectedDecisionFingerprint, nameof(ExpectedDecisionFingerprint));
        Require(ObservedExecutionFingerprint, nameof(ObservedExecutionFingerprint));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A fingerprint is required.", name);
        }
    }
}

/// <summary>Deterministic local Shadow drift metrics; it never submits or cancels orders.</summary>
public sealed record ShadowRunResult(
    int ObservationCount,
    int DivergenceCount,
    int StaleCount,
    int RecoveryCount,
    decimal DivergenceRate,
    decimal StaleRate);

public static class ShadowDriftEvaluator
{
    public static ShadowRunResult Evaluate(IEnumerable<ShadowObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var materialized = observations.ToArray();
        long previousSequence = -1;
        foreach (var observation in materialized)
        {
            observation.Validate();
            if (observation.Sequence <= previousSequence)
            {
                throw new InvalidOperationException("Shadow observations must have strictly increasing sequence numbers.");
            }

            previousSequence = observation.Sequence;
        }

        var divergenceCount = materialized.Count(static item =>
            !string.Equals(item.ExpectedDecisionFingerprint, item.ObservedExecutionFingerprint, StringComparison.Ordinal));
        var staleCount = materialized.Count(static item => item.IsStale);
        var recoveryCount = materialized.Count(static item => item.Recovered);
        var denominator = materialized.Length;
        return new(
            denominator,
            divergenceCount,
            staleCount,
            recoveryCount,
            denominator == 0 ? 0m : (decimal)divergenceCount / denominator,
            denominator == 0 ? 0m : (decimal)staleCount / denominator);
    }
}
