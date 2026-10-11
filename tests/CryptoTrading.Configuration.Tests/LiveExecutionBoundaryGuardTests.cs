using CryptoTrading.Configuration;
using Xunit;

namespace CryptoTrading.Configuration.Tests;

public sealed class LiveExecutionBoundaryGuardTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ValidRiskPolicySha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void DefaultBoundaryRemainsNoGoLive()
    {
        var guard = new LiveExecutionBoundaryGuard();

        Assert.False(guard.IsLiveEnabled);
        Assert.False(guard.IsRevoked);

        var ticket = CreateTicket(TimeSpan.FromSeconds(30));
        var decision = guard.AuthorizeAction(
            ticket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);

        Assert.False(decision.IsAuthorized);
        Assert.Equal("LIVE_GATE_DISABLED_DEFAULT_NO_TRADE", decision.ReasonCode);
    }

    [Fact]
    public void SegregatedCredentialPathsEnforceStrictSeparationFromTestnet()
    {
        // 1. Missing paths
        var missing = LiveExecutionBoundaryGuard.ValidateCredentialPaths("", "");
        Assert.False(missing.IsValid);
        Assert.Equal("LIVE_CREDENTIAL_PATH_MISSING", missing.ReasonCode);

        // 2. Reusing testnet key path
        var reusedKey = LiveExecutionBoundaryGuard.ValidateCredentialPaths(
            "/secrets/testnet.key", "/secrets/live.secret",
            testnetKeyPath: "/secrets/testnet.key", testnetSecretPath: "/secrets/testnet.secret");
        Assert.False(reusedKey.IsValid);
        Assert.Equal("CREDENTIAL_REUSE_TESTNET_KEY", reusedKey.ReasonCode);

        // 3. Reusing testnet secret path
        var reusedSecret = LiveExecutionBoundaryGuard.ValidateCredentialPaths(
            "/secrets/live.key", "/secrets/testnet.secret",
            testnetKeyPath: "/secrets/testnet.key", testnetSecretPath: "/secrets/testnet.secret");
        Assert.False(reusedSecret.IsValid);
        Assert.Equal("CREDENTIAL_REUSE_TESTNET_SECRET", reusedSecret.ReasonCode);

        // 4. Live baseUrl pointing to testnet host
        var testnetHost = LiveExecutionBoundaryGuard.ValidateCredentialPaths(
            "/secrets/live.key", "/secrets/live.secret",
            liveBaseUrl: "https://testnet.binance.vision");
        Assert.False(testnetHost.IsValid);
        Assert.Equal("LIVE_HOST_POINTS_TO_TESTNET", testnetHost.ReasonCode);

        // 5. Clean segregated paths
        var clean = LiveExecutionBoundaryGuard.ValidateCredentialPaths(
            "/secrets/live.key", "/secrets/live.secret",
            testnetKeyPath: "/secrets/testnet.key", testnetSecretPath: "/secrets/testnet.secret",
            liveBaseUrl: "https://api.binance.com");
        Assert.True(clean.IsValid);
        Assert.Equal("CREDENTIALS_SEGREGATED", clean.ReasonCode);
    }

    [Fact]
    public void TicketWithTtlExceeding60SecondsIsRejectedAndTriggersStickyRevocation()
    {
        var guard = new LiveExecutionBoundaryGuard(isLiveEnabled: true);
        var longTtlTicket = CreateTicket(TimeSpan.FromSeconds(65)); // > 60s TTL

        var decision = guard.AuthorizeAction(
            longTtlTicket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);

        Assert.False(decision.IsAuthorized);
        Assert.Equal("TICKET_TTL_EXCEEDS_MAX_60_SECONDS", decision.ReasonCode);
        Assert.True(guard.IsRevoked);
        Assert.Equal("TICKET_TTL_EXCEEDED_MAX_60S", guard.RevocationReason);
    }

    [Fact]
    public void SingleUseReplayProtectionLatchesStickyRevocationOnReplay()
    {
        var guard = new LiveExecutionBoundaryGuard(isLiveEnabled: true);
        var ticket = CreateTicket(TimeSpan.FromSeconds(30), ticketId: "unique-ticket-1");

        // First use authorized
        var first = guard.AuthorizeAction(
            ticket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);
        Assert.True(first.IsAuthorized);

        // Replay attempt fails and permanently revokes guard
        var second = guard.AuthorizeAction(
            ticket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);
        Assert.False(second.IsAuthorized);
        Assert.Equal("LIVE_TICKET_ALREADY_USED", second.ReasonCode);
        Assert.True(guard.IsRevoked);

        // Subsequent requests are permanently blocked by sticky revocation
        var thirdTicket = CreateTicket(TimeSpan.FromSeconds(30), ticketId: "unique-ticket-2");
        var third = guard.AuthorizeAction(
            thirdTicket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);
        Assert.False(third.IsAuthorized);
        Assert.StartsWith("LIVE_BOUNDARY_REVOKED", third.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitShaAndRiskPolicySha256BindingIsEnforced()
    {
        var guard = new LiveExecutionBoundaryGuard(isLiveEnabled: true);

        // 1. Commit SHA mismatch
        var badCommitTicket = CreateTicket(TimeSpan.FromSeconds(30), commitSha: "differentcommit0123456789abcdef01234567");
        var badCommitDecision = guard.AuthorizeAction(
            badCommitTicket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);
        Assert.False(badCommitDecision.IsAuthorized);
        Assert.Equal("COMMIT_SHA_MISMATCH", badCommitDecision.ReasonCode);

        // 2. RiskPolicy SHA mismatch
        var badPolicyTicket = CreateTicket(TimeSpan.FromSeconds(30), ticketId: "ticket-test-2", riskPolicySha256: "badbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadb");
        var badPolicyDecision = guard.AuthorizeAction(
            badPolicyTicket, "SubmitOrder", "BTCUSDT", "ord-1", ValidCommitSha, ValidRiskPolicySha256, 10m, NowUtc);
        Assert.False(badPolicyDecision.IsAuthorized);
        Assert.Equal("RISK_POLICY_SHA256_MISMATCH", badPolicyDecision.ReasonCode);
    }

    private static LiveActionAuthorizationTicket CreateTicket(
        TimeSpan validityWindow,
        string ticketId = "ticket-test-1",
        string actionType = "SubmitOrder",
        string symbol = "BTCUSDT",
        string? commitSha = ValidCommitSha,
        string? riskPolicySha256 = ValidRiskPolicySha256,
        string clientOrderId = "ord-1") =>
        new(
            TicketId: ticketId,
            PromotionManifestSha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            Symbol: symbol,
            ActionType: actionType,
            MaxOrderNotionalUsdt: 20.00m,
            NotBeforeUtc: NowUtc,
            ExpiresAtUtc: NowUtc.Add(validityWindow),
            OperatorId: "Operator-Ernane",
            ApproverId: "Approver-RiskOfficer",
            CommitSha: commitSha,
            RiskPolicySha256: riskPolicySha256,
            ClientOrderId: clientOrderId);
}
