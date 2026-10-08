using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;

namespace CryptoTrading.Persistence;

/// <summary>Durable record of a drift observation and its diagnostic evaluation.</summary>
public sealed record DriftObservationRecord(
    Guid ObservationId,
    string StrategyId,
    DriftType Type,
    decimal ExpectedValue,
    decimal ObservedValue,
    decimal AllowedDeviationThreshold,
    decimal AbsoluteDeviation,
    decimal RelativeRate,
    DriftSeverity Severity,
    bool RequiresQuarantine,
    string Message,
    DateTimeOffset ObservedAtUtc);

/// <summary>Durable store for point-in-time market regime classifications.</summary>
public interface IMarketRegimeStore
{
    Task RecordMarketRegimeAsync(MarketRegimeSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<MarketRegimeSnapshot?> GetLatestMarketRegimeAsync(Symbol symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketRegimeSnapshot>> GetMarketRegimeHistoryAsync(Symbol symbol, int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>Durable store and lifecycle manager for model strategy records.</summary>
public interface IModelStrategyRegistryStore : IModelStrategyRegistry
{
    Task RegisterModelAsync(StrategyModelRecord record, CancellationToken cancellationToken = default);
    Task<StrategyModelRecord?> GetModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task<StrategyModelRecord?> GetActiveModelAsync(string strategyId, CancellationToken cancellationToken = default);
    Task<StrategyModelRecord> TransitionStatusAsync(string modelId, ModelLifecycleStatus targetStatus, string actor, string justification, DateTimeOffset transitionAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StrategyModelRecord>> ListModelsAsync(string strategyId, CancellationToken cancellationToken = default);
}

/// <summary>Durable store for multi-drift observations.</summary>
public interface IDriftObservationStore
{
    Task RecordDriftObservationAsync(DriftObservation observation, DriftEvaluationResult evaluation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriftObservationRecord>> GetRecentDriftsAsync(string? strategyId = null, int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>Durable store for normalized realized returns and contextual bandit weights across regimes.</summary>
public interface IStrategyRegimePerformanceStore
{
    Task RecordStrategyRewardAsync(string strategyId, MarketRegime regime, decimal reward, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StrategyRegimePerformance>> GetPerformancesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable normalized-return attribution from a realized USDT fill to its committed
/// decision. Rewards require a known within-tolerance quote-asset fee comparison.
/// </summary>
public interface IExecutionLearningRewardStore
{
    Task<ExecutionLearningRewardResult> RecordExecutionRewardAsync(string executionId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of attempting to learn from one immutable execution fill; reward is realized P&amp;L divided by fill notional.</summary>
public sealed record ExecutionLearningRewardResult(
    bool Recorded,
    bool Replayed,
    string? SkippedReason,
    string? StrategyId = null,
    MarketRegime? Regime = null,
    decimal? Reward = null);
