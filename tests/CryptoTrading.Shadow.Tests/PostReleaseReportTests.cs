// Test fixtures are public for xUnit discovery and are instantiated by the test adapter.
#pragma warning disable CA1515
#pragma warning disable CA1812
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Shadow.Tests;

public sealed class PostReleaseReportTests
{
    [Xunit.Fact]
    [Xunit.Trait("Category", "ReleaseEvidence")]
    public void GeneratesDeterministicRedactedArtifactWithSortedSourcesAndFollowUps()
    {
        var first = CreateInput() with
        {
            SourceVersions = new Dictionary<string, string>
            {
                ["strategy"] = "trend/v1",
                ["risk"] = "risk/v2",
            },
            FollowUps = ["inspect-alerts", "review-reconciliation"],
        };
        var second = first with
        {
            SourceVersions = new Dictionary<string, string>
            {
                ["risk"] = "risk/v2",
                ["strategy"] = "trend/v1",
            },
            FollowUps = ["review-reconciliation", "inspect-alerts"],
        };

        var firstArtifact = PostReleaseReportGenerator.Generate(first);
        var secondArtifact = PostReleaseReportGenerator.Generate(second);

        Xunit.Assert.Equal(PostReleaseReportGenerator.SchemaVersion, firstArtifact.SchemaVersion);
        Xunit.Assert.Equal(firstArtifact.CanonicalJson, secondArtifact.CanonicalJson);
        Xunit.Assert.Equal(firstArtifact.Sha256, secondArtifact.Sha256);
        Xunit.Assert.Contains("source_versions", firstArtifact.CanonicalJson, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("apiKey", firstArtifact.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("secret", firstArtifact.CanonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    [Xunit.Trait("Category", "ReleaseEvidence")]
    public void RejectsIncompleteOrUnsafeEvidenceFailClosed()
    {
        var valid = CreateInput();

        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with { Approver = " " }));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            WindowStartUtc = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(-3)),
        }));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            SourceVersions = new Dictionary<string, string>(),
        }));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Decision = "Promote",
        }));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            SourceVersions = new Dictionary<string, string> { ["api_key"] = "must-not-appear" },
        }));
    }

    [Xunit.Fact]
    [Xunit.Trait("Category", "ReleaseEvidence")]
    public void RejectsMetricContradictionsAndNegativeCounters()
    {
        var valid = CreateInput();

        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Metrics = valid.Metrics with { OrderAcceptedCount = 11, OrderIntentCount = 10 },
        }));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Metrics = valid.Metrics with { ReliabilityErrorCount = -1 },
        }));
    }

    [Xunit.Fact]
    [Xunit.Trait("Category", "ReleaseEvidence")]
    public void WritesImmutableRedactedReportAndTraceabilityManifest()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var generatedAt = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        var bundle = PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input,
            ["AC-RELEASE-REPORT-01", "docs/readiness/traceability-20260919.yaml", "33eaf73"],
            generatedAt);

        Xunit.Assert.True(File.Exists(bundle.ReportPath));
        Xunit.Assert.True(File.Exists(bundle.ManifestPath));
        Xunit.Assert.Equal(bundle.Report.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(bundle.ReportPath)))));
        Xunit.Assert.Equal(bundle.ManifestSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(bundle.ManifestPath)))));

        using var manifest = JsonDocument.Parse(File.ReadAllText(bundle.ManifestPath));
        Xunit.Assert.Equal(PostReleaseEvidenceWriter.ManifestSchemaVersion, manifest.RootElement.GetProperty("schema_version").GetString());
        Xunit.Assert.False(manifest.RootElement.GetProperty("promotion_authorized").GetBoolean());
        Xunit.Assert.Equal(bundle.Report.Sha256, manifest.RootElement.GetProperty("artifact_sha256").GetString());
        Xunit.Assert.Equal("33eaf73", manifest.RootElement.GetProperty("traceability_references")[0].GetString());
        AssertUserOnlyFileMode(bundle.ReportPath);
        AssertUserOnlyFileMode(bundle.ManifestPath);

        var repeated = PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input,
            ["33eaf73", "AC-RELEASE-REPORT-01", "docs/readiness/traceability-20260919.yaml"],
            generatedAt);
        Xunit.Assert.Equal(bundle.Report.Sha256, repeated.Report.Sha256);
        Xunit.Assert.Equal(bundle.ManifestSha256, repeated.ManifestSha256);
    }

    [Xunit.Fact]
    [Xunit.Trait("Category", "ReleaseEvidence")]
    public void RejectsUnsafeOrConflictingEvidenceBundle()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var generatedAt = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input with { ReportId = "../unsafe" },
            ["AC-RELEASE-REPORT-01"],
            generatedAt));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input,
            ["api_key must never be recorded"],
            generatedAt));
        Xunit.Assert.Throws<ArgumentException>(() => PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input,
            ["AC-RELEASE-REPORT-01"],
            generatedAt.ToOffset(TimeSpan.FromHours(-3))));

        PostReleaseEvidenceWriter.Write(temporary.Path, input, ["AC-RELEASE-REPORT-01"], generatedAt);
        Xunit.Assert.Throws<IOException>(() => PostReleaseEvidenceWriter.Write(
            temporary.Path,
            input,
            ["AC-RELEASE-REPORT-01"],
            generatedAt.AddMinutes(1)));
    }

    private static void AssertUserOnlyFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Xunit.Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cryptotrading-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static PostReleaseReportInput CreateInput() => new()
    {
        ReportId = "RELEASE-20260919-local",
        WindowStartUtc = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero),
        WindowEndUtc = new DateTimeOffset(2026, 9, 19, 11, 0, 0, TimeSpan.Zero),
        Environment = "local",
        ExecutionMode = "PAPER_SHADOW",
        Decision = "No-Go",
        Owner = "owner",
        Reviewer = "reviewer",
        Approver = "pending-human-approval",
        SourceVersions = new Dictionary<string, string> { ["strategy"] = "trend/v1" },
        Metrics = new PostReleaseMetrics
        {
            ObservationCount = 10,
            ReliabilityErrorCount = 1,
            OrderIntentCount = 10,
            OrderAcceptedCount = 4,
            OrderRejectedCount = 6,
            NoTradeCount = 6,
            DivergenceCount = 0,
        },
        FollowUps = ["complete-human-review"],
    };
}
