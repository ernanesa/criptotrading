using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests.Risk;

public sealed class BudgetPolicyTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultBudgetPolicyMatchesApprovedOperatorParameters()
    {
        var policy = new BudgetPolicy();

        Assert.Equal("Ernane", policy.Owner);
        Assert.Equal(100.00m, policy.NominalBudgetBrl);
        Assert.Equal(20.00m, policy.AbsoluteHardCapUsdt);
        Assert.Equal(20.00m, policy.HardCapUsdt);
        Assert.Equal(0.02m, policy.FxHaircutRatio);
        Assert.Equal(0.02m, policy.FxSafetyHaircutRate);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.MaxFxQuoteAge);
        Assert.Equal(1, policy.MaxOpenPositions);
        Assert.Equal(2.00m, policy.MaxDailyLossBrl);
        Assert.Equal(5.00m, policy.MaxCumulativeDrawdownBrl);
        Assert.Equal(1.00m, policy.MaxRiskPerTradeBrl);
        Assert.Equal(0.0010m, policy.TakerFeeRatePerSide);
        Assert.Equal(0.0025m, policy.FeeBufferRate);
        Assert.Equal("BTCUSDT", policy.AllowedSymbol);
    }

    [Fact]
    public void UsdtBrlFxQuoteEvaluatesFreshnessAndConservativeCeiling()
    {
        var freshQuote = new UsdtBrlFxQuote(5.50m, NowUtc.AddSeconds(-15), isComplete: true);
        var staleQuote = new UsdtBrlFxQuote(5.50m, NowUtc.AddSeconds(-65), isComplete: true);

        Assert.True(freshQuote.IsFresh(NowUtc, TimeSpan.FromSeconds(60)));
        Assert.False(staleQuote.IsFresh(NowUtc, TimeSpan.FromSeconds(60)));

        // 100 / 5.50 * 0.98 = 17.81818181 USDT
        var conservativeUsdt = freshQuote.ConvertBrlToUsdtConservative(100.00m, 0.02m);
        Assert.Equal(17.81818181m, conservativeUsdt);

        var ceilingUnderCap = freshQuote.ComputeEffectiveCeilingUsdt(100.00m, 20.00m, 0.02m);
        Assert.Equal(17.81818181m, ceilingUnderCap);

        // At 4.00 BRL/USDT: 100 / 4.00 * 0.98 = 24.50 USDT -> hard-capped at 20.00 USDT
        var strongBrlQuote = new UsdtBrlFxQuote(4.00m, NowUtc);
        var ceilingAtHardCap = strongBrlQuote.ComputeEffectiveCeilingUsdt(100.00m, 20.00m, 0.02m);
        Assert.Equal(20.00m, ceilingAtHardCap);
    }

    [Fact]
    public void CalculateEffectiveCeilingFailsClosedOnMissingOrStaleQuote()
    {
        var policy = new BudgetPolicy();

        var missingResult = policy.CalculateEffectiveCeiling(null, NowUtc);
        Assert.False(missingResult.IsValid);
        Assert.Equal(RiskReasonCodes.FxQuoteUnavailable, missingResult.ReasonCode);

        var staleQuote = new UsdtBrlFxQuote(5.50m, NowUtc.AddSeconds(-61));
        var staleResult = policy.CalculateEffectiveCeiling(staleQuote, NowUtc);
        Assert.False(staleResult.IsValid);
        Assert.Equal(RiskReasonCodes.FxQuoteStale, staleResult.ReasonCode);
    }

    [Fact]
    public void EvaluateCommitmentEnforcesSinglePositionAndCapitalCeilings()
    {
        var policy = new BudgetPolicy();
        var quote = new UsdtBrlFxQuote(5.50m, NowUtc);

        // 1. Clean order within headroom approved
        var cleanRequest = new BudgetCommitmentRequest(
            requestedOrderNotionalUsdt: 10.00m,
            openPositionsNotionalUsdt: 0m,
            openPositionsCount: 0,
            pendingOrdersReservedUsdt: 0m,
            unknownOrdersLockedUsdt: 0m,
            dailyRealizedLossUsdt: 0m,
            cumulativeNetRealizedLossUsdt: 0m,
            estimatedStopRiskUsdt: 0.10m,
            evaluatedAtUtc: NowUtc,
            fxQuote: quote);

        var cleanDecision = policy.EvaluateCommitment(cleanRequest);
        Assert.True(cleanDecision.IsApproved);
        Assert.Equal(RiskReasonCodes.TargetWithinLimits, cleanDecision.ReasonCode);

        // 2. Already 1 open position -> blocked
        var secondPositionRequest = new BudgetCommitmentRequest(
            requestedOrderNotionalUsdt: 5.00m,
            openPositionsNotionalUsdt: 10.00m,
            openPositionsCount: 1,
            pendingOrdersReservedUsdt: 0m,
            unknownOrdersLockedUsdt: 0m,
            dailyRealizedLossUsdt: 0m,
            cumulativeNetRealizedLossUsdt: 0m,
            estimatedStopRiskUsdt: 0.10m,
            evaluatedAtUtc: NowUtc,
            fxQuote: quote);

        var secondPositionDecision = policy.EvaluateCommitment(secondPositionRequest);
        Assert.False(secondPositionDecision.IsApproved);
        Assert.Equal(RiskReasonCodes.MaxOpenPositionsExceeded, secondPositionDecision.ReasonCode);

        // 3. LockedUnknown orders present -> blocked
        var lockedUnknownRequest = new BudgetCommitmentRequest(
            requestedOrderNotionalUsdt: 5.00m,
            openPositionsNotionalUsdt: 0m,
            openPositionsCount: 0,
            pendingOrdersReservedUsdt: 0m,
            unknownOrdersLockedUsdt: 5.00m,
            dailyRealizedLossUsdt: 0m,
            cumulativeNetRealizedLossUsdt: 0m,
            estimatedStopRiskUsdt: 0.10m,
            evaluatedAtUtc: NowUtc,
            fxQuote: quote);

        var lockedUnknownDecision = policy.EvaluateCommitment(lockedUnknownRequest);
        Assert.False(lockedUnknownDecision.IsApproved);
        Assert.Equal(RiskReasonCodes.LockedUnknownCapitalActive, lockedUnknownDecision.ReasonCode);

        // 4. Position reduction (sell) always approved
        var reductionRequest = new BudgetCommitmentRequest(
            requestedOrderNotionalUsdt: 10.00m,
            openPositionsNotionalUsdt: 10.00m,
            openPositionsCount: 1,
            pendingOrdersReservedUsdt: 0m,
            unknownOrdersLockedUsdt: 5.00m,
            dailyRealizedLossUsdt: 10.00m,
            cumulativeNetRealizedLossUsdt: 10.00m,
            estimatedStopRiskUsdt: 0.10m,
            evaluatedAtUtc: NowUtc,
            fxQuote: null,
            isPositionReduction: true);

        var reductionDecision = policy.EvaluateCommitment(reductionRequest);
        Assert.True(reductionDecision.IsApproved);
    }

    [Fact]
    public void RiskPolicyDocumentHashIsBackwardCompatibleWithoutBudgetPolicy()
    {
        var baselinePolicy = new RiskPolicy(
            version: "v1",
            maxOrderNotional: 100m,
            maxExposureNotional: 250m,
            maxDailyLoss: 25m,
            maxDrawdown: 50m,
            maxMarketDataAge: TimeSpan.FromSeconds(5));

        var fromUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var hashWithoutBudget = RiskPolicyDocument.ComputeHash(baselinePolicy, fromUtc, toUtc);
        Assert.Equal(64, hashWithoutBudget.Length);

        var policyWithBudget = baselinePolicy with { BudgetPolicy = new BudgetPolicy() };
        var hashWithBudget = RiskPolicyDocument.ComputeHash(policyWithBudget, fromUtc, toUtc);

        Assert.NotEqual(hashWithoutBudget, hashWithBudget);
    }

    [Fact]
    public void IsSymbolAllowedMatchesExactCommaSeparatedAndWildcard()
    {
        var single = new BudgetPolicy(allowedSymbol: "BTCUSDT");
        Assert.True(single.IsSymbolAllowed("BTCUSDT"));
        Assert.True(single.IsSymbolAllowed("btcusdt"));
        Assert.False(single.IsSymbolAllowed("ETHUSDT"));
        Assert.False(single.IsSymbolAllowed(string.Empty));

        var multi = new BudgetPolicy(allowedSymbol: "BTCUSDT, ETHUSDT; SOLUSDT BNBUSDT");
        Assert.True(multi.IsSymbolAllowed("BTCUSDT"));
        Assert.True(multi.IsSymbolAllowed("ETHUSDT"));
        Assert.True(multi.IsSymbolAllowed("SOLUSDT"));
        Assert.True(multi.IsSymbolAllowed("BNBUSDT"));
        Assert.False(multi.IsSymbolAllowed("ADAUSDT"));

        var wildcard = new BudgetPolicy(allowedSymbol: "*");
        Assert.True(wildcard.IsSymbolAllowed("DOGEUSDT"));

        var mixedWildcard = new BudgetPolicy(allowedSymbol: "BTCUSDT, *");
        Assert.True(mixedWildcard.IsSymbolAllowed("ETHUSDT"));
        Assert.True(mixedWildcard.IsSymbolAllowed("SOLUSDT"));
    }
}
