using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class RiskPolicyDocumentTests
{
    private static readonly DateTimeOffset Effective = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = new(2030, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalHashIsStableAndApprovalIsSegregated()
    {
        var draft = RiskPolicyDocument.CreateDraft(Policy(), Effective, Expires, "author");

        var approved = draft.Approve("reviewer", "approval-1", Effective.AddHours(1));

        Assert.Equal(draft.DocumentHash, approved.DocumentHash);
        Assert.Equal(RiskPolicyLifecycle.Approved, approved.Lifecycle);
        var active = approved.Activate(Effective.AddHours(2));
        Assert.True(active.IsValidAt(Effective.AddHours(3)));
        Assert.Throws<InvalidOperationException>(() => approved.Approve("reviewer-2", "approval-2", Effective.AddHours(2)));
    }

    [Fact]
    public void CreatorCannotApproveAndExpiredPolicyCannotActivate()
    {
        var draft = RiskPolicyDocument.CreateDraft(Policy(), Effective, Expires, "author");

        Assert.Throws<ArgumentException>(() => draft.Approve("author", "approval-1", Effective.AddHours(1)));
        var approved = draft.Approve("reviewer", "approval-1", Effective.AddHours(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => approved.Activate(Expires));
    }

    [Fact]
    public void TamperedHashAndNonUtcTimestampsFailClosed()
    {
        var policy = Policy();
        Assert.Throws<ArgumentException>(() => new RiskPolicyDocument(
            policy, Effective, Expires, "author", new string('0', 64)));
        Assert.Throws<ArgumentException>(() => RiskPolicyDocument.CreateDraft(
            policy, Effective.ToOffset(TimeSpan.FromHours(-3)), Expires, "author"));
    }

    private static RiskPolicy Policy() => new(
        "risk-2030-01", 100m, 250m, 25m, 50m, TimeSpan.FromSeconds(5),
        withdrawalsEnabled: false, maxSlippageBps: 15m, maxVolatilityBps: 100m, minLiquidityNotional: 500m);
}
