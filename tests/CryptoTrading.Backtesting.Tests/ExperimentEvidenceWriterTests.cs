using System.Text;
using System.Text.Json;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ExperimentEvidenceWriterTests
{
    private const string PreregistrationHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void WritesCanonicalImmutableRedactedBundle()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var generated = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, generated);
        Assert.True(File.Exists(bundle.ArtifactPath));
        Assert.True(File.Exists(bundle.ManifestPath));
        Assert.Equal(bundle.Artifact.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(bundle.ArtifactPath)))));
        Assert.Equal(bundle.ManifestSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(bundle.ManifestPath)))));

        using var manifest = JsonDocument.Parse(File.ReadAllText(bundle.ManifestPath));
        Assert.False(manifest.RootElement.GetProperty("promotion_authorized").GetBoolean());
        Assert.Equal(bundle.Artifact.Sha256, manifest.RootElement.GetProperty("artifact_sha256").GetString());
        Assert.Equal("AC-RESEARCH-01", manifest.RootElement.GetProperty("traceability_references")[0].GetString());
        AssertUserOnly(bundle.ArtifactPath);
        AssertUserOnly(bundle.ManifestPath);

        var replay = ExperimentEvidenceWriter.Write(temporary.Path, input, generated);
        Assert.Equal(bundle.Artifact.Sha256, replay.Artifact.Sha256);
        Assert.Equal(bundle.ManifestSha256, replay.ManifestSha256);
    }

    [Fact]
    public void RefusesPromotionFromContaminatedHoldout()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.Write(
            temporary.Path, CreateInput() with { HoldoutContaminated = true, Decision = "PaperEligible" }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RejectsInvalidBudgetSensitiveReferenceAndNonUtcTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentEvidenceWriter.Write(
            temporary.Path, CreateInput() with { TrialCount = 4, TrialBudget = 3 }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(
            temporary.Path, CreateInput() with { TraceabilityReferences = ["api_key"] }, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(
            temporary.Path, CreateInput(), DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3))));
    }

    [Fact]
    public void BuildsEvidenceInputFromWalkForwardBenchmarkSummary()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var evaluation = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            CryptoTrading.Backtesting.BacktestEvaluationOptions.DailyCrypto(),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20));

        var input = ExperimentEvidenceWriter.FromWalkForward(
            "experiment-oos-001",
            PreregistrationHash,
            1,
            1,
            0,
            false,
            "Rejected",
            evaluation,
            new Dictionary<string, string> { ["dataset"] = dataset.Metadata.ContentHash },
            ["AC-RESEARCH-OOS-01"]);

        Assert.Contains("strategy/walk-forward", input.BenchmarkNetReturns.Keys);
        Assert.Equal(64, input.BenchmarkComparisonFingerprint.Length);
    }

    private static ExperimentEvidenceInput CreateInput() => new()
    {
        ExperimentId = "experiment-001",
        PreregistrationSha256 = PreregistrationHash,
        TrialCount = 3,
        TrialBudget = 3,
        HoldoutAccessCount = 1,
        HoldoutContaminated = false,
        Decision = "Rejected",
        BenchmarkNetReturns = new Dictionary<string, decimal> { ["NO_TRADE/cash"] = 0m, ["buy-and-hold"] = 0.2m },
        BenchmarkComparisonFingerprint = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
        SourceVersions = new Dictionary<string, string> { ["strategy"] = "trend/v1", ["dataset"] = "synthetic/v1" },
        TraceabilityReferences = ["docs/experiment-governance.md", "AC-RESEARCH-01"],
    };

    private static void AssertUserOnly(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cryptotrading-experiment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
