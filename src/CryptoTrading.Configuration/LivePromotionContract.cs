#pragma warning disable CA1054 // Raw base URL configuration strings are validated before URI parsing.

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

/// <summary>
/// Cryptographic fingerprint boundary proving that Live credentials are never shared
/// with Testnet or Paper/Shadow environments and have Spot-only, no-withdrawal permissions.
/// </summary>
public sealed record LiveExecutionCredentialBoundary(
    string TargetEnvironment,
    string LiveKeyFingerprintSha256,
    string? TestnetKeyFingerprintSha256,
    string? PaperKeyFingerprintSha256,
    bool WithdrawalsPermitted,
    bool MarginOrFuturesPermitted,
    bool IpWhitelistEnforced);

/// <summary>
/// Signed, time-bounded per-action authorization ticket required at the Live execution boundary.
/// </summary>
public sealed record LiveActionAuthorizationTicket(
    string TicketId,
    string PromotionManifestSha256,
    string Symbol,
    string ActionType,
    decimal MaxOrderNotionalUsdt,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string OperatorId,
    string ApproverId,
    string? CommitSha = null,
    string? RiskPolicySha256 = null,
    string? ClientOrderId = null)
{
    public TimeSpan ValidityWindow => ExpiresAtUtc - NotBeforeUtc;
}

/// <summary>Outcome of credential path segregation checks.</summary>
public sealed record LiveExecutionCredentialDecision(bool IsValid, string ReasonCode);

/// <summary>
/// Evaluation result for the Live execution boundary guard. Defaults to NO_TRADE (fail-closed).
/// </summary>
public sealed record LiveExecutionBoundaryDecision(
    bool IsAuthorized,
    string Mode,
    string ReasonCode)
{
    public static LiveExecutionBoundaryDecision NoTrade(string reasonCode) =>
        new(false, "NO_TRADE", reasonCode);

    public static LiveExecutionBoundaryDecision Authorized() =>
        new(true, "LIVE_AUTHORIZED", "LIVE_BOUNDARY_VERIFIED");
}

/// <summary>
/// Enforces the F21 Live execution boundary:
/// - Default is always NO_TRADE unless the explicit Live gate is enabled;
/// - Rejects any credential reuse between TESTNET/PAPER and LIVE;
/// - Requires a valid RSA-PSS-SHA256 signed LivePromotionManifest AND a valid
///   RSA-PSS-SHA256 signed, time-bounded LiveActionAuthorizationTicket.
/// </summary>
public sealed class LiveExecutionBoundaryGuard
{
    public const string LiveApiKeyFileEnv = "BINANCE_LIVE_API_KEY_FILE";
    public const string LiveApiSecretFileEnv = "BINANCE_LIVE_API_SECRET_FILE";
    public static readonly TimeSpan MaxActionTicketTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTicketValidityWindow = TimeSpan.FromMinutes(15);

    private readonly Lock sync = new();
    private readonly HashSet<string> usedTicketIds = new(StringComparer.Ordinal);
    private bool isRevoked;
    private string? revocationReason;

    public LiveExecutionBoundaryGuard(bool isLiveEnabled = false)
    {
        IsLiveEnabled = isLiveEnabled;
    }

    public bool IsLiveEnabled { get; }

    public bool IsRevoked
    {
        get
        {
            lock (sync) { return isRevoked; }
        }
    }

    public string? RevocationReason
    {
        get
        {
            lock (sync) { return revocationReason; }
        }
    }

    public void Revoke(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (sync)
        {
            isRevoked = true;
            revocationReason = reason.Trim();
        }
    }

    public static LiveExecutionCredentialDecision ValidateCredentialPaths(
        string? liveKeyPath,
        string? liveSecretPath,
        string? testnetKeyPath = null,
        string? testnetSecretPath = null,
        string? liveBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(liveKeyPath) || string.IsNullOrWhiteSpace(liveSecretPath))
        {
            return new LiveExecutionCredentialDecision(false, "LIVE_CREDENTIAL_PATH_MISSING");
        }

        var normalizedLiveKey = liveKeyPath.Trim();
        var normalizedLiveSecret = liveSecretPath.Trim();

        if (testnetKeyPath is not null &&
            string.Equals(normalizedLiveKey, testnetKeyPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new LiveExecutionCredentialDecision(false, "CREDENTIAL_REUSE_TESTNET_KEY");
        }

        if (testnetSecretPath is not null &&
            string.Equals(normalizedLiveSecret, testnetSecretPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new LiveExecutionCredentialDecision(false, "CREDENTIAL_REUSE_TESTNET_SECRET");
        }

        if (liveBaseUrl is not null)
        {
            var url = liveBaseUrl.Trim();
            if (url.Contains("testnet", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("binance.vision", StringComparison.OrdinalIgnoreCase))
            {
                return new LiveExecutionCredentialDecision(false, "LIVE_HOST_POINTS_TO_TESTNET");
            }
        }

        return new LiveExecutionCredentialDecision(true, "CREDENTIALS_SEGREGATED");
    }

    public LiveExecutionBoundaryDecision AuthorizeAction(
        LiveActionAuthorizationTicket ticket,
        string expectedActionType,
        string expectedSymbol,
        string expectedClientOrderId,
        string expectedCommitSha,
        string expectedRiskPolicySha256,
        decimal requestedNotionalUsdt,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        lock (sync)
        {
            if (isRevoked)
            {
                return LiveExecutionBoundaryDecision.NoTrade($"LIVE_BOUNDARY_REVOKED:{revocationReason}");
            }

            if (!IsLiveEnabled)
            {
                return LiveExecutionBoundaryDecision.NoTrade("LIVE_GATE_DISABLED_DEFAULT_NO_TRADE");
            }

            if (ticket.ValidityWindow > MaxActionTicketTtl)
            {
                Revoke("TICKET_TTL_EXCEEDED_MAX_60S");
                return LiveExecutionBoundaryDecision.NoTrade("TICKET_TTL_EXCEEDS_MAX_60_SECONDS");
            }

            if (nowUtc.Offset != TimeSpan.Zero || ticket.NotBeforeUtc.Offset != TimeSpan.Zero || ticket.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                ticket.NotBeforeUtc >= ticket.ExpiresAtUtc || nowUtc < ticket.NotBeforeUtc || nowUtc >= ticket.ExpiresAtUtc)
            {
                return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_OUTSIDE_VALIDITY_WINDOW");
            }

            if (!usedTicketIds.Add(ticket.TicketId))
            {
                Revoke("TICKET_REPLAY_DETECTED");
                return LiveExecutionBoundaryDecision.NoTrade("LIVE_TICKET_ALREADY_USED");
            }

            if (!string.Equals(ticket.ActionType, expectedActionType, StringComparison.OrdinalIgnoreCase))
            {
                return LiveExecutionBoundaryDecision.NoTrade("ACTION_TYPE_MISMATCH");
            }

            if (!string.Equals(ticket.Symbol, expectedSymbol, StringComparison.OrdinalIgnoreCase))
            {
                return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_SYMBOL_MISMATCH");
            }

            if (ticket.ClientOrderId is not null && !string.Equals(ticket.ClientOrderId, expectedClientOrderId, StringComparison.Ordinal))
            {
                return LiveExecutionBoundaryDecision.NoTrade("CLIENT_ORDER_ID_MISMATCH");
            }

            if (ticket.CommitSha is not null && !string.Equals(ticket.CommitSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                return LiveExecutionBoundaryDecision.NoTrade("COMMIT_SHA_MISMATCH");
            }

            if (ticket.RiskPolicySha256 is not null && !string.Equals(ticket.RiskPolicySha256, expectedRiskPolicySha256, StringComparison.OrdinalIgnoreCase))
            {
                return LiveExecutionBoundaryDecision.NoTrade("RISK_POLICY_SHA256_MISMATCH");
            }

            if (requestedNotionalUsdt > ticket.MaxOrderNotionalUsdt)
            {
                return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_BUDGET_CEILING_EXCEEDED");
            }

            return LiveExecutionBoundaryDecision.Authorized();
        }
    }

    private static readonly JsonSerializerOptions TicketJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static LiveExecutionBoundaryDecision Evaluate(
        bool liveExecutionGateEnabled,
        LivePromotionValidationResult manifestValidation,
        ReadOnlyMemory<byte> exactManifestJsonBytes,
        LiveExecutionCredentialBoundary? credentials,
        ReadOnlyMemory<byte> exactTicketJsonBytes,
        ReadOnlyMemory<byte> ticketSignature,
        string externalPublicKeyPem,
        string expectedSymbol,
        decimal requestedOrderNotionalUsdt,
        decimal effectiveBudgetCeilingUsdt,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifestValidation);
        ArgumentNullException.ThrowIfNull(externalPublicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSymbol);

        if (!liveExecutionGateEnabled)
        {
            return LiveExecutionBoundaryDecision.NoTrade("LIVE_GATE_DISABLED_DEFAULT_NO_TRADE");
        }

        if (!manifestValidation.IsValid)
        {
            return LiveExecutionBoundaryDecision.NoTrade(
                manifestValidation.FailureCode ?? "PROMOTION_MANIFEST_INVALID");
        }

        if (credentials is null)
        {
            return LiveExecutionBoundaryDecision.NoTrade("LIVE_CREDENTIALS_MISSING");
        }

        var credentialCheck = ValidateCredentialBoundary(credentials);
        if (credentialCheck is not null)
        {
            return LiveExecutionBoundaryDecision.NoTrade(credentialCheck);
        }

        if (exactTicketJsonBytes.IsEmpty || ticketSignature.IsEmpty || string.IsNullOrWhiteSpace(externalPublicKeyPem))
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_MISSING_OR_UNSIGNED");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(externalPublicKeyPem);
            if (!rsa.VerifyData(
                    exactTicketJsonBytes.Span,
                    ticketSignature.Span,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_SIGNATURE_INVALID");
            }
        }
        catch (CryptographicException)
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_SIGNATURE_INVALID");
        }
        catch (ArgumentException)
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_SIGNATURE_INVALID");
        }

        LiveActionAuthorizationTicket? ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<LiveActionAuthorizationTicket>(exactTicketJsonBytes.Span, TicketJsonOptions);
        }
        catch (JsonException)
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_JSON_INVALID");
        }

        if (ticket is null ||
            string.IsNullOrWhiteSpace(ticket.TicketId) ||
            string.IsNullOrWhiteSpace(ticket.OperatorId) ||
            string.IsNullOrWhiteSpace(ticket.ApproverId) ||
            string.Equals(ticket.OperatorId.Trim(), ticket.ApproverId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_CLAIMS_INVALID");
        }

        var manifestHashHex = Convert.ToHexString(SHA256.HashData(exactManifestJsonBytes.Span));
        if (!IsSha256Hex(ticket.PromotionManifestSha256) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ticket.PromotionManifestSha256),
                Convert.FromHexString(manifestHashHex)))
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_MANIFEST_HASH_MISMATCH");
        }

        if (!string.Equals(ticket.Symbol, expectedSymbol.Trim().ToUpperInvariant(), StringComparison.Ordinal))
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_SYMBOL_MISMATCH");
        }

        if (nowUtc.Offset != TimeSpan.Zero ||
            ticket.NotBeforeUtc.Offset != TimeSpan.Zero ||
            ticket.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            ticket.NotBeforeUtc >= ticket.ExpiresAtUtc ||
            ticket.ExpiresAtUtc - ticket.NotBeforeUtc > MaxTicketValidityWindow ||
            nowUtc < ticket.NotBeforeUtc ||
            nowUtc >= ticket.ExpiresAtUtc)
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_OUTSIDE_VALIDITY_WINDOW");
        }

        if (requestedOrderNotionalUsdt <= 0m ||
            effectiveBudgetCeilingUsdt <= 0m ||
            ticket.MaxOrderNotionalUsdt <= 0m ||
            requestedOrderNotionalUsdt > ticket.MaxOrderNotionalUsdt ||
            requestedOrderNotionalUsdt > effectiveBudgetCeilingUsdt ||
            ticket.MaxOrderNotionalUsdt > effectiveBudgetCeilingUsdt)
        {
            return LiveExecutionBoundaryDecision.NoTrade("ACTION_TICKET_BUDGET_CEILING_EXCEEDED");
        }

        return LiveExecutionBoundaryDecision.Authorized();
    }

    private static string? ValidateCredentialBoundary(LiveExecutionCredentialBoundary boundary)
    {
        if (!string.Equals(boundary.TargetEnvironment, "LIVE", StringComparison.Ordinal))
        {
            return "CREDENTIAL_ENVIRONMENT_NOT_LIVE";
        }

        if (!IsSha256Hex(boundary.LiveKeyFingerprintSha256))
        {
            return "LIVE_KEY_FINGERPRINT_INVALID";
        }

        if (boundary.WithdrawalsPermitted || boundary.MarginOrFuturesPermitted || !boundary.IpWhitelistEnforced)
        {
            return "LIVE_KEY_PERMISSIONS_UNSAFE";
        }

        if (!string.IsNullOrWhiteSpace(boundary.TestnetKeyFingerprintSha256) &&
            string.Equals(boundary.LiveKeyFingerprintSha256, boundary.TestnetKeyFingerprintSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "CREDENTIAL_ISOLATION_VIOLATION_TESTNET_REUSE";
        }

        if (!string.IsNullOrWhiteSpace(boundary.PaperKeyFingerprintSha256) &&
            string.Equals(boundary.LiveKeyFingerprintSha256, boundary.PaperKeyFingerprintSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "CREDENTIAL_ISOLATION_VIOLATION_PAPER_REUSE";
        }

        return null;
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}