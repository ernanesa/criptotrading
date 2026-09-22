namespace CryptoTrading.Domain.Research;

/// <summary>Summary of slow learning loop evaluation and registration lifecycle event.</summary>
public sealed record LearningLoopRunResult(
    string StrategyFamily,
    string? PromotedModelId,
    bool PromotionOccurred,
    ChampionChallengerEvaluationResult GateEvaluation,
    string StatusSummary);

/// <summary>
/// Slow learning loop orchestrator (US-177).
/// Safely runs scheduled or drift-triggered model evaluation DAG, enforcing champion-challenger promotion gates.
/// </summary>
public sealed class SlowLearningLoopOrchestrator
{
    private readonly IModelStrategyRegistry _registry;

    public SlowLearningLoopOrchestrator(IModelStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public LearningLoopRunResult ExecuteCycle(
        string strategyFamily,
        StrategyModelRecord challengerModelRecord,
        StrategyPerformanceMetrics championMetrics,
        StrategyPerformanceMetrics challengerMetrics,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyFamily);
        ArgumentNullException.ThrowIfNull(challengerModelRecord);
        ArgumentNullException.ThrowIfNull(championMetrics);
        ArgumentNullException.ThrowIfNull(challengerMetrics);

        // 1. Evaluate Champion vs Challenger
        var gateResult = ChampionChallengerEvaluator.Evaluate(championMetrics, challengerMetrics);

        // 2. Register challenger as candidate first
        _registry.RegisterModel(challengerModelRecord with { Status = ModelLifecycleStatus.Candidate });

        if (gateResult.PromotionApproved)
        {
            // 3. Promote candidate to active (registry automatically retires previous active model)
            _registry.TransitionStatus(
                challengerModelRecord.ModelId,
                ModelLifecycleStatus.Active,
                actor: "SlowLearningLoopOrchestrator",
                justification: gateResult.DecisionReason,
                transitionAtUtc: evaluatedAtUtc);

            return new LearningLoopRunResult(
                strategyFamily,
                challengerModelRecord.ModelId,
                PromotionOccurred: true,
                gateResult,
                StatusSummary: $"PROMOTED: Candidate {challengerModelRecord.ModelId} promoted to Active. Previous champion retired.");
        }

        // 4. Reject and quarantine
        _registry.TransitionStatus(
            challengerModelRecord.ModelId,
            ModelLifecycleStatus.Quarantined,
            actor: "SlowLearningLoopOrchestrator",
            justification: gateResult.DecisionReason,
            transitionAtUtc: evaluatedAtUtc);

        return new LearningLoopRunResult(
            strategyFamily,
            PromotedModelId: null,
            PromotionOccurred: false,
            gateResult,
            StatusSummary: $"QUARANTINED: Candidate {challengerModelRecord.ModelId} failed champion-challenger gate.");
    }
}
