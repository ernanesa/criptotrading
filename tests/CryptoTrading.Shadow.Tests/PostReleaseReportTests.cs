using CryptoTrading.Shadow;
using Xunit;

namespace CryptoTrading.Shadow.Tests;

public sealed class PostReleaseReportTests
{
    [Fact]
    [Trait("Category", "ReleaseEvidence")]
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

        Assert.Equal(PostReleaseReportGenerator.SchemaVersion, firstArtifact.SchemaVersion);
        Assert.Equal(firstArtifact.CanonicalJson, secondArtifact.CanonicalJson);
        Assert.Equal(firstArtifact.Sha256, secondArtifact.Sha256);
        Assert.Contains("source_versions", firstArtifact.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", firstArtifact.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", firstArtifact.CanonicalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseEvidence")]
    public void RejectsIncompleteOrUnsafeEvidenceFailClosed()
    {
        var valid = CreateInput();

        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with { Approver = " " }));
        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            WindowStartUtc = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(-3)),
        }));
        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            SourceVersions = new Dictionary<string, string>(),
        }));
        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Decision = "Promote",
        }));
        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            SourceVersions = new Dictionary<string, string> { ["api_key"] = "must-not-appear" },
        }));
    }

    [Fact]
    [Trait("Category", "ReleaseEvidence")]
    public void RejectsMetricContradictionsAndNegativeCounters()
    {
        var valid = CreateInput();

        Assert.Throws<ArgumentException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Metrics = valid.Metrics with { OrderAcceptedCount = 11, OrderIntentCount = 10 },
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostReleaseReportGenerator.Generate(valid with
        {
            Metrics = valid.Metrics with { ReliabilityErrorCount = -1 },
        }));
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
