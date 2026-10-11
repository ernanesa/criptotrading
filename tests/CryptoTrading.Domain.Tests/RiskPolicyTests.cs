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
    public void IncompleteRiskContextRejectsNewExposureFailClosed()
    {
        var decision = RiskEvaluator.Evaluate(
            Policy,
            Request(requested: 10m, riskContextComplete: false));

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Equal(0m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.RiskContextUnavailable, decision.ReasonCodes);
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
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(-3)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DefaultBrl100BudgetPolicyComputesEffectiveCeilingWithFxHaircutAndHardCap()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var budget = BudgetPolicy.ApprovedMicroLiveDefault;

        // At USDTBRL ask = 5.50 -> 100 / 5.50 * 0.98 = 17.81818181 USDT (< 20 USDT cap)
        var fxQuoteNormal = new UsdtBrlFxQuote(5.50m, now.AddSeconds(-10));
        var valuationNormal = budget.CalculateEffectiveCeiling(fxQuoteNormal, now);
        Assert.True(valuationNormal.IsValid);
        Assert.Equal(17.81818181m, valuationNormal.EffectiveBudgetCeilingUsdt);

        // At USDTBRL ask = 4.50 -> 100 / 4.50 * 0.98 = 21.77 USDT -> capped at 20.00 USDT hard cap
        var fxQuoteStrongBrl = new UsdtBrlFxQuote(4.50m, now.AddSeconds(-5));
        var valuationCapped = budget.CalculateEffectiveCeiling(fxQuoteStrongBrl, now);
        Assert.True(valuationCapped.IsValid);
        Assert.Equal(20.00m, valuationCapped.EffectiveBudgetCeilingUsdt);
    }

    [Fact]
    public void DefaultBrl100BudgetPolicyFailsClosedWhenUsdtBrlQuoteIsMissingOrStale()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var budget = BudgetPolicy.ApprovedMicroLiveDefault;

        var missing = budget.CalculateEffectiveCeiling(null, now);
        Assert.False(missing.IsValid);
        Assert.Equal(RiskReasonCodes.FxQuoteUnavailable, missing.ReasonCode);

        var staleQuote = new UsdtBrlFxQuote(5.50m, now.AddSeconds(-61));
        var stale = budget.CalculateEffectiveCeiling(staleQuote, now);
        Assert.False(stale.IsValid);
        Assert.Equal(RiskReasonCodes.FxQuoteStale, stale.ReasonCode);
    }

    [Fact]
    public void DefaultBrl100BudgetPolicyEnforcesCommittedCapitalAndStickyLockedUnknownOrders()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var budget = BudgetPolicy.ApprovedMicroLiveDefault;
        var fxQuote = new UsdtBrlFxQuote(4.90m, now.AddSeconds(-5)); // 20.00 USDT ceiling

        // Within ceiling: 10 USDT candidate, 0 open, 0 pending, 0.05 USDT stop risk
        var allowed = budget.EvaluateCommitment(
            new BudgetCommitmentRequest(
                requestedOrderNotionalUsdt: 10m,
                openPositionsNotionalUsdt: 0m,
                openPositionsCount: 0,
                pendingOrdersReservedUsdt: 0m,
                unknownOrdersLockedUsdt: 0m,
                dailyRealizedLossUsdt: 0m,
                cumulativeNetRealizedLossUsdt: 0m,
                estimatedStopRiskUsdt: 0.05m,
                evaluatedAtUtc: now,
                fxQuote: fxQuote));
        Assert.True(allowed.IsApproved);

        // Exceeds ceiling when open + pending + candidate + fees > 20 USDT
        var overBudget = budget.EvaluateCommitment(
            new BudgetCommitmentRequest(
                requestedOrderNotionalUsdt: 5m,
                openPositionsNotionalUsdt: 0m,
                openPositionsCount: 0,
                pendingOrdersReservedUsdt: 16m,
                unknownOrdersLockedUsdt: 0m,
                dailyRealizedLossUsdt: 0m,
                cumulativeNetRealizedLossUsdt: 0m,
                estimatedStopRiskUsdt: 0.05m,
                evaluatedAtUtc: now,
                fxQuote: fxQuote));
        Assert.False(overBudget.IsApproved);
        Assert.Equal(RiskReasonCodes.BudgetCeilingExceeded, overBudget.ReasonCode);

        // Any LockedUnknown order blocks new exposure immediately
        var lockedUnknownBlocked = budget.EvaluateCommitment(
            new BudgetCommitmentRequest(
                requestedOrderNotionalUsdt: 5m,
                openPositionsNotionalUsdt: 0m,
                openPositionsCount: 0,
                pendingOrdersReservedUsdt: 0m,
                unknownOrdersLockedUsdt: 5m,
                dailyRealizedLossUsdt: 0m,
                cumulativeNetRealizedLossUsdt: 0m,
                estimatedStopRiskUsdt: 0.05m,
                evaluatedAtUtc: now,
                fxQuote: fxQuote));
        Assert.False(lockedUnknownBlocked.IsApproved);
        Assert.Equal(RiskReasonCodes.LockedUnknownCapitalActive, lockedUnknownBlocked.ReasonCode);
    }

    private static RiskEvaluationRequest Request(
        decimal requested,
        decimal current = 0m,
        decimal dailyLoss = 0m,
        decimal drawdown = 0m,
        TimeSpan? age = null,
        bool emergency = false,
        bool withdrawal = false,
        bool riskContextComplete = true)
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
            withdrawal,
            riskContextComplete: riskContextComplete);
    }
}
