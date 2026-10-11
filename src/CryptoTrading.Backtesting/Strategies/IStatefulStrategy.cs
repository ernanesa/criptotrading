using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>Discrete execution outcome for strategy feedback loops.</summary>
public enum StrategyExecutionOutcome
{
    NoTrade,
    Filled,
    PartiallyFilled,
    RejectedMinimumNotional,
    RejectedFilterViolation,
    RejectedInsufficientLiquidity,
    RejectedRiskCap,
}

/// <summary>
/// Feedback sent back to a stateful strategy following order execution simulation.
/// Guarantees that internal strategy state reflects actual execution rather than optimistic assumptions.
/// </summary>
public sealed record StrategyExecutionFeedback(
    int DecisionBarIndex,
    int FillBarIndex,
    DateTimeOffset TimestampUtc,
    StrategyExecutionAction Action,
    StrategyExecutionOutcome Outcome,
    decimal RequestedQuantity,
    decimal ExecutedQuantity,
    decimal ExecutedPrice,
    decimal FeePaid,
    string? ReasonCode = null);

/// <summary>
/// Serializable point-in-time snapshot of strategy state for persistence and disaster recovery.
/// </summary>
public sealed record StrategyStateSnapshot(
    DateTimeOffset TimestampUtc,
    decimal CurrentInventory,
    int ActiveLevel,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Contract for strategies that track execution feedback and state transitions deterministically.
/// </summary>
public interface IStatefulStrategy
{
    StrategyExecutionSignal GenerateSignal(CloseBar bar, int barIndex);
    void ApplyExecutionFeedback(StrategyExecutionFeedback feedback);
    StrategyStateSnapshot CaptureState();
    void RestoreState(StrategyStateSnapshot snapshot);
}

