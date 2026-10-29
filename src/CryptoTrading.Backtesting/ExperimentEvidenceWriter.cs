using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Research;

namespace CryptoTrading.Backtesting;

public sealed record ExperimentEvidenceInput
{
    public required string ExperimentId { get; init; }
    public required string PreregistrationSha256 { get; init; }
    public required int TrialCount { get; init; }
    public required int TrialBudget { get; init; }
    public required int HoldoutAccessCount { get; init; }
    public required bool HoldoutContaminated { get; init; }
    public required string Decision { get; init; }
    public required IReadOnlyDictionary<string, decimal> BenchmarkNetReturns { get; init; }
    public required string BenchmarkComparisonFingerprint { get; init; }
    public BacktestExecutionTiming? ExecutionTiming { get; init; }
    public string? EvaluationAssumptionsFingerprint { get; init; }
    public string? ObservedSeriesFingerprint { get; init; }
    public string? ObservedSeriesFingerprintVersion { get; init; }

    /// <summary>
    /// Realized result of a deterministic paper/shadow simulation. It is not
    /// reconciled account PnL and never authorizes promotion.
    /// </summary>
    public PaperRunEvidence? PaperRun { get; init; }
    public string? DeclaredDatasetContentHash { get; init; }
    public required IReadOnlyDictionary<string, string> SourceVersions { get; init; }
    public required IReadOnlyCollection<string> TraceabilityReferences { get; init; }
}

public sealed record ExperimentEvidenceArtifact(
    string SchemaVersion,
    string ExperimentId,
    string Decision,
    string CanonicalJson,
    string Sha256);

public sealed record ExperimentEvidenceBundle(
    ExperimentEvidenceArtifact Artifact,
    string ArtifactPath,
    string ManifestPath,
    string ManifestJson,
    string ManifestSha256);

/// <summary>Writes redacted, immutable experiment evidence without promotion side effects.</summary>
/// <summary>Realized result produced by a deterministic paper/shadow run.</summary>
public sealed record PaperRunEvidence(
    decimal RealizedPnlQuote,
    decimal FeesPaidQuote,
    decimal OpenQuantity,
    decimal OpenCostQuote,
    int ClosedFillCount);

public static class ExperimentEvidenceWriter
{
    public const string SchemaVersion = "experiment-evidence/v1";
    public const string ProvenanceSchemaVersion = "experiment-evidence/v2";
    public const string DatasetProvenanceSchemaVersion = "experiment-evidence/v3";
    public const string WindowProvenanceSchemaVersion = "experiment-evidence/v4";
    public const string PaperRunSchemaVersion = "experiment-evidence/v5";
    public const string ManifestSchemaVersion = "experiment-evidence-manifest/v1";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>
    /// Builds evidence from a deterministic paper/shadow replay. Benchmark returns
    /// are taken from the same evaluation so the bundle stays internally
    /// consistent; paper results are recorded as simulated, never reconciled.
    /// </summary>
    public static ExperimentEvidenceInput FromPaperShadow(
        string experimentId,
        string preregistrationSha256,
        int trialCount,
        int trialBudget,
        int holdoutAccessCount,
        bool holdoutContaminated,
        string decision,
        WalkForwardEvaluationResult evaluation,
        PaperRunEvidence paperRun,
        IReadOnlyDictionary<string, string> sourceVersions,
        IReadOnlyCollection<string> traceabilityReferences)
    {
        ArgumentNullException.ThrowIfNull(paperRun);
        return FromWalkForward(
            experimentId, preregistrationSha256, trialCount, trialBudget, holdoutAccessCount,
            holdoutContaminated, decision, evaluation, sourceVersions, traceabilityReferences) with
        {
            PaperRun = paperRun
        };
    }

    public static ExperimentEvidenceInput FromWalkForward(
        string experimentId,
        string preregistrationSha256,
        int trialCount,
        int trialBudget,
        int holdoutAccessCount,
        bool holdoutContaminated,
        string decision,
        WalkForwardEvaluationResult evaluation,
        IReadOnlyDictionary<string, string> sourceVersions,
        IReadOnlyCollection<string> traceabilityReferences)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        decision = Required(decision, nameof(decision));
        if (decision is "PaperEligible" or "ShadowEligible" or "CanaryEligible")
        {
            throw new InvalidOperationException("Eligible evidence is disabled until independent dataset verification and statistical gates are implemented.");
        }

        var summary = evaluation.GetBenchmarkSummary();
        return new ExperimentEvidenceInput
        {
            ExperimentId = experimentId,
            PreregistrationSha256 = preregistrationSha256,
            TrialCount = trialCount,
            TrialBudget = trialBudget,
            HoldoutAccessCount = holdoutAccessCount,
            HoldoutContaminated = holdoutContaminated,
            Decision = decision,
            BenchmarkNetReturns = new Dictionary<string, decimal>
            {
                ["NO_TRADE/cash"] = summary.NoTradeCashNetReturn,
                ["buy-and-hold"] = summary.BuyAndHoldNetReturn,
                ["strategy/walk-forward"] = summary.StrategyNetReturn,
            },
            BenchmarkComparisonFingerprint = summary.Fingerprint,
            ExecutionTiming = evaluation.ExecutionTiming,
            EvaluationAssumptionsFingerprint = evaluation.EvaluationAssumptionsFingerprint,
            ObservedSeriesFingerprint = evaluation.ObservedSeriesFingerprint,
            ObservedSeriesFingerprintVersion = evaluation.ObservedSeriesFingerprint is null ? null : evaluation.ObservedSeriesFingerprintVersion,
            DeclaredDatasetContentHash = evaluation.ObservedSeriesFingerprint is null ? null : evaluation.Dataset.ContentHash,
            SourceVersions = sourceVersions,
            TraceabilityReferences = traceabilityReferences,
        };
    }

    public static ExperimentEvidenceInput FromExperimentRecord(
        ExperimentRecord record,
        WalkForwardEvaluationResult evaluation,
        string decision,
        IReadOnlyDictionary<string, string> sourceVersions,
        IReadOnlyCollection<string> traceabilityReferences)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(evaluation);
        var preregistration = record.Preregistration;
        return FromGovernanceState(
            preregistration,
            record.TrialCount,
            record.HoldoutAccessCount,
            record.HoldoutStatus == HoldoutStatus.Contaminated,
            evaluation,
            decision,
            sourceVersions,
            traceabilityReferences);
    }

    public static ExperimentEvidenceInput FromGovernanceState(
        ExperimentPreregistration preregistration,
        int trialCount,
        int holdoutAccessCount,
        bool holdoutContaminated,
        WalkForwardEvaluationResult evaluation,
        string decision,
        IReadOnlyDictionary<string, string> sourceVersions,
        IReadOnlyCollection<string> traceabilityReferences)
    {
        ArgumentNullException.ThrowIfNull(preregistration);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (trialCount < 1 || trialCount > preregistration.TrialBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(trialCount), "Persisted trial count must be within the preregistered budget.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(holdoutAccessCount);

        if (!string.Equals(evaluation.Dataset.Version, preregistration.DatasetVersion, StringComparison.Ordinal) ||
            !string.Equals(evaluation.Dataset.ContentHash, preregistration.DatasetHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evaluation.StrategyVersion, preregistration.StrategyVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Walk-forward lineage does not match the experiment preregistration.");
        }

        return FromWalkForward(
            preregistration.ExperimentId.Value.ToString("D"),
            preregistration.PreregistrationSha256,
            trialCount,
            preregistration.TrialBudget,
            holdoutAccessCount,
            holdoutContaminated,
            decision,
            evaluation,
            sourceVersions,
            traceabilityReferences);
    }

    public static ExperimentEvidenceBundle Write(
        string directory,
        ExperimentEvidenceInput input,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(input);
        EnsureUtc(generatedAtUtc);
        ValidateId(input.ExperimentId);
        ValidateSha(input.PreregistrationSha256, nameof(input.PreregistrationSha256));
        ValidateSha(input.BenchmarkComparisonFingerprint, nameof(input.BenchmarkComparisonFingerprint));
        if (input.TrialBudget < 1 || input.TrialBudget > 1_000_000 || input.TrialCount < 0 || input.TrialCount > input.TrialBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Trial count must be within the preregistered budget.");
        }

        if (input.HoldoutAccessCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Holdout access count cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(input.BenchmarkNetReturns);
        if (!input.BenchmarkNetReturns.ContainsKey("NO_TRADE/cash") ||
            !input.BenchmarkNetReturns.ContainsKey("buy-and-hold") ||
            input.BenchmarkNetReturns.Keys.Any(static key => key is not ("NO_TRADE/cash" or "buy-and-hold" or "strategy/walk-forward")))
        {
            throw new ArgumentException("Evidence must use the supported benchmark labels and include cash and buy-and-hold.", nameof(input));
        }

        ValidateSourceVersions(input.SourceVersions);

        var decision = Required(input.Decision, nameof(input.Decision));
        if (!new[] { "Rejected", "Iterate", "PaperEligible", "ShadowEligible", "CanaryEligible" }
            .Contains(decision, StringComparer.Ordinal))
        {
            throw new ArgumentException("Decision is not a supported experiment decision.", nameof(input));
        }

        if (input.HoldoutContaminated && decision is not ("Rejected" or "Iterate"))
        {
            throw new InvalidOperationException("A contaminated holdout cannot produce an eligible decision.");
        }

        if (decision is "PaperEligible" or "ShadowEligible" or "CanaryEligible")
        {
            throw new InvalidOperationException("Eligible evidence is disabled until independent dataset verification and statistical gates are implemented.");
        }

        if ((input.ExecutionTiming is not null || input.EvaluationAssumptionsFingerprint is not null ||
             input.ObservedSeriesFingerprint is not null || input.DeclaredDatasetContentHash is not null) &&
            !input.BenchmarkNetReturns.ContainsKey("strategy/walk-forward"))
        {
            throw new ArgumentException("Versioned walk-forward evidence must include the strategy return.", nameof(input));
        }

        if (input.EvaluationAssumptionsFingerprint is not null)
        {
            ValidateSha(input.EvaluationAssumptionsFingerprint, nameof(input.EvaluationAssumptionsFingerprint));
        }

        if ((input.ObservedSeriesFingerprint is null) != (input.DeclaredDatasetContentHash is null))
        {
            throw new InvalidOperationException("Observed series and declared dataset hashes must be provided together.");
        }

        if (input.ObservedSeriesFingerprint is not null)
        {
            ValidateSha(input.ObservedSeriesFingerprint, nameof(input.ObservedSeriesFingerprint));
            ValidateSha(input.DeclaredDatasetContentHash!, nameof(input.DeclaredDatasetContentHash));
        }

        if (input.ObservedSeriesFingerprintVersion is not null &&
            (input.ObservedSeriesFingerprint is null || input.ObservedSeriesFingerprintVersion != WalkForwardObservedIdentity.Version))
        {
            throw new InvalidOperationException("Observed-series fingerprint version is invalid or missing its hash.");
        }

        if (input.ExecutionTiming is { } timing && timing is not (BacktestExecutionTiming.NextBarClose or BacktestExecutionTiming.LegacySignalBarClose))
        {
            throw new InvalidOperationException("Execution timing is unknown.");
        }

        var references = ValidateReferences(input.TraceabilityReferences);
        var hasProvenance = input.ExecutionTiming is not null || input.EvaluationAssumptionsFingerprint is not null;
        var hasDataProvenance = input.ObservedSeriesFingerprint is not null;
        var artifactSchema = input.PaperRun is not null ? PaperRunSchemaVersion :
            input.ObservedSeriesFingerprintVersion is not null ? WindowProvenanceSchemaVersion :
            hasDataProvenance ? DatasetProvenanceSchemaVersion : hasProvenance ? ProvenanceSchemaVersion : SchemaVersion;
        var canonicalJson = SerializeEvidence(input, artifactSchema, decision, hasProvenance, hasDataProvenance);
        var artifact = new ExperimentEvidenceArtifact(
            artifactSchema, input.ExperimentId, decision, canonicalJson,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(canonicalJson))));

        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(directory, input.ExperimentId + ".experiment.json");
        var manifestPath = Path.Combine(directory, input.ExperimentId + ".experiment.manifest.json");
        var manifestJson = SerializeManifest(input.ExperimentId, Path.GetFileName(artifactPath), artifact, decision, generatedAtUtc, references);
        WriteImmutable(artifactPath, canonicalJson);
        WriteImmutable(manifestPath, manifestJson);
        return new(artifact, artifactPath, manifestPath, manifestJson,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(manifestJson))));
    }

    private static string SerializeEvidence(ExperimentEvidenceInput input, string schema, string decision, bool hasProvenance, bool hasDataProvenance)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", schema);
            writer.WriteString("experiment_id", input.ExperimentId);
            writer.WriteString("preregistration_sha256", input.PreregistrationSha256.ToUpperInvariant());
            writer.WriteNumber("trial_count", input.TrialCount);
            writer.WriteNumber("trial_budget", input.TrialBudget);
            writer.WriteNumber("holdout_access_count", input.HoldoutAccessCount);
            writer.WriteBoolean("holdout_contaminated", input.HoldoutContaminated);
            writer.WriteString("decision", decision);
            writer.WriteStartObject("benchmark_net_returns");
            foreach (var pair in input.BenchmarkNetReturns.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteString("benchmark_comparison_fingerprint", input.BenchmarkComparisonFingerprint.ToUpperInvariant());
            if (hasProvenance || hasDataProvenance)
            {
                writer.WriteString("execution_timing", input.ExecutionTiming?.ToString());
                writer.WriteString("evaluation_assumptions_fingerprint", input.EvaluationAssumptionsFingerprint?.ToUpperInvariant());
            }
            if (hasDataProvenance)
            {
                writer.WriteString("observed_series_fingerprint", input.ObservedSeriesFingerprint!.ToUpperInvariant());
                writer.WriteString("declared_dataset_content_hash", input.DeclaredDatasetContentHash!.ToUpperInvariant());
                if (input.ObservedSeriesFingerprintVersion is not null)
                {
                    writer.WriteString("observed_series_fingerprint_version", input.ObservedSeriesFingerprintVersion);
                }
            }
            if (input.PaperRun is { } paperRun)
            {
                writer.WriteNumber("paper_realized_pnl_quote", paperRun.RealizedPnlQuote);
                writer.WriteNumber("paper_fees_paid_quote", paperRun.FeesPaidQuote);
                writer.WriteNumber("paper_open_quantity", paperRun.OpenQuantity);
                writer.WriteNumber("paper_open_cost_quote", paperRun.OpenCostQuote);
                writer.WriteNumber("paper_closed_fill_count", paperRun.ClosedFillCount);
                writer.WriteString("paper_status", "SIMULATED_NOT_RECONCILED");
            }

            writer.WriteStartObject("source_versions");
            foreach (var pair in input.SourceVersions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Utf8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeManifest(string experimentId, string artifactFile, ExperimentEvidenceArtifact artifact,
        string decision, DateTimeOffset generatedAtUtc, IReadOnlyList<string> references)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", ManifestSchemaVersion);
            writer.WriteString("experiment_id", experimentId);
            writer.WriteString("artifact_file", artifactFile);
            writer.WriteString("artifact_schema_version", artifact.SchemaVersion);
            writer.WriteString("artifact_sha256", artifact.Sha256);
            writer.WriteString("decision", decision);
            writer.WriteString("generated_at_utc", generatedAtUtc);
            writer.WriteStartArray("traceability_references");
            foreach (var reference in references) writer.WriteStringValue(reference);
            writer.WriteEndArray();
            writer.WriteBoolean("promotion_authorized", false);
            writer.WriteEndObject();
        }

        return Utf8.GetString(buffer.WrittenSpan);
    }

    private static string[] ValidateReferences(IReadOnlyCollection<string> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var normalized = references.Select(value => Required(value)).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Any(value => value.Length > 256 || value.Any(char.IsControl) || LooksSensitive(value)))
        {
            throw new ArgumentException("Traceability references must be unique, bounded and redacted.", nameof(references));
        }

        return normalized;
    }

    private static void ValidateSourceVersions(IReadOnlyDictionary<string, string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count > 16)
        {
            throw new ArgumentException("Too many source-version entries.", nameof(versions));
        }

        foreach (var pair in versions)
        {
            if (pair.Key is not ("dataset" or "dataset_version" or "code_commit" or "dependency_lock" or "strategy" or "git" or "commit" or "model" or "sdk" or "runtime" or "engine" or "exchange" or "algorithm" or "fees") ||
                string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 128 || LooksSensitive(pair.Value) ||
                pair.Value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or '/' or '+')) ||
                (pair.Key == "code_commit" && !(pair.Value.Length is 40 or 64 && pair.Value.All(Uri.IsHexDigit))) ||
                (pair.Key == "dependency_lock" && !(pair.Value.Length == 64 && pair.Value.All(Uri.IsHexDigit))) ||
                !(pair.Value.Contains('.', StringComparison.Ordinal) || pair.Value.Contains("/v", StringComparison.OrdinalIgnoreCase) ||
                  (pair.Value.Length is 40 or 64 && pair.Value.All(Uri.IsHexDigit)) ||
                  (pair.Value.Length > 1 && (pair.Value[0] is 'v' or 'V') && pair.Value[1..].All(char.IsAsciiDigit))))
            {
                throw new ArgumentException("Source versions must use supported keys and bounded version or fingerprint metadata.", nameof(versions));
            }
        }
    }

    private static void WriteImmutable(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("Evidence destination must not be a symbolic link.");
        }

        if (File.Exists(path))
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                var mode = File.GetUnixFileMode(path);
                var disallowed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & UnixFileMode.UserRead) == 0 || (mode & disallowed) != 0)
                {
                    throw new UnauthorizedAccessException("Existing evidence permissions are not suitable for safe replay.");
                }
            }

            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                throw new IOException($"Evidence file already exists with different content: {path}");
            }

            return;
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporary, fileOptions))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string Required(string? value, string? name = null) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static void ValidateId(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("ExperimentId must be filename-safe.", nameof(value));
        }
    }

    private static void ValidateSha(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Value must be a SHA-256 digest.", name);
        }
    }

    private static bool LooksSensitive(string value) =>
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("private_key", StringComparison.OrdinalIgnoreCase);

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evidence timestamp must be UTC.", nameof(value));
        }
    }

}
