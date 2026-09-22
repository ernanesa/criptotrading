using System.Globalization;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Runtime;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ContinuousLearningAndHighAvailabilityTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void AdaptiveSlippageModelComputesDynamicExecutionImpactAndEnforcesFeasibility()
    {
        // 1. Small order, deep book: low slippage, feasible
        var smallEstimate = AdaptiveSlippageModel.EstimateSlippage(
            orderNotional: 1_000m,
            availableDepthNotional: 100_000m,
            volatilityRate: 0.01m);

        Assert.True(smallEstimate.IsFeasible);
        Assert.True(smallEstimate.ExpectedSlippageBps > 2.0m);
        Assert.True(smallEstimate.ExpectedSlippageBps < 10.0m);

        // 2. Large order, shallow book: high slippage, unfeasible
        var largeEstimate = AdaptiveSlippageModel.EstimateSlippage(
            orderNotional: 80_000m,
            availableDepthNotional: 50_000m,
            volatilityRate: 0.05m);

        Assert.False(largeEstimate.IsFeasible);
        Assert.Contains("SLIPPAGE_EXCEEDED", largeEstimate.Reason, StringComparison.Ordinal);
        Assert.True(largeEstimate.ExpectedSlippageBps > 20.0m);

        // 3. Zero depth: fail-closed unfeasible
        var zeroDepthEstimate = AdaptiveSlippageModel.EstimateSlippage(
            orderNotional: 1_000m,
            availableDepthNotional: 0m,
            volatilityRate: 0.01m);

        Assert.False(zeroDepthEstimate.IsFeasible);
        Assert.Contains("AVAILABLE_DEPTH_EMPTY", zeroDepthEstimate.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextualMetaAllocatorUpdatesWeightsAndFailsClosedInHighChop()
    {
        var allocator = new ContextualMetaAllocator();
        var strategies = new[] { "trend", "reversion", "grid" };

        // Record high reward for 'trend' in BullTrend
        allocator.RecordReward("trend", MarketRegime.BullTrend, 0.50m);
        allocator.RecordReward("reversion", MarketRegime.BullTrend, -0.20m);

        var weights = allocator.ComputeWeights(MarketRegime.BullTrend, strategies, totalCapital: 10_000m);

        Assert.Equal(3, weights.Count);
        var trendWeight = weights.First(w => w.StrategyId == "trend");
        var reversionWeight = weights.First(w => w.StrategyId == "reversion");

        Assert.True(trendWeight.Weight > reversionWeight.Weight);
        Assert.True(trendWeight.AllocatedCapital > reversionWeight.AllocatedCapital);

        // HighVolatilityChop: zero allocation to all strategies
        var chopWeights = allocator.ComputeWeights(MarketRegime.HighVolatilityChop, strategies, totalCapital: 10_000m);
        Assert.All(chopWeights, w => Assert.Equal(0m, w.Weight));
        Assert.All(chopWeights, w => Assert.Equal(0m, w.AllocatedCapital));
    }

    [Fact]
    public void RegimeChallengerClassifierEvaluatesMultiFeatureMarketConditions()
    {
        var now = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

        // Insufficient bars returns IlliquidOrStale
        var insufficientSnapshot = RegimeChallengerClassifier.Classify(
            BtcUsdt,
            bars: [],
            observedAtUtc: now);
        Assert.Equal(MarketRegime.IlliquidOrStale, insufficientSnapshot.Regime);

        // Bull bars with positive volume flow
        var bullBars = new List<MarketBarFeature>();
        for (var i = 0; i < 15; i++)
        {
            var p = 60_000m + (i * 200m);
            bullBars.Add(new MarketBarFeature(
                now.AddMinutes(i),
                Open: p - 50m,
                High: p + 100m,
                Low: p - 60m,
                Close: p + 80m,
                Volume: 50m));
        }

        var bullSnapshot = RegimeChallengerClassifier.Classify(BtcUsdt, bullBars, now);
        Assert.Equal(MarketRegime.BullTrend, bullSnapshot.Regime);
        Assert.Contains("CHALLENGER_BULL", bullSnapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChampionChallengerEvaluatorEnforcesSharpeAndDrawdownPromotionGates()
    {
        var champion = new StrategyPerformanceMetrics(
            StrategyId: "champion-v1",
            NetReturnRate: 0.12m,
            SharpeRatio: 1.50m,
            MaxDrawdownRate: 0.08m,
            TotalTradesCount: 50,
            WinRate: 0.55m);

        // Challenger with insufficient trade count
        var smallSampleChallenger = new StrategyPerformanceMetrics(
            StrategyId: "challenger-small",
            NetReturnRate: 0.20m,
            SharpeRatio: 2.20m,
            MaxDrawdownRate: 0.05m,
            TotalTradesCount: 10,
            WinRate: 0.70m);

        var sampleResult = ChampionChallengerEvaluator.Evaluate(champion, smallSampleChallenger);
        Assert.False(sampleResult.PromotionApproved);
        Assert.Contains("below required minimum", sampleResult.DecisionReason, StringComparison.Ordinal);

        // Challenger with excessive drawdown inflation
        var highDrawdownChallenger = new StrategyPerformanceMetrics(
            StrategyId: "challenger-high-dd",
            NetReturnRate: 0.25m,
            SharpeRatio: 1.80m,
            MaxDrawdownRate: 0.15m, // Exceeds 0.08 * 1.10 = 0.088
            TotalTradesCount: 40,
            WinRate: 0.58m);

        var ddResult = ChampionChallengerEvaluator.Evaluate(champion, highDrawdownChallenger);
        Assert.False(ddResult.PromotionApproved);
        Assert.Contains("exceeds tolerance cap", ddResult.DecisionReason, StringComparison.Ordinal);

        // Valid qualifying challenger
        var winningChallenger = new StrategyPerformanceMetrics(
            StrategyId: "challenger-valid",
            NetReturnRate: 0.18m,
            SharpeRatio: 1.85m, // +0.35 Sharpe
            MaxDrawdownRate: 0.07m,
            TotalTradesCount: 45,
            WinRate: 0.60m);

        var validResult = ChampionChallengerEvaluator.Evaluate(champion, winningChallenger);
        Assert.True(validResult.PromotionApproved);
        Assert.Contains("PROMOTION_APPROVED", validResult.DecisionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedParameterAdaptorRestrictsStepChangesAndAbsoluteRange()
    {
        var envelope = new ParameterEnvelope(
            ParameterName: "LookbackWindow",
            MinValue: 10m,
            MaxValue: 50m,
            MaxStepChangeRate: 0.20m); // Max 20% shift per step

        // Attempting a huge leap from 20 to 45 should be constrained by step rate
        var adaptedStep = BoundedParameterAdaptor.Adapt(currentValue: 20m, proposedValue: 45m, envelope);
        Assert.True(adaptedStep.IsAccepted);
        Assert.Equal(24m, adaptedStep.AdaptedValue); // 20 + 20*0.20 = 24
        Assert.Contains("STEP_CONSTRAINED", adaptedStep.Reason, StringComparison.Ordinal);

        // Attempting to exceed max bound
        var adaptedExceed = BoundedParameterAdaptor.Adapt(currentValue: 48m, proposedValue: 90m, envelope);
        Assert.True(adaptedExceed.IsAccepted);
        Assert.True(adaptedExceed.AdaptedValue <= 50m);
    }

    [Fact]
    public void SlowLearningLoopOrchestratorPromotesQualifyingChallengerToActive()
    {
        var registry = new InMemoryModelStrategyRegistry();
        var orchestrator = new SlowLearningLoopOrchestrator(registry);
        var now = DateTimeOffset.Parse("2026-09-21T00:00:00Z", CultureInfo.InvariantCulture);

        var initialChampion = new StrategyModelRecord(
            modelId: "model-champ-v1",
            strategyId: "trend-following",
            version: "1.0.0",
            codeCommitSha: "a1b2c3d4e5f67890",
            datasetHash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            configurationFingerprint: "{\"window\": 20}",
            status: ModelLifecycleStatus.Active,
            registeredAtUtc: DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture),
            registeredBy: "researcher-1");
        registry.RegisterModel(initialChampion);

        var challengerRecord = new StrategyModelRecord(
            modelId: "model-challenger-v2",
            strategyId: "trend-following",
            version: "2.0.0",
            codeCommitSha: "b2c3d4e5f67890a1",
            datasetHash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            configurationFingerprint: "{\"window\": 22}",
            status: ModelLifecycleStatus.Draft,
            registeredAtUtc: now,
            registeredBy: "researcher-2");

        var champMetrics = new StrategyPerformanceMetrics("model-champ-v1", 0.10m, 1.2m, 0.09m, 40, 0.52m);
        var challengerMetrics = new StrategyPerformanceMetrics("model-challenger-v2", 0.18m, 1.6m, 0.08m, 40, 0.58m);

        var loopResult = orchestrator.ExecuteCycle(
            "trend-following",
            challengerRecord,
            champMetrics,
            challengerMetrics,
            now);

        Assert.True(loopResult.PromotionOccurred);
        Assert.Equal("model-challenger-v2", loopResult.PromotedModelId);

        // Registry status assertions: challenger is Active, initial champion is Retired
        var activeModel = registry.GetActiveModel("trend-following");
        Assert.NotNull(activeModel);
        Assert.Equal("model-challenger-v2", activeModel.ModelId);

        var oldModel = registry.GetModel("model-champ-v1");
        Assert.NotNull(oldModel);
        Assert.Equal(ModelLifecycleStatus.Retired, oldModel.Status);
    }

    [Fact]
    public void FencingTokenCoordinatorEnforcesStrictMonotonicEpochAndRejectsStaleTokens()
    {
        var coordinator = new FencingTokenCoordinator();
        var now = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

        var tokenNode1 = coordinator.Acquire("worker-node-1", TimeSpan.FromMinutes(5), now);
        Assert.Equal(1, tokenNode1.Epoch);
        Assert.Equal("worker-node-1", tokenNode1.LeaderNodeId);

        // Valid execution with current token
        var validResult = coordinator.ValidateExecution(tokenNode1, now.AddMinutes(1));
        Assert.True(validResult.IsValid);

        // Node 2 acquires leadership (failover) -> epoch increments to 2
        var tokenNode2 = coordinator.Acquire("worker-node-2", TimeSpan.FromMinutes(5), now.AddMinutes(2));
        Assert.Equal(2, tokenNode2.Epoch);
        Assert.Equal("worker-node-2", tokenNode2.LeaderNodeId);

        // Node 1 attempts execution with stale token -> rejected to prevent double trading
        var staleResult = coordinator.ValidateExecution(tokenNode1, now.AddMinutes(3));
        Assert.False(staleResult.IsValid);
        Assert.Contains("STALE_EPOCH", staleResult.Reason, StringComparison.Ordinal);

        // Node 2 attempts execution after lease expiration -> rejected
        var expiredResult = coordinator.ValidateExecution(tokenNode2, now.AddMinutes(10));
        Assert.False(expiredResult.IsValid);
        Assert.Contains("TOKEN_EXPIRED", expiredResult.Reason, StringComparison.Ordinal);
    }
}
