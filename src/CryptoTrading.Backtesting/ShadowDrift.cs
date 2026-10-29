using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;

using CryptoTrading.Exchange.Paper;

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
    decimal StaleRate)
{
    /// <summary>
    /// Realized result of the simulated fills, when the caller supplies it. It is
    /// in-memory simulation evidence, not reconciled account PnL.
    /// </summary>
    public PaperPnlSummary? PaperResult { get; init; }
}

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

/// <summary>Redacted execution-side observation captured from Paper or another local simulator.</summary>
public sealed record ShadowExecutionObservation(
    long Sequence,
    DateTimeOffset EventTimeUtc,
    string ObservedDecisionFingerprint,
    bool IsStale,
    bool Recovered);

/// <summary>
/// Connects strategy decisions to execution observations without giving the
/// runner any exchange command or network capability.
/// </summary>
public static class ShadowRunRunner
{
    public static ShadowRunResult Run(
        IReadOnlyList<TrendFollowingDecision> decisions,
        IReadOnlyList<ShadowExecutionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(observations);
        if (decisions.Count != observations.Count)
        {
            throw new ArgumentException("Each strategy decision must have exactly one execution observation.", nameof(observations));
        }

        var comparison = new ShadowObservation[decisions.Count];
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            var observation = observations[index];
            if (observation.Sequence != decision.BarIndex || observation.EventTimeUtc != decision.CloseTimeUtc)
            {
                throw new InvalidOperationException("Shadow observations must align with the decision bar index and UTC close time.");
            }

            comparison[index] = new(
                observation.Sequence,
                observation.EventTimeUtc,
                Fingerprint(decision),
                observation.ObservedDecisionFingerprint,
                observation.IsStale,
                observation.Recovered);
        }

        return ShadowDriftEvaluator.Evaluate(comparison);
    }

    public static string Fingerprint(TrendFollowingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var canonical = string.Join(
            '|',
            decision.Symbol.Value,
            decision.CloseTimeUtc.ToString("O"),
            decision.BarIndex,
            decision.Signal,
            decision.Reason,
            decision.StrategyId,
            decision.StrategyVersion,
            decision.ConfigurationFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
