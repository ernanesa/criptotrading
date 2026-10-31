using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using CryptoTrading.Backtesting.Strategies;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>Explicit offline training. Never called by the decision cycle.</summary>
public static class MlDecisionTrainer
{
    [RequiresDynamicCode("ML.NET training runs only in an explicit offline JIT research process.")]
    [RequiresUnreferencedCode("Offline training binds ML.NET rows; exported inference does not.")]
    public static MlArtifactExport TrainFromBars(IReadOnlyList<CloseBar> bars, MlArtifactPolicy policy,
        DateTimeOffset createdAtUtc, DateTimeOffset validUntilUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValid || policy.UsesOrderBook)
            throw new ArgumentException("OHLC bars support bar-only artifacts; historical book observations must be supplied explicitly.", nameof(policy));
        var horizon = policy.HorizonBars;
        if (bars.Count > 100_000 || bars.Count < 8 + 3 * horizon + 64)
            throw new ArgumentException("Insufficient bar history for three purged ML partitions.", nameof(bars));
        bars = bars.ToArray();
        var frame = TimeSpan.FromTicks(policy.HorizonDuration.Ticks / horizon);
        for (var i = 0; i < bars.Count; i++)
            if (bars[i].Symbol.Value != policy.Symbol || bars[i].CloseTimeUtc.Offset != TimeSpan.Zero ||
                i > 0 && bars[i].CloseTimeUtc - bars[i - 1].CloseTimeUtc != frame)
                throw new ArgumentException("ML bar history must match its symbol, UTC timestamps and declared cadence.", nameof(bars));

        List<MlTrainingObservation> observations = new(bars.Count - 8 - horizon);
        for (var i = 8; i + horizon < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, _, _, dispersion) = MlNetSystem1DecisionEngine.ComputePointInTimeFeatures(bars, i, 8);
            var features = MlNetSystem1DecisionEngine.CreateFeatureVector(bars, i, null);
            var label = MlNetSystem1DecisionEngine.EvaluateChronologicalTripleBarrier(bars, i, horizon, policy.Side,
                Math.Max(180m, dispersion * 3m), Math.Max(100m, dispersion * 1.8m));
            observations.Add(new(bars[i].CloseTimeUtc, bars[i + horizon].CloseTimeUtc, features, label));
        }
        // Purge a full outcome horizon at each boundary. Labels from the last
        // retained sample strictly precede the next partition's first feature.
        var available = observations.Count - 2 * horizon;
        var trainCount = Math.Clamp((int)(available * 0.6m), 32, available - 32);
        var calibrationCount = (available - trainCount) / 2;
        var calibrationStart = trainCount + horizon;
        var validationStart = calibrationStart + calibrationCount + horizon;
        return Train(observations.Take(trainCount).ToArray(), observations.Skip(calibrationStart).Take(calibrationCount).ToArray(),
            observations.Skip(validationStart).ToArray(), policy, createdAtUtc, validUntilUtc, cancellationToken);
    }

    [RequiresDynamicCode("ML.NET training runs in a JIT research process; inference uses exported numeric parameters.")]
    [RequiresUnreferencedCode("ML.NET offline IDataView binding requires training-row metadata.")]
    public static MlArtifactExport Train(
        IReadOnlyList<MlTrainingObservation> training,
        IReadOnlyList<MlTrainingObservation> calibration,
        IReadOnlyList<MlTrainingObservation> validation,
        MlArtifactPolicy policy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset validUntilUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValid) throw new ArgumentException("Invalid ML artifact policy.", nameof(policy));
        var train = Snapshot(training, 32, policy, nameof(training));
        var calibrate = Snapshot(calibration, 16, policy, nameof(calibration));
        var validate = Snapshot(validation, 16, policy, nameof(validation));
        var trainLabelsThrough = train.Max(static row => row.LabelAvailableAtUtc);
        var calibrationLabelsThrough = calibrate.Max(static row => row.LabelAvailableAtUtc);
        var validationLabelsThrough = validate.Max(static row => row.LabelAvailableAtUtc);
        if (trainLabelsThrough >= calibrate[0].FeatureAtUtc || calibrationLabelsThrough >= validate[0].FeatureAtUtc ||
            validationLabelsThrough >= createdAtUtc || createdAtUtc >= validUntilUtc ||
            createdAtUtc.Offset != TimeSpan.Zero || validUntilUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ML training, calibration and validation require strictly separated, matured labels.");
        cancellationToken.ThrowIfCancellationRequested();

        var means = new double[MlDecisionArtifact.FeatureCount];
        var inverseStd = new double[MlDecisionArtifact.FeatureCount];
        var varying = new bool[MlDecisionArtifact.FeatureCount];
        for (var feature = 0; feature < means.Length; feature++)
        {
            double mean = 0d, m2 = 0d;
            for (var i = 0; i < train.Length; i++)
            {
                var value = feature == 4 && !policy.UsesOrderBook ? 0d : (double)train[i].Features.At(feature);
                var delta = value - mean;
                mean += delta / (i + 1);
                m2 += delta * (value - mean);
            }
            means[feature] = mean;
            varying[feature] = m2 > 0d;
            var std = Math.Sqrt(Math.Max(0d, m2 / (train.Length - 1)));
            inverseStd[feature] = std > 0d ? 1d / Math.Max(1e-12, std) : 1d;
        }

        // ML.NET GAM compresses constant columns internally. Pack them explicitly
        // so exported public shape indices retain a verified original mapping.
        var active = Enumerable.Range(0, means.Length).Where(index => varying[index]).ToArray();
        if (active.Length == 0) throw new ArgumentException("ML training features are constant.", nameof(training));
        var ml = new MLContext(seed: 42);
        var schema = SchemaDefinition.Create(typeof(TrainingRow));
        schema[nameof(TrainingRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, active.Length);
        var rows = train.Select(row => Row(row.Features, row.Label, means, inverseStd, policy.UsesOrderBook, active)).ToArray();
        var data = ml.Data.LoadFromEnumerable(rows, schemaDefinition: schema);
        var linear = ml.BinaryClassification.Trainers.SdcaLogisticRegression(new SdcaLogisticRegressionBinaryTrainer.Options
        {
            LabelColumnName = nameof(TrainingRow.Label), FeatureColumnName = nameof(TrainingRow.Features),
            MaximumNumberOfIterations = 35, NumberOfThreads = 1, L2Regularization = 0.05f, L1Regularization = 0.01f
        }).Fit(data);
        cancellationToken.ThrowIfCancellationRequested();
        var gam = ml.BinaryClassification.Trainers.Gam(new GamBinaryTrainer.Options
        {
            LabelColumnName = nameof(TrainingRow.Label), FeatureColumnName = nameof(TrainingRow.Features),
            NumberOfIterations = 45, NumberOfThreads = 1, LearningRate = 0.02, MaximumBinCountPerFeature = 16
        }).Fit(data);
        cancellationToken.ThrowIfCancellationRequested();

        var shapes = Enumerable.Range(0, MlDecisionArtifact.FeatureCount).Select(static _ => new MlGamShape([], [])).ToArray();
        for (var packed = 0; packed < active.Length; packed++)
        {
            if (packed >= gam.Model.SubModel.NumberOfShapeFunctions)
                throw new InvalidDataException("ML_GAM_EXPORT_FEATURE_MAPPING_UNSUPPORTED");
            var bounds = gam.Model.SubModel.GetBinUpperBounds(packed);
            var effects = gam.Model.SubModel.GetBinEffects(packed);
            if (bounds.Count == 0 && effects.Count == 0)
            {
                continue;
            }
            if (bounds.Count != effects.Count || !double.IsPositiveInfinity(bounds[^1]))
                throw new InvalidDataException("ML_GAM_EXPORT_BIN_LAYOUT_UNSUPPORTED");
            shapes[active[packed]] = new(bounds.Take(bounds.Count - 1).ToArray(), effects.ToArray());
        }
        if (linear.Model.SubModel.Weights.Count != active.Length)
            throw new InvalidDataException("ML_LINEAR_EXPORT_FEATURE_MAPPING_UNSUPPORTED");
        var linearWeights = new float[MlDecisionArtifact.FeatureCount];
        for (var i = 0; i < active.Length; i++) linearWeights[active[i]] = linear.Model.SubModel.Weights[i];

        var fingerprint = new MlTrainingFingerprint(policy, train, calibrate, validate);
        var datasetHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fingerprint,
            MlArtifactJsonContext.Default.MlTrainingFingerprint)));
        var metadata = new MlArtifactMetadata($"{policy.Symbol}-{policy.Timeframe}-{policy.Side}-{datasetHash[..16]}",
            policy.UsesOrderBook ? MlDecisionArtifact.BookSchema : MlDecisionArtifact.BarsSchema, policy, datasetHash,
            train[0].FeatureAtUtc, trainLabelsThrough, calibrate[0].FeatureAtUtc, calibrationLabelsThrough,
            validate[0].FeatureAtUtc, validationLabelsThrough, createdAtUtc, validUntilUtc,
            train.Length, train.Count(static row => row.Label), calibrate.Length, calibrate.Count(static row => row.Label),
            validate.Length, validate.Count(static row => row.Label), 0m, 0m);
        var parameters = new MlArtifactPayload(metadata, means, inverseStd, linearWeights,
            linear.Model.SubModel.Bias, gam.Model.SubModel.Bias, shapes, PlattParameters.Default);
        var numericModel = new MlDecisionArtifact(parameters);

        // Compare against the actual learned ML.NET estimators before publishing
        // any candidate. PredictionEngine exists only in this offline method.
        using var linearOracle = ml.Model.CreatePredictionEngine<TrainingRow, ScoreRow>(linear, inputSchemaDefinition: schema);
        using var gamOracle = ml.Model.CreatePredictionEngine<TrainingRow, ScoreRow>(gam, inputSchemaDefinition: schema);
        decimal maximumError = 0m;
        foreach (var observation in calibrate.Concat(validate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = Row(observation.Features, observation.Label, means, inverseStd, policy.UsesOrderBook, active);
            var expected = (decimal)linearOracle.Predict(row).Score * 0.55m + (decimal)gamOracle.Predict(row).Score * 0.45m;
            maximumError = Math.Max(maximumError, Math.Abs(expected - numericModel.RawScore(observation.Features)));
        }
        if (maximumError > 0.0001m) throw new InvalidDataException("ML_EXPORT_SCORE_PARITY_FAILED");

        var fitted = PlattCalibrator.Fit(calibrate.Select(row => (numericModel.RawScore(row.Features), row.Label)).ToArray());
        var predictions = validate.Select(row => (fitted.Transform(numericModel.RawScore(row.Features)), row.Label)).ToArray();
        var report = MlNetSystem1DecisionEngine.CalculateCalibrationReport(predictions);
        parameters = parameters with
        {
            Calibration = fitted,
            Metadata = metadata with { ValidationBrierScore = report.BrierScore, ValidationEce = report.ExpectedCalibrationError }
        };
        _ = new MlDecisionArtifact(parameters);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters, MlArtifactJsonContext.Default.MlArtifactPayload);
        if (bytes.Length > MlDecisionArtifact.MaximumPayloadBytes) throw new InvalidDataException("ML_ARTIFACT_PAYLOAD_TOO_LARGE");
        return new MlArtifactExport(bytes, Convert.ToHexString(SHA256.HashData(bytes)), maximumError);
    }

    private static MlTrainingObservation[] Snapshot(IReadOnlyList<MlTrainingObservation> source, int minimum,
        MlArtifactPolicy policy, string parameterName)
    {
        if (source.Count < minimum || source.Count > 100_000)
            throw new ArgumentException("ML partition has insufficient or excessive observations.", parameterName);
        var copy = source.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            var row = copy[i];
            if (!row.Features.IsValid(policy.UsesOrderBook) || policy.UsesOrderBook && row.Features.OrderBookImbalance is null ||
                row.FeatureAtUtc.Offset != TimeSpan.Zero || row.LabelAvailableAtUtc.Offset != TimeSpan.Zero ||
                row.LabelAvailableAtUtc - row.FeatureAtUtc < policy.HorizonDuration || i > 0 && row.FeatureAtUtc <= copy[i - 1].FeatureAtUtc)
                throw new ArgumentException("ML observations require valid point-in-time features and matured labels.", parameterName);
        }
        if (!copy.Any(static row => row.Label) || !copy.Any(static row => !row.Label))
            throw new ArgumentException("Each ML partition must contain both observed classes.", parameterName);
        return copy;
    }

    private static TrainingRow Row(in MlFeatureVector features, bool label, double[] means, double[] inverseStd, bool usesBook, int[] active)
    {
        Span<float> full = stackalloc float[MlDecisionArtifact.FeatureCount];
        MlDecisionArtifact.Normalize(features, means, inverseStd, usesBook, full);
        var vector = new float[active.Length];
        for (var i = 0; i < active.Length; i++) vector[i] = full[active[i]];
        return new TrainingRow { Features = vector, Label = label };
    }

    private sealed class TrainingRow
    {
        public float[] Features { get; set; } = [];
        public bool Label { get; set; }
    }

    private sealed class ScoreRow
    {
        public float Score { get; set; }
    }
}
