using System.Globalization;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Portfolio;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class MultiStrategyOperationsTests
{
    [Fact]
    public void AdaptiveExecutionTacticScorerSelectsAppropriateTacticBasedOnUrgencyAndSpread()
    {
        // 1. High urgency (0.85) -> Aggressive taker immediate
        var highUrgency = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.85m,
            topOfBookSpreadBps: 2.0m,
            expectedSlippageBps: 1.5m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m);

        Assert.Equal(ExecutionTactic.AggressiveTakerImmediate, highUrgency.SelectedTactic);
        Assert.True(highUrgency.RequiresImmediateFill);
        Assert.Contains("TAKER_EXECUTION", highUrgency.Reason, StringComparison.Ordinal);

        // 2. Patient signal with tight spread (0.20 urgency, 3 bps spread) -> Passive maker pegged
        var patientPegged = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.20m,
            topOfBookSpreadBps: 3.0m,
            expectedSlippageBps: 0.5m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m);

        Assert.Equal(ExecutionTactic.PassiveMakerPegged, patientPegged.SelectedTactic);
        Assert.False(patientPegged.RequiresImmediateFill);
        Assert.Contains("MAKER_PEGGED", patientPegged.Reason, StringComparison.Ordinal);

        // 3. Very patient with wide spread (0.10 urgency, 15 bps spread) -> Deep maker
        var patientDeep = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.10m,
            topOfBookSpreadBps: 15.0m,
            expectedSlippageBps: 0.0m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m);

        Assert.Equal(ExecutionTactic.PassiveMakerDeep, patientDeep.SelectedTactic);
        Assert.Contains("MAKER_DEEP", patientDeep.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveExecutionTacticScorerCalibratesPegOffsetAndImbalance()
    {
        // 1. Moderate urgency (0.72) promoted to Taker Immediate by high bid imbalance (+0.50)
        var promotedToTaker = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.72m,
            topOfBookSpreadBps: 4.0m,
            expectedSlippageBps: 1.0m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m,
            orderBookImbalance: 0.50m);

        Assert.Equal(ExecutionTactic.AggressiveTakerImmediate, promotedToTaker.SelectedTactic);
        Assert.True(promotedToTaker.RequiresImmediateFill);
        Assert.Equal(0.77m, promotedToTaker.UrgencyScore);

        // 2. Tight spread (3.5 bps) with high volatility (25 bps) applies 1-tick peg offset for queue priority
        var offsetPegged = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.20m,
            topOfBookSpreadBps: 3.5m,
            expectedSlippageBps: 0.5m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m,
            orderBookImbalance: 0.0m,
            volatilityBps: 25.0m);

        Assert.Equal(ExecutionTactic.PassiveMakerPegged, offsetPegged.SelectedTactic);
        Assert.Equal(1, offsetPegged.PegOffsetTicks);

        // 3. Low volatility and neutral imbalance keeps 0-tick peg offset at touch
        var touchPegged = AdaptiveExecutionTacticScorer.SelectTactic(
            signalUrgencyScore: 0.20m,
            topOfBookSpreadBps: 2.0m,
            expectedSlippageBps: 0.5m,
            makerFeeRate: 0.0005m,
            takerFeeRate: 0.001m,
            orderBookImbalance: 0.0m,
            volatilityBps: 10.0m);

        Assert.Equal(ExecutionTactic.PassiveMakerPegged, touchPegged.SelectedTactic);
        Assert.Equal(0, touchPegged.PegOffsetTicks);
    }

    [Fact]
    public void MultiStrategyComparativeHealthEnforcesPortfolioEligibilityAndQuarantine()
    {
        var now = DateTimeOffset.Parse("2026-09-21T18:00:00Z", CultureInfo.InvariantCulture);

        var healthySnapshot = new StrategyHealthSnapshot(
            StrategyHealthStatus.Healthy,
            [],
            now);

        var blockedSnapshot = new StrategyHealthSnapshot(
            StrategyHealthStatus.Blocked,
            ["RECONCILIATION_MISMATCH"],
            now);

        var inputs = new List<(string, StrategyHealthSnapshot, decimal)>
        {
            ("trend-strat", healthySnapshot, 1.8m),
            ("breakout-strat", healthySnapshot, 1.2m),
            ("reversion-strat", blockedSnapshot, 0.5m), // Blocked by health
            ("grid-strat", healthySnapshot, -0.3m),      // Negative Sharpe
        };

        var assessment = MultiStrategyComparativeHealth.EvaluatePortfolio(inputs);

        Assert.True(assessment.HasAnyEligibleStrategy);
        Assert.Equal(2, assessment.EligibleStrategies.Count);
        Assert.Equal(2, assessment.QuarantinedStrategies.Count);

        Assert.Contains(assessment.EligibleStrategies, s => s.StrategyId == "trend-strat");
        Assert.Contains(assessment.EligibleStrategies, s => s.StrategyId == "breakout-strat");
        Assert.Contains(assessment.QuarantinedStrategies, s => s.StrategyId == "reversion-strat");
        Assert.Contains(assessment.QuarantinedStrategies, s => s.StrategyId == "grid-strat");
    }

    [Fact]
    public void MultiStrategyPortfolioRebalancerEnforcesCapsAndSuppressesHysteresisChurn()
    {
        var totalCapital = 100_000m;

        // Inputs:
        // Strategy A: Current $40,000, Proposed 42% ($42,000) -> Delta $2,000 (5% drift)
        // Strategy B: Current $30,000, Proposed 30.5% ($30,500) -> Delta $500 (1.6% drift -> within band)
        // Strategy C: Current $10,000, Proposed 70% (exceeds cap of 60% -> capped to $60,000)
        var inputs = new List<(string, decimal, decimal)>
        {
            ("strat-a", 40_000m, 0.42m),
            ("strat-b", 30_000m, 0.305m),
            ("strat-c", 10_000m, 0.70m),
        };

        var plan = MultiStrategyPortfolioRebalancer.PlanRebalance(
            inputs,
            totalCapital,
            hysteresisThreshold: 0.04m, // 4%
            maxStrategyCap: 0.60m);     // 60% cap

        Assert.True(plan.RebalancingNeeded);
        Assert.Equal(3, plan.Instructions.Count);

        var instrA = plan.Instructions.First(i => i.StrategyId == "strat-a");
        var instrB = plan.Instructions.First(i => i.StrategyId == "strat-b");
        var instrC = plan.Instructions.First(i => i.StrategyId == "strat-c");

        // Strat A: 5% change > 4% -> Rebalance triggered
        Assert.True(instrA.RebalanceRequired);
        Assert.Equal(42_000m, instrA.TargetNotional);

        // Strat B: 1.6% change < 4% -> Within band, suppressed
        Assert.False(instrB.RebalanceRequired);

        // Strat C: Proposed 70% was clamped to 60% ($60,000)
        Assert.Equal(60_000m, instrC.TargetNotional);
        Assert.True(instrC.RebalanceRequired);
    }
}
