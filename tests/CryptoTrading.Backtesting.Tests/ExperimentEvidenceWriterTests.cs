using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Research;
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
        var expectedCanonical = JsonSerializer.Serialize(new
        {
            schema_version = ExperimentEvidenceWriter.SchemaVersion,
            experiment_id = input.ExperimentId,
            preregistration_sha256 = input.PreregistrationSha256,
            trial_count = input.TrialCount,
            trial_budget = input.TrialBudget,
            holdout_access_count = input.HoldoutAccessCount,
            holdout_contaminated = input.HoldoutContaminated,
            decision = input.Decision,
            benchmark_net_returns = input.BenchmarkNetReturns.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            benchmark_comparison_fingerprint = input.BenchmarkComparisonFingerprint,
            source_versions = input.SourceVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
        Assert.Equal(expectedCanonical, bundle.Artifact.CanonicalJson);
        var expectedManifest = JsonSerializer.Serialize(new
        {
            schema_version = ExperimentEvidenceWriter.ManifestSchemaVersion,
            experiment_id = input.ExperimentId,
            artifact_file = Path.GetFileName(bundle.ArtifactPath),
            artifact_schema_version = bundle.Artifact.SchemaVersion,
            artifact_sha256 = bundle.Artifact.Sha256,
            decision = input.Decision,
            generated_at_utc = generated,
            traceability_references = input.TraceabilityReferences.Order(StringComparer.Ordinal).ToArray(),
            promotion_authorized = false
        });
        Assert.Equal(expectedManifest, bundle.ManifestJson);
        Assert.Equal(ExperimentEvidenceWriter.SchemaVersion, bundle.Artifact.SchemaVersion);
        using (var artifact = JsonDocument.Parse(bundle.Artifact.CanonicalJson))
        {
            Assert.False(artifact.RootElement.TryGetProperty("execution_timing", out _));
        }
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
    public void PaperRunResultIsRecordedAsSimulatedAndNotReconciled()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            PaperRun = new PaperRunEvidence(9.79m, 0.21m, 0m, 0m, 1)
        };

        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(bundle.Artifact.CanonicalJson);
        var root = json.RootElement;

        Assert.Equal(ExperimentEvidenceWriter.PaperRunSchemaVersion, bundle.Artifact.SchemaVersion);
        Assert.Equal(9.79m, root.GetProperty("paper_realized_pnl_quote").GetDecimal());
        Assert.Equal("SIMULATED_NOT_RECONCILED", root.GetProperty("paper_status").GetString());
        using var manifest = JsonDocument.Parse(bundle.ManifestJson);
        Assert.False(manifest.RootElement.GetProperty("promotion_authorized").GetBoolean());
    }

    [Fact]
    public void FromPaperShadowWritesBundleThatRecordsSimulatedResult()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var evaluation = WalkForwardEvaluator.Evaluate(dataset.Metadata, dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            new BacktestEvaluationOptions(10_000m, 365),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray());
        using var temporary = new TemporaryDirectory();

        var input = ExperimentEvidenceWriter.FromPaperShadow(
            "shadow-run", PreregistrationHash, 1, 1, 0, false, "Rejected", evaluation,
            new PaperRunEvidence(12.5m, 0.4m, 0.01m, 100m, 1),
            new Dictionary<string, string> { ["strategy"] = "trend/1.0.0" }, ["AC-RESEARCH-OOS-01"]);
        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(bundle.Artifact.CanonicalJson);

        Assert.Equal(ExperimentEvidenceWriter.PaperRunSchemaVersion, bundle.Artifact.SchemaVersion);
        Assert.Equal("Rejected", bundle.Artifact.Decision);
        Assert.Equal(12.5m, json.RootElement.GetProperty("paper_realized_pnl_quote").GetDecimal());
        Assert.Equal("SIMULATED_NOT_RECONCILED", json.RootElement.GetProperty("paper_status").GetString());
    }

    [Fact]
    public void PaperRunStillCannotProduceEligibleEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            Decision = "PaperEligible",
            PaperRun = new PaperRunEvidence(1m, 0m, 0m, 0m, 1)
        };

        Assert.Throws<InvalidOperationException>(() =>
            ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RejectsUnexpectedEvidenceMetadataBeforeCreatingFiles()
    {
        using var temporary = new TemporaryDirectory();
        var badSourceKey = CreateInput() with { SourceVersions = new Dictionary<string, string> { ["api_key"] = "synthetic-example" } };
        var badSourceValue = CreateInput() with { SourceVersions = new Dictionary<string, string> { ["strategy"] = "arbitrary-free-text" } };
        var badBenchmark = CreateInput() with
        {
            BenchmarkNetReturns = new Dictionary<string, decimal> { ["NO_TRADE/cash"] = 0m, ["buy-and-hold"] = 0m, ["unregistered-label"] = 0m }
        };

        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(temporary.Path, badSourceKey, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(temporary.Path, badSourceValue, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(temporary.Path, badBenchmark, DateTimeOffset.UtcNow));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path));
    }

    [Fact]
    public void ExistingEvidenceWithBroadPermissionsIsRejectedWithoutChangingItsMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, time);
        var broad = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(bundle.ArtifactPath, broad);

        Assert.Throws<UnauthorizedAccessException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, time));
        Assert.Equal(broad, File.GetUnixFileMode(bundle.ArtifactPath));
    }

    [Fact]
    public void ExistingEvidenceWithoutOwnerReadIsRejectedWithoutChangingItsMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, time);
        File.SetUnixFileMode(bundle.ArtifactPath, UnixFileMode.UserWrite);

        Assert.Throws<UnauthorizedAccessException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, time));
        Assert.Equal(UnixFileMode.UserWrite, File.GetUnixFileMode(bundle.ArtifactPath));
    }

    [Fact]
    public void ExistingEvidenceSymlinkIsRejectedWithoutChangingTheTarget()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        var input = CreateInput();
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var original = ExperimentEvidenceWriter.Write(source.Path, input, time);
        var before = File.ReadAllText(original.ArtifactPath);
        var beforeMode = File.GetUnixFileMode(original.ArtifactPath);
        File.CreateSymbolicLink(Path.Combine(target.Path, input.ExperimentId + ".experiment.json"), original.ArtifactPath);

        Assert.Throws<IOException>(() => ExperimentEvidenceWriter.Write(target.Path, input, time));
        Assert.Equal(before, File.ReadAllText(original.ArtifactPath));
        Assert.Equal(beforeMode, File.GetUnixFileMode(original.ArtifactPath));
    }

    [Fact]
    public void DanglingEvidenceSymlinkIsRejectedAndPreserved()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        using var temporary = new TemporaryDirectory();
        var input = CreateInput();
        var linkPath = Path.Combine(temporary.Path, input.ExperimentId + ".experiment.json");
        var missingTarget = Path.Combine(temporary.Path, "missing-target.json");
        File.CreateSymbolicLink(linkPath, missingTarget);

        Assert.Throws<IOException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow));
        Assert.Equal(missingTarget, new FileInfo(linkPath).LinkTarget);
        Assert.False(File.Exists(missingTarget));
    }

    [Fact]
    public void ManifestSymlinkIsRejectedWithoutChangingTarget()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD()) return;
        using var source = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        var input = CreateInput();
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var original = ExperimentEvidenceWriter.Write(source.Path, input, time);
        var before = File.ReadAllText(original.ManifestPath);
        var mode = File.GetUnixFileMode(original.ManifestPath);
        File.CreateSymbolicLink(Path.Combine(target.Path, input.ExperimentId + ".experiment.manifest.json"), original.ManifestPath);

        Assert.Throws<IOException>(() => ExperimentEvidenceWriter.Write(target.Path, input, time));
        Assert.Equal(before, File.ReadAllText(original.ManifestPath));
        Assert.Equal(mode, File.GetUnixFileMode(original.ManifestPath));
    }

    [Fact]
    public void AcceptsGovernedRunnerSourceVersionKeys()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            SourceVersions = new Dictionary<string, string>
            {
                ["dataset"] = new string('A', 64),
                ["dataset_version"] = "1.0.0",
                ["code_commit"] = new string('B', 40),
                ["dependency_lock"] = new string('C', 64)
            }
        };

        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(bundle.ArtifactPath));
    }

    [Fact]
    public void ProvenanceEvidenceRequiresStrategyReturnButHistoricalV1RemainsReadable()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            ExecutionTiming = BacktestExecutionTiming.NextBarClose,
            EvaluationAssumptionsFingerprint = new string('A', 64)
        };

        Assert.Throws<ArgumentException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow));
        Assert.True(File.Exists(ExperimentEvidenceWriter.Write(temporary.Path, CreateInput(), DateTimeOffset.UtcNow).ArtifactPath));
    }

    [Fact]
    public void BuildsEvidenceInputFromWalkForwardBenchmarkSummary()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var multiWindow = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            CryptoTrading.Backtesting.BacktestEvaluationOptions.DailyCrypto(),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20));

        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.FromWalkForward(
            "experiment-oos-001", PreregistrationHash, 1, 1, 0, false, "Rejected", multiWindow,
            new Dictionary<string, string> { ["dataset"] = dataset.Metadata.ContentHash }, ["AC-RESEARCH-OOS-01"]));
        var evaluation = WalkForwardEvaluator.Evaluate(dataset.Metadata, dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            CryptoTrading.Backtesting.BacktestEvaluationOptions.DailyCrypto(),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray());

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
        using var temporary = new TemporaryDirectory();
        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);
        Assert.Equal(ExperimentEvidenceWriter.WindowProvenanceSchemaVersion, bundle.Artifact.SchemaVersion);
        var expectedCanonical = JsonSerializer.Serialize(new
        {
            schema_version = ExperimentEvidenceWriter.WindowProvenanceSchemaVersion,
            experiment_id = input.ExperimentId,
            preregistration_sha256 = input.PreregistrationSha256.ToUpperInvariant(),
            trial_count = input.TrialCount,
            trial_budget = input.TrialBudget,
            holdout_access_count = input.HoldoutAccessCount,
            holdout_contaminated = input.HoldoutContaminated,
            decision = input.Decision,
            benchmark_net_returns = input.BenchmarkNetReturns.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            benchmark_comparison_fingerprint = input.BenchmarkComparisonFingerprint.ToUpperInvariant(),
            execution_timing = input.ExecutionTiming?.ToString(),
            evaluation_assumptions_fingerprint = input.EvaluationAssumptionsFingerprint?.ToUpperInvariant(),
            observed_series_fingerprint = input.ObservedSeriesFingerprint!.ToUpperInvariant(),
            declared_dataset_content_hash = input.DeclaredDatasetContentHash!.ToUpperInvariant(),
            observed_series_fingerprint_version = input.ObservedSeriesFingerprintVersion,
            source_versions = input.SourceVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
        Assert.Equal(expectedCanonical, bundle.Artifact.CanonicalJson);
        using var json = JsonDocument.Parse(bundle.Artifact.CanonicalJson);
        Assert.Equal(evaluation.ObservedSeriesFingerprint, json.RootElement.GetProperty("observed_series_fingerprint").GetString());
        Assert.Equal(dataset.Metadata.ContentHash, json.RootElement.GetProperty("declared_dataset_content_hash").GetString());
    }

    [Fact]
    public void BuildsEvidenceInputOnlyWhenExperimentLineageMatches()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var evaluation = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            CryptoTrading.Backtesting.BacktestEvaluationOptions.DailyCrypto(),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray());
        var preregistration = CreatePreregistration(dataset.Metadata, evaluation.StrategyVersion);
        var registry = new InMemoryExperimentRegistry();
        var registration = registry.Register(preregistration);
        registry.RecordTrial(preregistration.ExperimentId, new ExperimentTrial(
            1,
            "baseline",
            "baseline-v1",
            new string('C', 64),
            ExperimentDecision.Rejected,
            DateTimeOffset.UtcNow,
            "test",
            new IdempotencyKey("trial-1")));

        var input = ExperimentEvidenceWriter.FromExperimentRecord(
            registry.GetById(preregistration.ExperimentId),
            evaluation,
            "Rejected",
            new Dictionary<string, string> { ["dataset"] = dataset.Metadata.Version },
            ["AC-RESEARCH-LINEAGE-01"]);

        Assert.Equal(preregistration.PreregistrationSha256, input.PreregistrationSha256);
        var persistedInput = ExperimentEvidenceWriter.FromGovernanceState(
            preregistration,
            trialCount: 1,
            holdoutAccessCount: 0,
            holdoutContaminated: false,
            evaluation,
            "Rejected",
            new Dictionary<string, string> { ["dataset"] = dataset.Metadata.Version },
            ["AC-RESEARCH-LINEAGE-03"]);
        Assert.Equal(input.BenchmarkComparisonFingerprint, persistedInput.BenchmarkComparisonFingerprint);
        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.FromExperimentRecord(
            registry.GetById(preregistration.ExperimentId),
            evaluation with
            {
                Dataset = new DatasetMetadata(
                    dataset.Metadata.DatasetId,
                    "other",
                    dataset.Metadata.Source,
                    dataset.Metadata.SchemaVersion,
                    dataset.Metadata.ContentHash,
                    dataset.Metadata.IngestedAtUtc,
                    dataset.Metadata.TimeZoneId)
            },
            "Rejected",
            new Dictionary<string, string>(),
            ["AC-RESEARCH-LINEAGE-02"]));
    }

    [Theory]
    [InlineData("PaperEligible")]
    [InlineData("ShadowEligible")]
    [InlineData("CanaryEligible")]
    public void LegacyWalkForwardCannotProduceEligibleEvidence(string decision)
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var evaluation = WalkForwardEvaluator.Evaluate(dataset.Metadata, dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            new BacktestEvaluationOptions(10_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray());

        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.FromWalkForward(
            "legacy-oos", PreregistrationHash, 1, 1, 0, false, decision, evaluation,
            new Dictionary<string, string>(), ["AC-RESEARCH-OOS-01"]));
    }

    [Fact]
    public void EligibleEvidenceWithoutExecutionProvenanceIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.Write(
            temporary.Path, CreateInput() with { Decision = "PaperEligible" }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EligibleEvidenceWithoutObservedSeriesAndDatasetIdentityIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            Decision = "PaperEligible",
            ExecutionTiming = BacktestExecutionTiming.NextBarClose,
            EvaluationAssumptionsFingerprint = new string('A', 64)
        };

        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ArbitraryWellFormedHashesCannotAuthorizeEligibleEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            Decision = "PaperEligible",
            ExecutionTiming = BacktestExecutionTiming.NextBarClose,
            EvaluationAssumptionsFingerprint = new string('A', 64),
            ObservedSeriesFingerprint = new string('B', 64),
            DeclaredDatasetContentHash = new string('C', 64)
        };

        Assert.Throws<InvalidOperationException>(() => ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HistoricalResultWithoutObservedSeriesCanWriteRejectedEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var evaluation = WalkForwardEvaluator.Evaluate(dataset.Metadata, dataset.Bars,
            CryptoTrading.Backtesting.Strategies.TrendFollowingParameters.BtcUsdtBaseline,
            new BacktestEvaluationOptions(10_000m, 365, executionTiming: BacktestExecutionTiming.LegacySignalBarClose),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray()) with
        {
            EvaluationAssumptionsFingerprint = null,
            ObservedSeriesFingerprint = null,
            ObservedSeriesFingerprintVersion = null
        };
        var input = ExperimentEvidenceWriter.FromWalkForward("historical", PreregistrationHash, 1, 1, 0, false,
            "Rejected", evaluation, new Dictionary<string, string>(), ["AC-RESEARCH-OOS-01"]);

        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);

        Assert.Equal("Rejected", bundle.Artifact.Decision);
    }

    [Fact]
    public void DatasetProvenanceIncludesExplicitNullTimingWhenUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var input = CreateInput() with
        {
            ObservedSeriesFingerprint = new string('A', 64),
            DeclaredDatasetContentHash = new string('B', 64),
            BenchmarkNetReturns = new Dictionary<string, decimal> { ["NO_TRADE/cash"] = 0m, ["buy-and-hold"] = 0m, ["strategy/walk-forward"] = 0m }
        };
        var bundle = ExperimentEvidenceWriter.Write(temporary.Path, input, DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(bundle.Artifact.CanonicalJson);

        Assert.Equal(ExperimentEvidenceWriter.DatasetProvenanceSchemaVersion, bundle.Artifact.SchemaVersion);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("execution_timing").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("evaluation_assumptions_fingerprint").ValueKind);
    }

    private static ExperimentPreregistration CreatePreregistration(DatasetMetadata dataset, string strategyVersion) =>
        new(
            new ExperimentId(Guid.NewGuid()),
            "trend-following-family",
            "Baseline has positive net return after declared costs.",
            "baseline-only",
            ["baseline"],
            1,
            "net-return",
            "Reject if net return is not positive after costs.",
            "NO_TRADE/cash and buy-and-hold",
            DateTimeOffset.UtcNow,
            "test",
            new IdempotencyKey("registration-" + Guid.NewGuid().ToString("N")),
            strategyVersion,
            "local",
            dataset.Version,
            dataset.ContentHash,
            "holdout-v1",
            "metrics-v1",
            "local-test",
            new string('D', 64),
            7,
            dataset.IngestedAtUtc,
            dataset.IngestedAtUtc.AddDays(180));

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
