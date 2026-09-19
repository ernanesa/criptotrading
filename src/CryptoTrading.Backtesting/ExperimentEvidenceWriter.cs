using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
public static class ExperimentEvidenceWriter
{
    public const string SchemaVersion = "experiment-evidence/v1";
    public const string ManifestSchemaVersion = "experiment-evidence-manifest/v1";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General) { WriteIndented = false };

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
        if (input.TrialBudget < 1 || input.TrialBudget > 1_000_000 || input.TrialCount < 0 || input.TrialCount > input.TrialBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Trial count must be within the preregistered budget.");
        }

        if (input.HoldoutAccessCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Holdout access count cannot be negative.");
        }

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

        var references = ValidateReferences(input.TraceabilityReferences);
        var canonicalJson = JsonSerializer.Serialize(new
        {
            schema_version = SchemaVersion,
            experiment_id = input.ExperimentId,
            preregistration_sha256 = input.PreregistrationSha256.ToUpperInvariant(),
            trial_count = input.TrialCount,
            trial_budget = input.TrialBudget,
            holdout_access_count = input.HoldoutAccessCount,
            holdout_contaminated = input.HoldoutContaminated,
            decision,
            source_versions = input.SourceVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        }, JsonOptions);
        var artifact = new ExperimentEvidenceArtifact(
            SchemaVersion, input.ExperimentId, decision, canonicalJson,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(canonicalJson))));

        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(directory, input.ExperimentId + ".experiment.json");
        var manifestPath = Path.Combine(directory, input.ExperimentId + ".experiment.manifest.json");
        var manifestJson = JsonSerializer.Serialize(new
        {
            schema_version = ManifestSchemaVersion,
            experiment_id = input.ExperimentId,
            artifact_file = Path.GetFileName(artifactPath),
            artifact_schema_version = artifact.SchemaVersion,
            artifact_sha256 = artifact.Sha256,
            decision,
            generated_at_utc = generatedAtUtc,
            traceability_references = references,
            promotion_authorized = false,
        }, JsonOptions);
        WriteImmutable(artifactPath, canonicalJson);
        WriteImmutable(manifestPath, manifestJson);
        return new(artifact, artifactPath, manifestPath, manifestJson,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(manifestJson))));
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

    private static void WriteImmutable(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                throw new IOException($"Evidence file already exists with different content: {path}");
            }

            RestrictPermissions(path);
            return;
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RestrictPermissions(temporary);
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

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
