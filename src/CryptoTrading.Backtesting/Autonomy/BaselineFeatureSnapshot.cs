using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>
/// Versioned representation of the exact closed-bar inputs and
/// baseline indicator outcome used by one decision cycle. This is a pure
/// replay artifact; persistence is owned by the durable decision boundary.
/// </summary>
public sealed record BaselineFeatureSnapshot(
    string SchemaVersion,
    string ConfigurationFingerprint,
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    DateTimeOffset AvailableAtUtc,
    IReadOnlyList<ClosedBarEnvelope> Bars,
    TrendFollowingDecision Decision,
    string FeatureFingerprint)
{
    public const string CurrentSchemaVersion = "baseline-feature-snapshot.v1";

    public static BaselineFeatureSnapshot Create(
        IReadOnlyList<ClosedBarEnvelope> bars,
        TrendFollowingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(parameters);
        if (bars.Count == 0)
        {
            throw new ArgumentException("At least one closed-bar envelope is required.", nameof(bars));
        }

        long previousSequence = -1;
        DateTimeOffset? previousCloseTime = null;
        DateTimeOffset? previousAvailability = null;
        for (var index = 0; index < bars.Count; index++)
        {
            var envelope = bars[index] ?? throw new ArgumentException("Bar collection cannot contain null values.", nameof(bars));
            envelope.Validate();
            if (envelope.Sequence <= previousSequence)
            {
                throw new ArgumentException("Closed-bar sequences must be strictly increasing.", nameof(bars));
            }

            if (envelope.Bar.Symbol != parameters.Symbol)
            {
                throw new ArgumentException(
                    $"Bar at index {index} has symbol {envelope.Bar.Symbol}; expected {parameters.Symbol}.",
                    nameof(bars));
            }

            if (previousCloseTime is { } priorClose && envelope.Bar.CloseTimeUtc <= priorClose)
            {
                throw new ArgumentException("Closed-bar close times must be strictly increasing.", nameof(bars));
            }

            if (previousAvailability is { } priorAvailability && envelope.AvailableAtUtc < priorAvailability)
            {
                throw new ArgumentException("Closed-bar availability times must be monotonic.", nameof(bars));
            }

            previousSequence = envelope.Sequence;
            previousCloseTime = envelope.Bar.CloseTimeUtc;
            previousAvailability = envelope.AvailableAtUtc;
        }

        var strategy = new TrendFollowingStrategy(parameters);
        var decision = strategy.Evaluate(bars.Select(static envelope => envelope.Bar).ToArray())[^1];
        var fingerprint = ComputeFingerprint(bars, decision, parameters.ConfigurationFingerprint);
        var latest = bars[^1];
        return new(
            CurrentSchemaVersion,
            parameters.ConfigurationFingerprint,
            parameters.Symbol,
            latest.Bar.CloseTimeUtc,
            latest.AvailableAtUtc,
            Array.AsReadOnly(bars.ToArray()),
            decision,
            fingerprint);
    }

    internal void ValidateFor(TrendFollowingParameters parameters, IReadOnlyList<CloseBar> requestedBars)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(requestedBars);
        if (Bars.Count == 0 ||
            !string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(ConfigurationFingerprint, parameters.ConfigurationFingerprint, StringComparison.Ordinal) ||
            Symbol != parameters.Symbol ||
            Bars.Count != requestedBars.Count ||
            Decision.Symbol != Symbol ||
            Decision.CloseTimeUtc != CloseTimeUtc ||
            CloseTimeUtc != Bars[^1].Bar.CloseTimeUtc ||
            AvailableAtUtc != Bars[^1].AvailableAtUtc)
        {
            throw new ArgumentException("Feature snapshot does not match the configured baseline evaluation.", nameof(parameters));
        }

        for (var index = 0; index < Bars.Count; index++)
        {
            if (Bars[index].Bar != requestedBars[index])
            {
                throw new ArgumentException("Feature snapshot bars do not match the decision request.", nameof(requestedBars));
            }
        }

        var expected = Create(Bars, parameters);
        if (!string.Equals(FeatureFingerprint, expected.FeatureFingerprint, StringComparison.Ordinal) || Decision != expected.Decision)
        {
            throw new ArgumentException("Feature snapshot fingerprint or indicator output is invalid.", nameof(parameters));
        }
    }

    private static string ComputeFingerprint(
        IReadOnlyList<ClosedBarEnvelope> bars,
        TrendFollowingDecision decision,
        string configurationFingerprint)
    {
        var canonical = new StringBuilder();
        canonical.Append(CurrentSchemaVersion).Append('|').Append(configurationFingerprint).Append('\n');
        foreach (var envelope in bars)
        {
            // Sequence establishes local quality ordering, but REST windows do
            // not carry a durable sequence. It must not change feature parity.
            canonical.Append(envelope.Bar.Symbol.Value).Append('|')
                .Append(envelope.Bar.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(envelope.Bar.ClosePrice.Value.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
                .Append(envelope.AvailableAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        canonical.Append(decision.BarIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(DecimalText(decision.FastEma)).Append('|')
            .Append(DecimalText(decision.SlowEma)).Append('|')
            .Append(decision.Signal).Append('|').Append(decision.Reason);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string DecimalText(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture) ?? "null";
}
