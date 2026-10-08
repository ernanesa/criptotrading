using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CryptoTrading.Configuration;

/// <summary>
/// Validates an externally signed production promotion manifest. This is a pure
/// contract check: it does not authorize orders or enable a LIVE runtime. LIVE
/// runtime support remains unsupported and must continue to fail closed.
/// </summary>
public static partial class LivePromotionContractValidator
{
    public const string RequiredEnvironment = "PRODUCTION";
    public const string SignatureAlgorithm = "RSA-PSS-SHA256";
    private const int MaximumManifestBytes = 64 * 1024;

    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "manifestVersion", "environment", "artifactSha256", "riskPolicySha256", "configurationSha256",
        "notBeforeUtc", "expiresAtUtc", "symbol", "minimumCapital", "maximumCapital", "owner", "approver",
        "forkAuthorization",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Verifies the signature over the exact supplied JSON bytes, then checks
    /// all manifest claims against the independently supplied promotion context.
    /// Only RSA-PSS with SHA-256 is accepted.
    /// </summary>
    public static LivePromotionValidationResult Validate(
        ReadOnlyMemory<byte> exactManifestJsonBytes,
        ReadOnlyMemory<byte> signature,
        string externalPublicKeyPem,
        LivePromotionValidationContext expected)
    {
        ArgumentNullException.ThrowIfNull(externalPublicKeyPem);
        ArgumentNullException.ThrowIfNull(expected);

        if (exactManifestJsonBytes.IsEmpty || exactManifestJsonBytes.Length > MaximumManifestBytes)
        {
            return LivePromotionValidationResult.Reject("MANIFEST_SIZE_INVALID");
        }

        if (signature.IsEmpty || string.IsNullOrWhiteSpace(externalPublicKeyPem))
        {
            return LivePromotionValidationResult.Reject("SIGNATURE_INPUT_MISSING");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(externalPublicKeyPem);
            if (!rsa.VerifyData(
                    exactManifestJsonBytes.Span,
                    signature.Span,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                return LivePromotionValidationResult.Reject("SIGNATURE_INVALID");
            }
        }
        catch (CryptographicException)
        {
            return LivePromotionValidationResult.Reject("SIGNATURE_INVALID");
        }
        catch (ArgumentException)
        {
            return LivePromotionValidationResult.Reject("SIGNATURE_INVALID");
        }

        if (!TryReadManifest(exactManifestJsonBytes, out var manifest, out var parseFailure))
        {
            return LivePromotionValidationResult.Reject(parseFailure);
        }

        if (manifest.ManifestVersion != 1 ||
            !string.Equals(manifest.Environment, RequiredEnvironment, StringComparison.Ordinal))
        {
            return LivePromotionValidationResult.Reject("ENVIRONMENT_OR_VERSION_INVALID");
        }

        if (!IsSha256(expected.ArtifactSha256) || !IsSha256(expected.RiskPolicySha256) || !IsSha256(expected.ConfigurationSha256) ||
            !MatchesSha256(manifest.ArtifactSha256, expected.ArtifactSha256) ||
            !MatchesSha256(manifest.RiskPolicySha256, expected.RiskPolicySha256) ||
            !MatchesSha256(manifest.ConfigurationSha256, expected.ConfigurationSha256))
        {
            return LivePromotionValidationResult.Reject("ARTIFACT_RISK_OR_CONFIGURATION_HASH_MISMATCH");
        }

        if (!IsValidSymbol(expected.Symbol) || !IsValidSymbol(manifest.Symbol) ||
            !string.Equals(manifest.Symbol, expected.Symbol, StringComparison.Ordinal))
        {
            return LivePromotionValidationResult.Reject("SYMBOL_MISMATCH");
        }

        if (expected.NowUtc.Offset != TimeSpan.Zero || expected.MaximumPermittedCapital <= 0m ||
            expected.RequestedCapital <= 0m || manifest.NotBeforeUtc.Offset != TimeSpan.Zero ||
            manifest.ExpiresAtUtc.Offset != TimeSpan.Zero || manifest.NotBeforeUtc >= manifest.ExpiresAtUtc ||
            manifest.MinimumCapital < 0m || manifest.MaximumCapital <= 0m ||
            manifest.MinimumCapital > manifest.MaximumCapital ||
            manifest.MaximumCapital > expected.MaximumPermittedCapital ||
            expected.RequestedCapital < manifest.MinimumCapital || expected.RequestedCapital > manifest.MaximumCapital)
        {
            return LivePromotionValidationResult.Reject("CAPITAL_OR_TIME_BOUNDS_INVALID");
        }

        if (expected.NowUtc < manifest.NotBeforeUtc || expected.NowUtc >= manifest.ExpiresAtUtc)
        {
            return LivePromotionValidationResult.Reject("MANIFEST_OUTSIDE_VALIDITY_WINDOW");
        }

        if (string.IsNullOrWhiteSpace(manifest.Owner) || string.IsNullOrWhiteSpace(manifest.Approver) ||
            string.Equals(manifest.Owner.Trim(), manifest.Approver.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return LivePromotionValidationResult.Reject("OWNER_APPROVER_INVALID");
        }

        if (manifest.ForkAuthorization)
        {
            return LivePromotionValidationResult.Reject("FORK_AUTHORIZATION_UNSUPPORTED");
        }

        return LivePromotionValidationResult.Accept();
    }

    private static bool TryReadManifest(
        ReadOnlyMemory<byte> json,
        out LivePromotionManifest manifest,
        out string failureCode)
    {
        manifest = default!;
        failureCode = "MANIFEST_JSON_INVALID";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    failureCode = "MANIFEST_FIELDS_INVALID";
                    return false;
                }
            }

            if (!RequiredProperties.SetEquals(seen))
            {
                failureCode = "MANIFEST_FIELDS_INVALID";
                return false;
            }

            manifest = JsonSerializer.Deserialize<LivePromotionManifest>(json.Span, JsonOptions)!;
            return manifest is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool MatchesSha256(string? actual, string expected)
    {
        if (!IsSha256(actual) || !IsSha256(expected))
        {
            return false;
        }

        var actualBytes = Convert.FromHexString(actual!);
        var expectedBytes = Convert.FromHexString(expected);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static bool IsValidSymbol(string? symbol) =>
        symbol is not null && SymbolPattern().IsMatch(symbol);

    [GeneratedRegex("^[A-Z0-9_]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}

/// <summary>Signed claims. The signature is verified over the exact JSON byte sequence.</summary>
public sealed record LivePromotionManifest(
    int ManifestVersion,
    string Environment,
    string ArtifactSha256,
    string RiskPolicySha256,
    string ConfigurationSha256,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string Symbol,
    decimal MinimumCapital,
    decimal MaximumCapital,
    string Owner,
    string Approver,
    bool ForkAuthorization);

/// <summary>Independent values expected by the verifier at promotion time.</summary>
public sealed record LivePromotionValidationContext(
    string ArtifactSha256,
    string RiskPolicySha256,
    string ConfigurationSha256,
    string Symbol,
    decimal RequestedCapital,
    decimal MaximumPermittedCapital,
    DateTimeOffset NowUtc);

public sealed record LivePromotionValidationResult(bool IsValid, string? FailureCode)
{
    internal static LivePromotionValidationResult Accept() => new(true, null);

    internal static LivePromotionValidationResult Reject(string failureCode) => new(false, failureCode);
}
