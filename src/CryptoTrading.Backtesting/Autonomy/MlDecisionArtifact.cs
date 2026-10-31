using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Backtesting.Autonomy;

public readonly record struct MlFeatureVector(
    decimal ZScore, decimal SlopeBpsPerBar, decimal Momentum3BarBps,
    decimal PriceDispersionBps, decimal? OrderBookImbalance = null)
{
    internal bool IsValid(bool usesOrderBook) => ZScore is >= -4m and <= 4m && SlopeBpsPerBar is >= -15m and <= 15m &&
        Momentum3BarBps is >= -30m and <= 30m && PriceDispersionBps is >= 0.5m and <= 30m &&
        (!usesOrderBook || OrderBookImbalance is null or (>= -1m and <= 1m));

    internal decimal At(int index) => index switch
    {
        0 => ZScore, 1 => SlopeBpsPerBar, 2 => Momentum3BarBps,
        3 => PriceDispersionBps, 4 => OrderBookImbalance ?? 0m,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

public readonly record struct MlTrainingObservation(
    DateTimeOffset FeatureAtUtc, DateTimeOffset LabelAvailableAtUtc, MlFeatureVector Features, bool Label);

public sealed record MlArtifactPolicy(
    [property: JsonRequired] string Symbol,
    [property: JsonRequired] string Timeframe,
    [property: JsonRequired] OrderSide Side,
    [property: JsonRequired] int HorizonBars,
    [property: JsonRequired] bool UsesOrderBook = false)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Symbol) && Symbol.Length is >= 3 and <= 20 &&
        Symbol.All(static value => char.IsAsciiLetterOrDigit(value) && !char.IsAsciiLetterLower(value)) &&
        !string.IsNullOrWhiteSpace(Timeframe) && Timeframe.Length is >= 2 and <= 4 &&
        Timeframe[^1] is 's' or 'm' or 'h' or 'd' or 'w' &&
        int.TryParse(Timeframe.AsSpan(0, Timeframe.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var period) && period > 0 &&
        Side is OrderSide.Buy or OrderSide.Sell && HorizonBars is >= 1 and <= 32;

    internal TimeSpan HorizonDuration
    {
        get
        {
            var period = int.Parse(Timeframe.AsSpan(0, Timeframe.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture);
            var unitTicks = Timeframe[^1] switch
            {
                's' => TimeSpan.TicksPerSecond, 'm' => TimeSpan.TicksPerMinute,
                'h' => TimeSpan.TicksPerHour, 'd' => TimeSpan.TicksPerDay,
                'w' => 7 * TimeSpan.TicksPerDay,
                _ => throw new InvalidDataException("ML_TIMEFRAME_INVALID")
            };
            return TimeSpan.FromTicks(checked(unitTicks * period * HorizonBars));
        }
    }
}

public sealed record MlArtifactMetadata(
    [property: JsonRequired] string ModelId,
    [property: JsonRequired] string FeatureSchemaVersion,
    [property: JsonRequired] MlArtifactPolicy Policy,
    [property: JsonRequired] string DatasetSha256,
    [property: JsonRequired] DateTimeOffset TrainingStartedAtUtc,
    [property: JsonRequired] DateTimeOffset TrainingLabelsThroughUtc,
    [property: JsonRequired] DateTimeOffset CalibrationStartedAtUtc,
    [property: JsonRequired] DateTimeOffset CalibrationLabelsThroughUtc,
    [property: JsonRequired] DateTimeOffset ValidationStartedAtUtc,
    [property: JsonRequired] DateTimeOffset ValidationLabelsThroughUtc,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] DateTimeOffset ValidUntilUtc,
    [property: JsonRequired] int TrainingCount,
    [property: JsonRequired] int TrainingPositiveCount,
    [property: JsonRequired] int CalibrationCount,
    [property: JsonRequired] int CalibrationPositiveCount,
    [property: JsonRequired] int ValidationCount,
    [property: JsonRequired] int ValidationPositiveCount,
    [property: JsonRequired] decimal ValidationBrierScore,
    [property: JsonRequired] decimal ValidationEce);

public readonly record struct MlInferenceContext(
    string Symbol, string Timeframe, OrderSide Side, int HorizonBars,
    DateTimeOffset FeatureAtUtc, DateTimeOffset EvaluatedAtUtc, MlFeatureVector Features);

public readonly record struct MlArtifactPrediction(decimal? Probability, decimal? RawScore, string? ModelId, string? RejectionReason);

public sealed record MlArtifactExport(ReadOnlyMemory<byte> Payload, string Sha256, decimal MaximumScoreRoundTripError);

/// <summary>Immutable numerical inference; ML.NET training and reflection are not used here.</summary>
public sealed class MlDecisionArtifact
{
    public const string BarsSchema = "system1-bars-v2";
    public const string BookSchema = "system1-book-v2";
    public const int FeatureCount = 5;
    public const int MaximumPayloadBytes = 262_144;
    private readonly MlArtifactPayload data;
    public string? VerifiedSha256 { get; }

    internal MlDecisionArtifact(MlArtifactPayload payload) : this(payload, null) { }

    private MlDecisionArtifact(MlArtifactPayload payload, string? verifiedSha256)
    {
        ValidatePayload(payload);
        VerifiedSha256 = verifiedSha256;
        data = payload with
        {
            Means = (double[])payload.Means.Clone(), InverseStd = (double[])payload.InverseStd.Clone(),
            LinearWeights = (float[])payload.LinearWeights.Clone(),
            GamShapes = payload.GamShapes.Select(static shape => new MlGamShape(
                (double[])shape.UpperBounds.Clone(), (double[])shape.Effects.Clone())).ToArray()
        };
    }

    public MlArtifactMetadata Metadata => data.Metadata;

    public static MlDecisionArtifact LoadVerified(ReadOnlySpan<byte> payload, string expectedSha256)
    {
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes || expectedSha256 is null || expectedSha256.Length != 64)
            throw new InvalidDataException("ML_ARTIFACT_SIZE_OR_DIGEST_INVALID");
        // Snapshot untrusted input before checking its externally supplied digest.
        var owned = payload.ToArray();
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException error) { throw new InvalidDataException("ML_ARTIFACT_DIGEST_INVALID", error); }
        var actual = SHA256.HashData(owned);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("ML_ARTIFACT_HASH_MISMATCH");
        try
        {
            return new MlDecisionArtifact(JsonSerializer.Deserialize(owned, MlArtifactJsonContext.Default.MlArtifactPayload)
                ?? throw new InvalidDataException("ML_ARTIFACT_PAYLOAD_MISSING"), Convert.ToHexString(actual));
        }
        catch (JsonException error) { throw new InvalidDataException("ML_ARTIFACT_JSON_INVALID", error); }
    }

    public bool TryPredict(in MlInferenceContext context, out MlArtifactPrediction prediction)
    {
        var reason = RejectionReason(context);
        if (reason is not null)
        {
            prediction = new(null, null, Metadata.ModelId, reason);
            return false;
        }
        var raw = RawScore(context.Features);
        prediction = new(data.Calibration.Transform(raw), raw, Metadata.ModelId, null);
        return true;
    }

    private string? RejectionReason(in MlInferenceContext context)
    {
        var policy = Metadata.Policy;
        if (!string.Equals(context.Symbol, policy.Symbol, StringComparison.Ordinal) ||
            !string.Equals(context.Timeframe, policy.Timeframe, StringComparison.Ordinal) ||
            context.Side != policy.Side || context.HorizonBars != policy.HorizonBars)
            return "ML_ARTIFACT_CONTEXT_MISMATCH";
        if (context.FeatureAtUtc.Offset != TimeSpan.Zero || context.EvaluatedAtUtc.Offset != TimeSpan.Zero ||
            context.FeatureAtUtc > context.EvaluatedAtUtc || context.FeatureAtUtc < Metadata.CreatedAtUtc ||
            context.FeatureAtUtc <= Metadata.ValidationLabelsThroughUtc)
            return "ML_ARTIFACT_NOT_POINT_IN_TIME";
        if (context.EvaluatedAtUtc > Metadata.ValidUntilUtc) return "ML_ARTIFACT_EXPIRED";
        if (!context.Features.IsValid(policy.UsesOrderBook)) return "ML_FEATURE_OUT_OF_RANGE";
        if (policy.UsesOrderBook && context.Features.OrderBookImbalance is null) return "ML_ORDER_BOOK_FEATURE_UNAVAILABLE";
        return null;
    }

    internal decimal RawScore(in MlFeatureVector features)
    {
        Span<float> normalized = stackalloc float[FeatureCount];
        Normalize(features, data.Means, data.InverseStd, Metadata.Policy.UsesOrderBook, normalized);
        var linear = data.LinearBias;
        var gam = data.GamBias;
        for (var i = 0; i < FeatureCount; i++)
        {
            linear += normalized[i] * data.LinearWeights[i];
            var shape = data.GamShapes[i];
            if (shape.Effects.Length == 0) continue;
            var bin = Array.BinarySearch(shape.UpperBounds, (double)normalized[i]);
            gam += shape.Effects[bin < 0 ? ~bin : bin];
        }
        // These are dimensionless analytical margins, never prices or quantities.
        return (decimal)linear * 0.55m + (decimal)(float)gam * 0.45m;
    }

    internal static void Normalize(in MlFeatureVector features, double[] means, double[] inverseStd, bool usesBook, Span<float> destination)
    {
        for (var i = 0; i < FeatureCount; i++)
        {
            var value = i == 4 && !usesBook ? 0d : (double)features.At(i);
            destination[i] = (float)((value - means[i]) * inverseStd[i]);
        }
    }

    private static void ValidatePayload(MlArtifactPayload value)
    {
        if (value is null || value.Metadata is not { Policy: { } policy } metadata || !policy.IsValid ||
            string.IsNullOrWhiteSpace(metadata.ModelId) || metadata.ModelId.Length > 128 ||
            metadata.FeatureSchemaVersion != (policy.UsesOrderBook ? BookSchema : BarsSchema) ||
            !IsDigest(metadata.DatasetSha256) ||
            value.Means is not { Length: FeatureCount } || value.InverseStd is not { Length: FeatureCount } ||
            value.LinearWeights is not { Length: FeatureCount } || value.GamShapes is not { Length: FeatureCount } ||
            !float.IsFinite(value.LinearBias) || Math.Abs(value.LinearBias) > 1_000_000f ||
            !double.IsFinite(value.GamBias) || Math.Abs(value.GamBias) > 1_000_000d ||
            value.Calibration.Alpha is < 0.05m or > 20m || value.Calibration.Beta is < -20m or > 20m)
            throw new InvalidDataException("ML_ARTIFACT_SCHEMA_OR_PARAMETERS_INVALID");

        DateTimeOffset[] times = [metadata.TrainingStartedAtUtc, metadata.TrainingLabelsThroughUtc,
            metadata.CalibrationStartedAtUtc, metadata.CalibrationLabelsThroughUtc, metadata.ValidationStartedAtUtc,
            metadata.ValidationLabelsThroughUtc, metadata.CreatedAtUtc, metadata.ValidUntilUtc];
        if (times.Any(static at => at.Offset != TimeSpan.Zero) ||
            !times.Zip(times.Skip(1), static (a, b) => a < b).All(static ordered => ordered) ||
            metadata.TrainingCount < 32 || metadata.CalibrationCount < 16 || metadata.ValidationCount < 16 ||
            metadata.TrainingPositiveCount <= 0 || metadata.TrainingPositiveCount >= metadata.TrainingCount ||
            metadata.CalibrationPositiveCount <= 0 || metadata.CalibrationPositiveCount >= metadata.CalibrationCount ||
            metadata.ValidationPositiveCount <= 0 || metadata.ValidationPositiveCount >= metadata.ValidationCount ||
            metadata.ValidationBrierScore is < 0m or > 1m || metadata.ValidationEce is < 0m or > 1m)
            throw new InvalidDataException("ML_ARTIFACT_TEMPORAL_OR_CALIBRATION_EVIDENCE_INVALID");
        var horizon = policy.HorizonDuration;
        if (metadata.TrainingLabelsThroughUtc - metadata.TrainingStartedAtUtc < horizon ||
            metadata.CalibrationLabelsThroughUtc - metadata.CalibrationStartedAtUtc < horizon ||
            metadata.ValidationLabelsThroughUtc - metadata.ValidationStartedAtUtc < horizon)
            throw new InvalidDataException("ML_ARTIFACT_LABEL_HORIZON_INVALID");

        for (var i = 0; i < FeatureCount; i++)
        {
            var shape = value.GamShapes[i];
            if (!double.IsFinite(value.Means[i]) || Math.Abs(value.Means[i]) > 1_000_000d ||
                !double.IsFinite(value.InverseStd[i]) || value.InverseStd[i] is <= 0d or > 1e12 ||
                !float.IsFinite(value.LinearWeights[i]) || Math.Abs(value.LinearWeights[i]) > 1_000_000f ||
                shape?.UpperBounds is null || shape.Effects is null || shape.Effects.Length > 128 ||
                !(shape.UpperBounds.Length == 0 && shape.Effects.Length == 0 || shape.Effects.Length == shape.UpperBounds.Length + 1) ||
                shape.UpperBounds.Any(static bound => !double.IsFinite(bound)) ||
                shape.Effects.Any(static effect => !double.IsFinite(effect) || Math.Abs(effect) > 1_000_000d))
                throw new InvalidDataException("ML_ARTIFACT_NUMERIC_PARAMETERS_INVALID");
            for (var j = 1; j < shape.UpperBounds.Length; j++)
                if (shape.UpperBounds[j] <= shape.UpperBounds[j - 1])
                    throw new InvalidDataException("ML_ARTIFACT_BIN_ORDER_INVALID");
        }
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));
}

internal sealed record MlGamShape([property: JsonRequired] double[] UpperBounds, [property: JsonRequired] double[] Effects);
internal sealed record MlArtifactPayload(
    [property: JsonRequired] MlArtifactMetadata Metadata,
    [property: JsonRequired] double[] Means,
    [property: JsonRequired] double[] InverseStd,
    [property: JsonRequired] float[] LinearWeights,
    [property: JsonRequired] float LinearBias,
    [property: JsonRequired] double GamBias,
    [property: JsonRequired] MlGamShape[] GamShapes,
    [property: JsonRequired] PlattParameters Calibration);
internal sealed record MlTrainingFingerprint(MlArtifactPolicy Policy, MlTrainingObservation[] Training,
    MlTrainingObservation[] Calibration, MlTrainingObservation[] Validation);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MlArtifactPayload))]
[JsonSerializable(typeof(MlTrainingFingerprint))]
internal sealed partial class MlArtifactJsonContext : JsonSerializerContext;
