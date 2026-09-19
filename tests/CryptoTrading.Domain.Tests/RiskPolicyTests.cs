using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class RiskPolicyTests
{
    private static readonly RiskPolicy Policy = new(
        version: "risk-test-1",
        maxOrderNotional: 100m,
        maxExposureNotional: 250m,
        maxDailyLoss: 25m,
        maxDrawdown: 50m,
        maxMarketDataAge: TimeSpan.FromSeconds(5));

    [Fact]
    public void ApprovesTargetWithinAllLimitsAndCarriesPolicyVersion()
    {
        var decision = RiskEvaluator.Evaluate(Policy, Request(requested: 80m));

        Assert.Equal(RiskDecisionStatus.Approved, decision.Status);
        Assert.Equal(80m, decision.AuthorizedTargetNotional);
        Assert.Equal("risk-test-1", decision.PolicyVersion);
        Assert.Equal([RiskReasonCodes.TargetWithinLimits], decision.ReasonCodes);
    }

    [Fact]
    public void CapsTargetAtTheSmallestHardLimitAndNeverIncreasesIt()
    {
        var decision = RiskEvaluator.Evaluate(Policy, Request(requested: 140m));

        Assert.Equal(RiskDecisionStatus.Adjusted, decision.Status);
        Assert.Equal(100m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.TargetCappedByNotional, decision.ReasonCodes);
        Assert.True(decision.AuthorizedTargetNotional <= decision.RequestedTargetNotional);
    }

    [Fact]
    public void DailyLossAndDrawdownRejectNewExposureButAllowReduction()
    {
        var blocked = RiskEvaluator.Evaluate(Policy, Request(requested: 80m, current: 20m, dailyLoss: 25m));
        var reduction = RiskEvaluator.Evaluate(Policy, Request(requested: 10m, current: 20m, dailyLoss: 25m));

        Assert.Equal(RiskDecisionStatus.Rejected, blocked.Status);
        Assert.Contains(RiskReasonCodes.DailyLossLimit, blocked.ReasonCodes);
        Assert.Equal(RiskDecisionStatus.Adjusted, reduction.Status);
        Assert.Equal(10m, reduction.AuthorizedTargetNotional);

        var drawdown = RiskEvaluator.Evaluate(Policy, Request(requested: 80m, current: 20m, drawdown: 50m));
        Assert.Equal(RiskDecisionStatus.Rejected, drawdown.Status);
        Assert.Contains(RiskReasonCodes.DrawdownLimit, drawdown.ReasonCodes);
    }

    [Fact]
    public void StaleMarketDataRejectsEntry()
    {
        var decision = RiskEvaluator.Evaluate(Policy, Request(requested: 10m, age: TimeSpan.FromSeconds(6)));

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Equal(0m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.MarketDataStale, decision.ReasonCodes);
    }

    [Fact]
    public void FutureMarketDataRejectsEntryFailClosed()
    {
        var evaluatedAt = DateTimeOffset.UtcNow;
        var request = new RiskEvaluationRequest(
            10m,
            0m,
            0m,
            0m,
            evaluatedAt.AddMilliseconds(1),
            evaluatedAt);

        var decision = RiskEvaluator.Evaluate(Policy, request);

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Equal(0m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.MarketDataStale, decision.ReasonCodes);
    }

    [Fact]
    public void EmergencyStopAndWithdrawalsAreEmergencyBlocked()
    {
        var emergency = RiskEvaluator.Evaluate(Policy, Request(requested: 10m, emergency: true));
        var withdrawal = RiskEvaluator.Evaluate(Policy, Request(requested: 0m, withdrawal: true));

        Assert.Equal(RiskDecisionStatus.EmergencyBlocked, emergency.Status);
        Assert.Equal(RiskReasonCodes.EmergencyStop, emergency.ReasonCodes.Single());
        Assert.Equal(RiskDecisionStatus.EmergencyBlocked, withdrawal.Status);
        Assert.Equal(RiskReasonCodes.WithdrawalsDisabled, withdrawal.ReasonCodes.Single());
    }

    [Fact]
    public void PolicyRejectsInvalidLimitsAndRequestRejectsNonUtcTimestamps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskPolicy("x", 0m, 1m, 1m, 1m, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(requested: -1m));
        Assert.Throws<ArgumentException>(() => new RiskEvaluationRequest(1m, 0m, 0m, 0m,
            DateTimeOffset.Now, DateTimeOffset.UtcNow));
    }

    private static RiskEvaluationRequest Request(
        decimal requested,
        decimal current = 0m,
        decimal dailyLoss = 0m,
        decimal drawdown = 0m,
        TimeSpan? age = null,
        bool emergency = false,
        bool withdrawal = false)
    {
        var evaluatedAt = DateTimeOffset.UtcNow;
        return new(
            requested,
            current,
            dailyLoss,
            drawdown,
            evaluatedAt - (age ?? TimeSpan.FromSeconds(1)),
            evaluatedAt,
            emergency,
            withdrawal);
    }
}
