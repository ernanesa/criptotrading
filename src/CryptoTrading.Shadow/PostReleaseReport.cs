using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Shadow;

/// <summary>Redacted counters required by AC-RELEASE-REPORT-01.</summary>
public sealed record PostReleaseMetrics
{
    public int ObservationCount { get; init; }

    public int ReliabilityErrorCount { get; init; }

    public int RiskViolationCount { get; init; }

    public int SafeModeEntryCount { get; init; }

    public int KillSwitchActivationCount { get; init; }

    public int OrderIntentCount { get; init; }

    public int OrderAcceptedCount { get; init; }

    public int OrderRejectedCount { get; init; }

    public int UnknownOrderCount { get; init; }

    public int FillCount { get; init; }

    public int StrategySignalCount { get; init; }

    public int NoTradeCount { get; init; }

    public int IncidentCount { get; init; }

    public int InterventionCount { get; init; }

    public int DivergenceCount { get; init; }

    internal void Validate()
    {
        foreach (var (name, value) in EnumerateCounts())
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(name, "Post-release counters cannot be negative.");
            }
        }

        if (OrderAcceptedCount > OrderIntentCount)
        {
            throw new ArgumentException("Accepted orders cannot exceed order intents.", nameof(OrderAcceptedCount));
        }

        if (OrderRejectedCount > OrderIntentCount)
        {
            throw new ArgumentException("Rejected orders cannot exceed order intents.", nameof(OrderRejectedCount));
        }

        if (NoTradeCount > ObservationCount || DivergenceCount > ObservationCount)
        {
            throw new ArgumentException("No-trade and divergence counts cannot exceed observations.", nameof(ObservationCount));
        }
    }

    private IEnumerable<(string Name, int Value)> EnumerateCounts()
    {
        yield return (nameof(ObservationCount), ObservationCount);
        yield return (nameof(ReliabilityErrorCount), ReliabilityErrorCount);
        yield return (nameof(RiskViolationCount), RiskViolationCount);
        yield return (nameof(SafeModeEntryCount), SafeModeEntryCount);
        yield return (nameof(KillSwitchActivationCount), KillSwitchActivationCount);
        yield return (nameof(OrderIntentCount), OrderIntentCount);
        yield return (nameof(OrderAcceptedCount), OrderAcceptedCount);
        yield return (nameof(OrderRejectedCount), OrderRejectedCount);
        yield return (nameof(UnknownOrderCount), UnknownOrderCount);
        yield return (nameof(FillCount), FillCount);
        yield return (nameof(StrategySignalCount), StrategySignalCount);
        yield return (nameof(NoTradeCount), NoTradeCount);
        yield return (nameof(IncidentCount), IncidentCount);
        yield return (nameof(InterventionCount), InterventionCount);
        yield return (nameof(DivergenceCount), DivergenceCount);
    }
}

/// <summary>
/// Immutable input for a post-release evidence report. It contains counters
/// only; exchange payloads, credentials and order identifiers are excluded by
/// construction.
/// </summary>
public sealed record PostReleaseReportInput
{
    public required string ReportId { get; init; }

    public required DateTimeOffset WindowStartUtc { get; init; }

    public required DateTimeOffset WindowEndUtc { get; init; }

    public required string Environment { get; init; }

    public required string ExecutionMode { get; init; }

    public required string Decision { get; init; }

    public required string Owner { get; init; }

    public required string Reviewer { get; init; }

    public required string Approver { get; init; }

    public required IReadOnlyDictionary<string, string> SourceVersions { get; init; }

    public required PostReleaseMetrics Metrics { get; init; }

    public required IReadOnlyCollection<string> FollowUps { get; init; }
}

/// <summary>Deterministic, redacted post-release evidence artifact.</summary>
public sealed record PostReleaseReportArtifact(
    string SchemaVersion,
    string CanonicalJson,
    string Sha256);

/// <summary>
/// Validates and emits post-release evidence. This generator never evaluates
/// or grants promotion authority; consumers must retain the recorded decision
/// and perform their own human-approval gate.
/// </summary>
public static class PostReleaseReportGenerator
{
    public const string SchemaVersion = "post-release-report/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
    };

    public static PostReleaseReportArtifact Generate(PostReleaseReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var canonical = JsonSerializer.Serialize(new
        {
            schema_version = SchemaVersion,
            report_id = input.ReportId,
            window_start_utc = input.WindowStartUtc,
            window_end_utc = input.WindowEndUtc,
            environment = input.Environment,
            execution_mode = input.ExecutionMode,
            decision = input.Decision,
            owner = input.Owner,
            reviewer = input.Reviewer,
            approver = input.Approver,
            source_versions = input.SourceVersions
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            metrics = input.Metrics,
            follow_ups = input.FollowUps.Order(StringComparer.Ordinal).ToArray(),
        }, JsonOptions);

        return new(
            SchemaVersion,
            canonical,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static void Validate(PostReleaseReportInput input)
    {
        ValidateText(input.ReportId, nameof(input.ReportId));
        ValidateText(input.Environment, nameof(input.Environment));
        ValidateText(input.ExecutionMode, nameof(input.ExecutionMode));
        ValidateText(input.Decision, nameof(input.Decision));
        ValidateText(input.Owner, nameof(input.Owner));
        ValidateText(input.Reviewer, nameof(input.Reviewer));
        ValidateText(input.Approver, nameof(input.Approver));
        EnsureUtc(input.WindowStartUtc, nameof(input.WindowStartUtc));
        EnsureUtc(input.WindowEndUtc, nameof(input.WindowEndUtc));
        if (input.WindowEndUtc <= input.WindowStartUtc)
        {
            throw new ArgumentException("The release window must have positive duration.");
        }

        if (!AllowedDecisions.Contains(input.Decision, StringComparer.Ordinal))
        {
            throw new ArgumentException("Decision must be Go, No-Go, Rolled Back or Hold.");
        }

        ArgumentNullException.ThrowIfNull(input.SourceVersions);
        if (input.SourceVersions.Count == 0)
        {
            throw new ArgumentException("At least one source/version is required.");
        }

        foreach (var pair in input.SourceVersions)
        {
            ValidateText(pair.Key, nameof(input.SourceVersions));
            ValidateText(pair.Value, nameof(input.SourceVersions));
            if (LooksSensitive(pair.Key) || LooksSensitive(pair.Value))
            {
                throw new ArgumentException("Source versions cannot contain credential-like metadata.");
            }
        }

        ArgumentNullException.ThrowIfNull(input.Metrics);
        input.Metrics.Validate();
        ArgumentNullException.ThrowIfNull(input.FollowUps);
        foreach (var followUp in input.FollowUps)
        {
            ValidateText(followUp, nameof(input.FollowUps));
        }
    }

    private static readonly string[] AllowedDecisions = ["Go", "No-Go", "Rolled Back", "Hold"];

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl) || value.Length > 256)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Evidence text is empty, too long or contains control characters.");
        }
    }

    private static bool LooksSensitive(string value) =>
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("private_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("privatekey", StringComparison.OrdinalIgnoreCase);

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Release timestamps must be UTC.", parameterName);
        }
    }
}
