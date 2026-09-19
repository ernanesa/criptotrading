using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class MarketRiskTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingSnapshotIsFailClosedWhenMarketLimitsAreConfigured()
    {
        var policy = Policy(maxSlippageBps: 25m);
        var request = Request(100m);

        var decision = RiskEvaluator.Evaluate(policy, request);

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Equal(0m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.MarketRiskDataUnavailable, decision.ReasonCodes);
    }

    [Fact]
    public void ExcessiveSlippageBlocksNewExposureButAllowsReduction()
    {
        var policy = Policy(maxSlippageBps: 25m);
        var snapshot = new MarketRiskSnapshot(30m, 10m, 10_000m, Now);

        var opening = RiskEvaluator.Evaluate(policy, Request(100m, currentExposure: 20m, marketRisk: snapshot));
        var reduction = RiskEvaluator.Evaluate(policy, Request(20m, currentExposure: 100m, marketRisk: snapshot));

        Assert.Equal(RiskDecisionStatus.Rejected, opening.Status);
        Assert.Contains(RiskReasonCodes.SlippageLimit, opening.ReasonCodes);
        Assert.Equal(RiskDecisionStatus.Adjusted, reduction.Status);
        Assert.Equal(20m, reduction.AuthorizedTargetNotional);
    }

    [Fact]
    public void VolatilityLimitIsFailClosed()
    {
        var decision = RiskEvaluator.Evaluate(
            Policy(maxVolatilityBps: 100m),
            Request(100m, marketRisk: new MarketRiskSnapshot(5m, 101m, 10_000m, Now)));

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Contains(RiskReasonCodes.VolatilityLimit, decision.ReasonCodes);
    }

    [Fact]
    public void LiquidityCapsAuthorizedTargetAndRecordsReason()
    {
        var decision = RiskEvaluator.Evaluate(
            Policy(minLiquidityNotional: 100m),
            Request(500m, marketRisk: new MarketRiskSnapshot(5m, 10m, 250m, Now)));

        Assert.Equal(RiskDecisionStatus.Adjusted, decision.Status);
        Assert.Equal(250m, decision.AuthorizedTargetNotional);
        Assert.Contains(RiskReasonCodes.LiquidityLimit, decision.ReasonCodes);
    }

    [Fact]
    public void BelowMinimumLiquidityRejectsNewExposure()
    {
        var decision = RiskEvaluator.Evaluate(
            Policy(minLiquidityNotional: 100m),
            Request(50m, marketRisk: new MarketRiskSnapshot(5m, 10m, 99m, Now)));

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Contains(RiskReasonCodes.LiquidityLimit, decision.ReasonCodes);
    }

    [Fact]
    public void IncompleteSnapshotIsFailClosed()
    {
        var decision = RiskEvaluator.Evaluate(
            Policy(maxVolatilityBps: 100m),
            Request(100m, marketRisk: new MarketRiskSnapshot(5m, 10m, 10_000m, Now, isComplete: false)));

        Assert.Equal(RiskDecisionStatus.Rejected, decision.Status);
        Assert.Contains(RiskReasonCodes.MarketRiskDataUnavailable, decision.ReasonCodes);
    }

    [Fact]
    public void CalculatorDerivesSlippageVolatilityAndLiquidityFromLocalInputs()
    {
        var candles = new[]
        {
            new MarketCandle(Now.AddMinutes(-2), 100m),
            new MarketCandle(Now.AddMinutes(-1), 101m),
            new MarketCandle(Now, 100m)
        };
        var snapshot = MarketRiskCalculator.Calculate(
            candles,
            new[] { new MarketBookLevel(99m, 2m) },
            new[] { new MarketBookLevel(101m, 3m) },
            MarketRiskSide.Buy,
            Now);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(303m, snapshot.AvailableLiquidityNotional);
        Assert.Equal(100m, snapshot.ExpectedSlippageBps);
        Assert.InRange(snapshot.VolatilityBps, 99.5m, 99.51m);
    }

    [Fact]
    public void CalculatorReturnsIncompleteSnapshotWhenBookOrCandlesAreInsufficient()
    {
        var snapshot = MarketRiskCalculator.Calculate(
            new[] { new MarketCandle(Now, 100m) },
            Array.Empty<MarketBookLevel>(),
            Array.Empty<MarketBookLevel>(),
            MarketRiskSide.Sell,
            Now);

        Assert.False(snapshot.IsComplete);
    }

    private static RiskPolicy Policy(
        decimal? maxSlippageBps = null,
        decimal? maxVolatilityBps = null,
        decimal? minLiquidityNotional = null) => new(
        "risk-market-v1",
        1_000m,
        2_000m,
        500m,
        500m,
        TimeSpan.FromMinutes(1),
        maxSlippageBps: maxSlippageBps,
        maxVolatilityBps: maxVolatilityBps,
        minLiquidityNotional: minLiquidityNotional);

    private static RiskEvaluationRequest Request(
        decimal target,
        decimal currentExposure = 0m,
        MarketRiskSnapshot? marketRisk = null) => new(
        target,
        currentExposure,
        0m,
        0m,
        Now,
        Now,
        marketRisk: marketRisk);
}
