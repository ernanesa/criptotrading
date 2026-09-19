using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Shadow;

/// <summary>
/// The two local files that make up one post-release evidence bundle. The
/// manifest is a pointer and integrity record; it is never a promotion gate.
/// </summary>
public sealed record PostReleaseEvidenceBundle(
    PostReleaseReportArtifact Report,
    string ReportPath,
    string ManifestPath,
    string ManifestJson,
    string ManifestSha256);

/// <summary>
/// Persists a redacted post-release report and its traceability manifest as an
/// immutable local bundle. No exchange client, credential or promotion path is
/// referenced by this writer.
/// </summary>
public static class PostReleaseEvidenceWriter
{
    public const string ManifestSchemaVersion = "post-release-evidence-manifest/v1";

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
    };

    public static PostReleaseEvidenceBundle Write(
        string directory,
        PostReleaseReportInput input,
        IReadOnlyCollection<string> traceabilityReferences,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(traceabilityReferences);
        EnsureUtc(generatedAtUtc, nameof(generatedAtUtc));

        var report = PostReleaseReportGenerator.Generate(input);
        ValidateDirectory(directory);
        ValidateReportId(input.ReportId);
        var references = ValidateReferences(traceabilityReferences);

        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, input.ReportId + ".json");
        var manifestPath = Path.Combine(directory, input.ReportId + ".manifest.json");
        var manifestJson = JsonSerializer.Serialize(new
        {
            schema_version = ManifestSchemaVersion,
            report_id = input.ReportId,
            artifact_file = Path.GetFileName(reportPath),
            artifact_schema_version = report.SchemaVersion,
            artifact_sha256 = report.Sha256,
            decision = input.Decision,
            generated_at_utc = generatedAtUtc,
            traceability_references = references,
            promotion_authorized = false,
        }, JsonOptions);

        WriteImmutable(reportPath, report.CanonicalJson);
        WriteImmutable(manifestPath, manifestJson);

        return new(
            report,
            reportPath,
            manifestPath,
            manifestJson,
            Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(manifestJson))));
    }

    private static string[] ValidateReferences(IReadOnlyCollection<string> references)
    {
        if (references.Count == 0)
        {
            throw new ArgumentException("At least one traceability reference is required.", nameof(references));
        }

        var normalized = references
            .Select(reference => reference.Trim())
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var reference in normalized)
        {
            if (reference.Length == 0 || reference.Length > 256 || reference.Any(char.IsControl))
            {
                throw new ArgumentException("Traceability references must be bounded text without control characters.", nameof(references));
            }

            if (LooksSensitive(reference))
            {
                throw new ArgumentException("Traceability references cannot contain credential-like metadata.", nameof(references));
            }
        }

        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Traceability references must be unique.", nameof(references));
        }

        return normalized;
    }

    private static void ValidateReportId(string reportId)
    {
        if (reportId.Length is < 1 or > 128 ||
            reportId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("ReportId must be a bounded opaque filename-safe identifier.", nameof(reportId));
        }
    }

    private static void ValidateDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new ArgumentException("The evidence directory cannot be a filesystem root.", nameof(directory));
        }
    }

    private static void WriteImmutable(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new IOException($"Evidence file already exists with different content: {path}");
            }

            RestrictFilePermissions(path);
            return;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RestrictFilePermissions(temporaryPath);
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException($"Evidence file was concurrently created with different content: {path}");
                }

                RestrictFilePermissions(path);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
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
            throw new ArgumentException("Evidence timestamps must be UTC.", parameterName);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
