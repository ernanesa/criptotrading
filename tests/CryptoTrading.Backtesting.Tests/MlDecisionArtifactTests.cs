using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MlDecisionArtifactTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly MlArtifactPolicy Policy = new("BTCUSDT", "1m", OrderSide.Buy, 4, UsesOrderBook: false);

    [Fact]
    public void ExportedEnsembleMatchesMlNetScoresAndUsesFittedOosCalibration()
    {
        var export = Train();
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        Assert.Equal(export.Sha256, artifact.VerifiedSha256);
        Assert.InRange(export.MaximumScoreRoundTripError, 0m, 0.0001m);
        Assert.Equal(64, artifact.Metadata.TrainingCount);
        Assert.Equal(32, artifact.Metadata.CalibrationCount);
        Assert.Equal(32, artifact.Metadata.ValidationCount);
        Assert.Equal("system1-bars-v2", artifact.Metadata.FeatureSchemaVersion);
        Assert.InRange(artifact.Metadata.ValidationBrierScore, 0m, 1m);
        Assert.True(artifact.TryPredict(Context(new(-3m, -6m, -9m, 5m)), out var negative));
        Assert.True(artifact.TryPredict(Context(new(3m, 6m, 9m, 5m)), out var positive));
        Assert.NotNull(negative.Probability);
        Assert.NotNull(positive.Probability);
        Assert.True(negative.Probability > positive.Probability);
        Assert.NotEqual(negative.RawScore, positive.RawScore);
        Assert.Equal(artifact.Metadata.ModelId, negative.ModelId);
    }

    [Fact]
    public void LoaderRejectsTamperingAndUnsupportedSchemaEvenWithMatchingDigest()
    {
        var export = Train();
        var changed = export.Payload.ToArray();
        changed[changed.Length / 2] ^= 1;
        Assert.Throws<InvalidDataException>(() => MlDecisionArtifact.LoadVerified(changed, export.Sha256));
        var json = JsonNode.Parse(export.Payload.Span)!;
        json["metadata"]!["featureSchemaVersion"] = "unknown-schema";
        var unsupported = Encoding.UTF8.GetBytes(json.ToJsonString());
        var digest = Convert.ToHexString(SHA256.HashData(unsupported));
        Assert.Throws<InvalidDataException>(() => MlDecisionArtifact.LoadVerified(unsupported, digest));
    }

    [Fact]
    public void TrainerRejectsUnmaturedOrOverlappingLabelsAndSingleClassCalibration()
    {
        var train = Samples(0, 64);
        var calibration = Samples(80, 32);
        var validation = Samples(130, 32);
        train[^1] = train[^1] with { LabelAvailableAtUtc = calibration[0].FeatureAtUtc };
        Assert.Throws<ArgumentException>(() => MlDecisionTrainer.Train(train, calibration, validation,
            Policy, Start.AddMinutes(180), Start.AddDays(1), TestContext.Current.CancellationToken));
        var singleClass = calibration.Select(row => row with { Label = true }).ToArray();
        Assert.Throws<ArgumentException>(() => MlDecisionTrainer.Train(Samples(0, 64), singleClass, validation,
            Policy, Start.AddMinutes(180), Start.AddDays(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ModelRejectsExpiredFutureAndIncompatibleRequestsWithoutInventingProbability()
    {
        var export = Train();
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        var valid = Context(new(-2m, -4m, -6m, 4m));
        MlInferenceContext[] invalid = [
            valid with { FeatureAtUtc = Start.AddMinutes(160) },
            valid with { EvaluatedAtUtc = Start.AddDays(2) },
            valid with { FeatureAtUtc = valid.EvaluatedAtUtc.AddMinutes(1) },
            valid with { Symbol = "ETHUSDT" },
            valid with { Timeframe = "5m" },
            valid with { HorizonBars = 8 },
            valid with { Side = OrderSide.Sell },
            valid with { Features = new(1000m, 0m, 0m, 1m) }];
        foreach (var context in invalid)
        {
            Assert.False(artifact.TryPredict(context, out var prediction));
            Assert.Null(prediction.Probability);
            Assert.Null(prediction.RawScore);
            Assert.NotNull(prediction.RejectionReason);
        }
    }

    [Fact]
    public void BookAwareModelRequiresObservedBookFeaturesAndCopiesItsInputPayload()
    {
        var export = Train(usesBook: true);
        var bytes = export.Payload.ToArray();
        var artifact = MlDecisionArtifact.LoadVerified(bytes, export.Sha256);
        var context = Context(new(-2m, -4m, -6m, 4m, -0.5m));
        Assert.True(artifact.TryPredict(context, out var before));
        Array.Clear(bytes);
        Assert.True(artifact.TryPredict(context, out var after));
        Assert.Equal(before, after);
        Assert.False(artifact.TryPredict(context with { Features = context.Features with { OrderBookImbalance = null } }, out var missing));
        Assert.Equal("ML_ORDER_BOOK_FEATURE_UNAVAILABLE", missing.RejectionReason);
    }

    [Fact]
    public void ConstantFeaturesInTheMiddleDoNotShiftGamFeatureMappings()
    {
        static MlTrainingObservation[] ConstantSlope(MlTrainingObservation[] rows) =>
            rows.Select(row => row with { Features = row.Features with { SlopeBpsPerBar = 0m } }).ToArray();
        var export = MlDecisionTrainer.Train(ConstantSlope(Samples(0, 64, true)), ConstantSlope(Samples(80, 32, true)),
            ConstantSlope(Samples(130, 32, true)), Policy with { UsesOrderBook = true },
            Start.AddMinutes(180), Start.AddDays(1), TestContext.Current.CancellationToken);
        Assert.InRange(export.MaximumScoreRoundTripError, 0m, 0.0001m);
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        Assert.True(artifact.TryPredict(Context(new(-2m, 0m, -6m, 4m, -0.5m)), out var prediction));
        Assert.NotNull(prediction.Probability);
    }

    [Theory]
    [InlineData("1m", 4)]
    [InlineData("999w", 32)]
    public void TrainerDoesNotAcceptLabelsBeforeTheDeclaredHorizon(string timeframe, int horizon)
    {
        var training = Samples(0, 64);
        training[0] = training[0] with { LabelAvailableAtUtc = training[0].FeatureAtUtc.AddSeconds(1) };
        Assert.Throws<ArgumentException>(() => MlDecisionTrainer.Train(training, Samples(80, 32), Samples(130, 32),
            Policy with { Timeframe = timeframe, HorizonBars = horizon }, Start.AddMinutes(180), Start.AddDays(1),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("metadata", "validationBrierScore")]
    [InlineData("metadata", "validationEce")]
    [InlineData("", "linearBias")]
    [InlineData("calibration", "beta")]
    public void LoaderRequiresExplicitFieldsEvenIfMissingValuesWouldDefaultToZero(string parent, string property)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var export = Train();
        var json = JsonNode.Parse(export.Payload.Span)!;
        var target = parent.Length == 0 ? json : json[parent]!;
        Assert.True(target.AsObject().Remove(property));
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => MlDecisionArtifact.LoadVerified(bytes, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    [Fact]
    public void BarOnlyModelIgnoresBookInputsBothDuringTrainingAndInference()
    {
        static MlTrainingObservation[] WithIrrelevantBook(MlTrainingObservation[] rows) =>
            rows.Select(row => row with { Features = row.Features with { OrderBookImbalance = 1000m } }).ToArray();
        var export = MlDecisionTrainer.Train(WithIrrelevantBook(Samples(0, 64)), WithIrrelevantBook(Samples(80, 32)),
            WithIrrelevantBook(Samples(130, 32)), Policy, Start.AddMinutes(180), Start.AddDays(1), TestContext.Current.CancellationToken);
        var artifact = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        var context = Context(new(-2m, -4m, -6m, 4m));
        Assert.True(artifact.TryPredict(context, out var withoutBook));
        Assert.True(artifact.TryPredict(context with { Features = context.Features with { OrderBookImbalance = 1000m } }, out var ignoredBook));
        Assert.Equal(withoutBook, ignoredBook);
    }

    [Fact]
    public void CatalogSelectsOnlyTheExactSymbolTimeframeSideAndHorizon()
    {
        var btc = Train();
        var eth = Train(symbol: "ETHUSDT");
        var bundle = MlArtifactCatalog.CreateBundle([btc, eth]);
        var catalog = MlArtifactCatalog.LoadVerified(bundle.Payload.Span, bundle.Sha256);
        Assert.Equal(2, catalog.Count);
        Assert.Equal("BTCUSDT", catalog.Find("BTCUSDT", "1m", OrderSide.Buy, 4)!.Metadata.Policy.Symbol);
        Assert.Equal("ETHUSDT", catalog.Find("ETHUSDT", "1m", OrderSide.Buy, 4)!.Metadata.Policy.Symbol);
        Assert.Null(catalog.Find("BTCUSDT", "5m", OrderSide.Buy, 4));
        Assert.Null(catalog.Find("BTCUSDT", "1m", OrderSide.Sell, 4));
        Assert.Null(catalog.Find("BTCUSDT", "1m", OrderSide.Buy, 8));
        Assert.Throws<InvalidDataException>(() => MlArtifactCatalog.CreateBundle([btc, btc]));
    }

    [Fact]
    public void CatalogVerifiesBothExternalDigestAndEachBundledPayload()
    {
        var bundle = MlArtifactCatalog.CreateBundle([Train()]);
        var json = JsonNode.Parse(bundle.Payload.Span)!;
        json["models"]![0]!["sha256"] = new string('0', 64);
        var tampered = Encoding.UTF8.GetBytes(json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => MlArtifactCatalog.LoadVerified(tampered, bundle.Sha256));
        Assert.Throws<InvalidDataException>(() => MlArtifactCatalog.LoadVerified(tampered, Convert.ToHexString(SHA256.HashData(tampered))));
    }

    [Fact]
    public void OfflineBarPipelineBuildsMaturedLabelsAndPurgesFoldBoundaries()
    {
        var symbol = new Symbol("BTCUSDT");
        var bars = Enumerable.Range(0, 256).Select(i => new CloseBar(symbol, Start.AddMinutes(i), i % 24 < 12 ? 100m : 103m)).ToArray();
        var export = MlDecisionTrainer.TrainFromBars(bars, Policy, Start.AddHours(5), Start.AddDays(1), TestContext.Current.CancellationToken);
        var model = MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256);
        Assert.True(model.Metadata.TrainingLabelsThroughUtc < model.Metadata.CalibrationStartedAtUtc);
        Assert.True(model.Metadata.CalibrationLabelsThroughUtc < model.Metadata.ValidationStartedAtUtc);
        Assert.True(model.Metadata.ValidationLabelsThroughUtc < model.Metadata.CreatedAtUtc);
        Assert.InRange(model.Metadata.CalibrationPositiveCount, 1, model.Metadata.CalibrationCount - 1);
        Assert.Throws<ArgumentException>(() => MlDecisionTrainer.TrainFromBars(bars, Policy with { UsesOrderBook = true },
            Start.AddHours(5), Start.AddDays(1), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => MlDecisionTrainer.TrainFromBars(bars, Policy with { Timeframe = "5m" },
            Start.AddHours(5), Start.AddDays(1), TestContext.Current.CancellationToken));
    }

    private static MlInferenceContext Context(MlFeatureVector features) =>
        new("BTCUSDT", "1m", OrderSide.Buy, 4, Start.AddMinutes(181), Start.AddMinutes(181), features);

    internal static MlArtifactExport Train(bool usesBook = false, string symbol = "BTCUSDT") =>
        MlDecisionTrainer.Train(Samples(0, 64, usesBook), Samples(80, 32, usesBook), Samples(130, 32, usesBook),
            Policy with { UsesOrderBook = usesBook, Symbol = symbol }, Start.AddMinutes(180), Start.AddDays(1), TestContext.Current.CancellationToken);

    private static MlTrainingObservation[] Samples(int offset, int count, bool usesBook = false) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var x = (i % 16 - 8) / 2m;
            var at = Start.AddMinutes(offset + i);
            return new MlTrainingObservation(at, at.AddMinutes(4),
                new MlFeatureVector(x, x * 2m, x * 3m, 2m + Math.Abs(x), usesBook ? x / 4m : null), x < 0m);
        }).ToArray();
}
