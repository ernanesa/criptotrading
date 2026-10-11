using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace CryptoTrading.Configuration.Tests;

public sealed class LivePromotionContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string ArtifactHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RiskHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ConfigurationHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AcceptsExternallySignedExactManifestWhenAllClaimsMatch()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest());

        var result = Validate(signed, rsa, CreateContext());

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public void RejectsExpectedHashMismatch()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest());
        var context = CreateContext() with { ConfigurationSha256 = ArtifactHash };

        var result = Validate(signed, rsa, context);

        Assert.False(result.IsValid);
        Assert.Equal("ARTIFACT_RISK_OR_CONFIGURATION_HASH_MISMATCH", result.FailureCode);
    }

    [Fact]
    public void RejectsManifestByteTamperingAfterSignature()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest());
        var tampered = signed.Json.ToArray();
        var index = Array.IndexOf(tampered, (byte)'B');
        Assert.True(index >= 0);
        tampered[index] = (byte)'C';

        var result = LivePromotionContractValidator.Validate(
            tampered,
            signed.Signature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            CreateContext());

        Assert.False(result.IsValid);
        Assert.Equal("SIGNATURE_INVALID", result.FailureCode);
    }

    [Fact]
    public void RejectsExpiredManifest()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest() with { ExpiresAtUtc = Now });

        var result = Validate(signed, rsa, CreateContext());

        Assert.False(result.IsValid);
        Assert.Equal("MANIFEST_OUTSIDE_VALIDITY_WINDOW", result.FailureCode);
    }

    [Fact]
    public void RejectsRequestedCapitalOutsideSignedBounds()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest() with { MaximumCapital = 500m });
        var context = CreateContext() with { RequestedCapital = 501m };

        var result = Validate(signed, rsa, context);

        Assert.False(result.IsValid);
        Assert.Equal("CAPITAL_OR_TIME_BOUNDS_INVALID", result.FailureCode);
    }

    [Fact]
    public void RejectsManifestWhoseMaximumExceedsIndependentCapitalLimit()
    {
        using var rsa = RSA.Create(2048);
        var signed = Sign(rsa, CreateManifest() with { MaximumCapital = 2_000m });
        var context = CreateContext() with { MaximumPermittedCapital = 1_000m };

        var result = Validate(signed, rsa, context);

        Assert.False(result.IsValid);
        Assert.Equal("CAPITAL_OR_TIME_BOUNDS_INVALID", result.FailureCode);
    }

    [Fact]
    public void RejectsSameOwnerAndApproverAndAnyForkAuthorization()
    {
        using var rsa = RSA.Create(2048);
        var sameOwner = Sign(rsa, CreateManifest() with { Approver = "strategy-owner" });
        var forkAuthorized = Sign(rsa, CreateManifest() with { ForkAuthorization = true });

        Assert.Equal("OWNER_APPROVER_INVALID", Validate(sameOwner, rsa, CreateContext()).FailureCode);
        Assert.Equal("FORK_AUTHORIZATION_UNSUPPORTED", Validate(forkAuthorized, rsa, CreateContext()).FailureCode);
    }

    [Fact]
    public void LiveExecutionBoundaryGuardDefaultsToNoTradeAndRejectsCredentialReuseOrUnsignedTickets()
    {
        using var rsa = RSA.Create(2048);
        var signedManifest = Sign(rsa, CreateManifest());
        var manifestValidation = Validate(signedManifest, rsa, CreateContext());
        var manifestHash = Convert.ToHexString(SHA256.HashData(signedManifest.Json));
        var validCredentials = new LiveExecutionCredentialBoundary(
            TargetEnvironment: "LIVE",
            LiveKeyFingerprintSha256: "1111111111111111111111111111111111111111111111111111111111111111",
            TestnetKeyFingerprintSha256: "2222222222222222222222222222222222222222222222222222222222222222",
            PaperKeyFingerprintSha256: "3333333333333333333333333333333333333333333333333333333333333333",
            WithdrawalsPermitted: false,
            MarginOrFuturesPermitted: false,
            IpWhitelistEnforced: true);
        var ticket = new LiveActionAuthorizationTicket(
            TicketId: "ticket-001",
            PromotionManifestSha256: manifestHash,
            Symbol: "BTCUSDT",
            ActionType: "SPOT_ORDER_SUBMIT",
            MaxOrderNotionalUsdt: 15m,
            NotBeforeUtc: Now.AddMinutes(-1),
            ExpiresAtUtc: Now.AddMinutes(5),
            OperatorId: "Ernane",
            ApproverId: "independent-approver");
        var ticketBytes = JsonSerializer.SerializeToUtf8Bytes(ticket, JsonOptions);
        var ticketSignature = rsa.SignData(ticketBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        // 1. Default gate disabled -> NO_TRADE
        var defaultDecision = LiveExecutionBoundaryGuard.Evaluate(
            liveExecutionGateEnabled: false,
            manifestValidation,
            signedManifest.Json,
            validCredentials,
            ticketBytes,
            ticketSignature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "BTCUSDT",
            10m,
            20m,
            Now);
        Assert.False(defaultDecision.IsAuthorized);
        Assert.Equal("NO_TRADE", defaultDecision.Mode);
        Assert.Equal("LIVE_GATE_DISABLED_DEFAULT_NO_TRADE", defaultDecision.ReasonCode);

        // 2. Testnet credential reuse -> NO_TRADE
        var reusedTestnetCreds = validCredentials with
        {
            TestnetKeyFingerprintSha256 = validCredentials.LiveKeyFingerprintSha256,
        };
        var reusedDecision = LiveExecutionBoundaryGuard.Evaluate(
            liveExecutionGateEnabled: true,
            manifestValidation,
            signedManifest.Json,
            reusedTestnetCreds,
            ticketBytes,
            ticketSignature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "BTCUSDT",
            10m,
            20m,
            Now);
        Assert.False(reusedDecision.IsAuthorized);
        Assert.Equal("CREDENTIAL_ISOLATION_VIOLATION_TESTNET_REUSE", reusedDecision.ReasonCode);

        // 3. Valid isolated credentials + valid signed manifest + valid signed ticket within 20 USDT ceiling -> Authorized
        var authorized = LiveExecutionBoundaryGuard.Evaluate(
            liveExecutionGateEnabled: true,
            manifestValidation,
            signedManifest.Json,
            validCredentials,
            ticketBytes,
            ticketSignature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            "BTCUSDT",
            10m,
            20m,
            Now);
        Assert.True(authorized.IsAuthorized);
        Assert.Equal("LIVE_AUTHORIZED", authorized.Mode);
    }

    private static LivePromotionManifest CreateManifest() => new(
        ManifestVersion: 1,
        Environment: LivePromotionContractValidator.RequiredEnvironment,
        ArtifactSha256: ArtifactHash,
        RiskPolicySha256: RiskHash,
        ConfigurationSha256: ConfigurationHash,
        NotBeforeUtc: Now.AddMinutes(-1),
        ExpiresAtUtc: Now.AddMinutes(10),
        Symbol: "BTCUSDT",
        MinimumCapital: 100m,
        MaximumCapital: 750m,
        Owner: "strategy-owner",
        Approver: "independent-approver",
        ForkAuthorization: false);

    private static LivePromotionValidationContext CreateContext() => new(
        ArtifactHash,
        RiskHash,
        ConfigurationHash,
        "BTCUSDT",
        RequestedCapital: 500m,
        MaximumPermittedCapital: 1_000m,
        NowUtc: Now);

    private static SignedManifest Sign(RSA rsa, LivePromotionManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var signature = rsa.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new SignedManifest(json, signature);
    }

    private static LivePromotionValidationResult Validate(
        SignedManifest signed,
        RSA rsa,
        LivePromotionValidationContext context) =>
        LivePromotionContractValidator.Validate(signed.Json, signed.Signature, rsa.ExportSubjectPublicKeyInfoPem(), context);

    private sealed record SignedManifest(byte[] Json, byte[] Signature);
}
