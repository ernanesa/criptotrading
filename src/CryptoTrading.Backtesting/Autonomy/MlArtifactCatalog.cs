using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Backtesting.Autonomy;

public interface IMlDecisionArtifactProvider
{
    MlDecisionArtifact? Find(string symbol, string timeframe, OrderSide side, int horizonBars);
}

public sealed class MlArtifactCatalog : IMlDecisionArtifactProvider
{
    public const int MaximumPayloadBytes = 1_048_576;
    private const int MaximumModels = 64;
    private const string Schema = "ml-artifact-catalog-v1";
    private readonly FrozenDictionary<ModelKey, MlDecisionArtifact> models;
    private readonly record struct ModelKey(string Symbol, string Timeframe, OrderSide Side, int HorizonBars);

    private MlArtifactCatalog(IEnumerable<MlDecisionArtifact> artifacts)
    {
        Dictionary<ModelKey, MlDecisionArtifact> unique = [];
        foreach (var artifact in artifacts)
        {
            var policy = artifact.Metadata.Policy;
            if (unique.Count >= MaximumModels || !unique.TryAdd(new(policy.Symbol, policy.Timeframe, policy.Side, policy.HorizonBars), artifact))
                throw new InvalidDataException("ML_CATALOG_DUPLICATE_OR_EXCESSIVE_MODELS");
        }
        if (unique.Count == 0) throw new InvalidDataException("ML_CATALOG_EMPTY");
        models = unique.ToFrozenDictionary();
    }

    public int Count => models.Count;

    public MlDecisionArtifact? Find(string symbol, string timeframe, OrderSide side, int horizonBars) =>
        models.GetValueOrDefault(new ModelKey(symbol, timeframe, side, horizonBars));

    public static MlArtifactExport CreateBundle(IEnumerable<MlArtifactExport> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        List<MlDecisionArtifact> verified = [];
        List<BundledModel> bundled = [];
        decimal maximumError = 0m;
        foreach (var export in exports)
        {
            if (bundled.Count >= MaximumModels) throw new InvalidDataException("ML_CATALOG_EXCESSIVE_MODELS");
            verified.Add(MlDecisionArtifact.LoadVerified(export.Payload.Span, export.Sha256));
            bundled.Add(new(export.Sha256, export.Payload.ToArray()));
            maximumError = Math.Max(maximumError, export.MaximumScoreRoundTripError);
        }
        _ = new MlArtifactCatalog(verified);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CatalogPayload(Schema, bundled.ToArray()), MlCatalogJsonContext.Default.CatalogPayload);
        if (bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("ML_CATALOG_PAYLOAD_TOO_LARGE");
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)), maximumError);
    }

    public static MlArtifactCatalog LoadVerified(ReadOnlySpan<byte> payload, string expectedSha256)
    {
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes || expectedSha256 is null || expectedSha256.Length != 64)
            throw new InvalidDataException("ML_CATALOG_SIZE_OR_DIGEST_INVALID");
        var owned = payload.ToArray();
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException error) { throw new InvalidDataException("ML_CATALOG_DIGEST_INVALID", error); }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(owned), expected))
            throw new InvalidDataException("ML_CATALOG_HASH_MISMATCH");
        CatalogPayload? decoded;
        try { decoded = JsonSerializer.Deserialize(owned, MlCatalogJsonContext.Default.CatalogPayload); }
        catch (JsonException error) { throw new InvalidDataException("ML_CATALOG_JSON_INVALID", error); }
        if (decoded?.Schema != Schema || decoded.Models is not { Length: > 0 and <= MaximumModels })
            throw new InvalidDataException("ML_CATALOG_SCHEMA_OR_MODEL_COUNT_INVALID");
        return new MlArtifactCatalog(decoded.Models.Select(static model => model?.Payload is null
            ? throw new InvalidDataException("ML_CATALOG_MODEL_MISSING")
            : MlDecisionArtifact.LoadVerified(model.Payload, model.Sha256)));
    }
}

internal sealed record BundledModel([property: JsonRequired] string Sha256, [property: JsonRequired] byte[] Payload);
internal sealed record CatalogPayload([property: JsonRequired] string Schema, [property: JsonRequired] BundledModel[] Models);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogPayload))]
internal sealed partial class MlCatalogJsonContext : JsonSerializerContext;
