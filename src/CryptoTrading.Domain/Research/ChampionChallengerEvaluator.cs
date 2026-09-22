namespace CryptoTrading.Domain.Research;

/// <summary>Summary metrics for strategy performance in backtest or paper evaluation window.</summary>
public sealed record StrategyPerformanceMetrics(
    string StrategyId,
    decimal NetReturnRate,
    decimal SharpeRatio,
    decimal MaxDrawdownRate,
    int TotalTradesCount,
    decimal WinRate);

/// <summary>Result of statistical gate evaluation between champion and challenger models.</summary>
public sealed record ChampionChallengerEvaluationResult(
    string ChampionStrategyId,
    string ChallengerStrategyId,
    bool PromotionApproved,
    decimal SharpeImprovementDelta,
    decimal DrawdownDelta,
    string DecisionReason);

/// <summary>
/// Automated champion-challenger gate evaluator (US-175).
/// Enforces strict quantitative criteria before recommending a challenger model promotion.
/// </summary>
public static class ChampionChallengerEvaluator
{
    public const decimal DefaultMinSharpeImprovement = 0.15m;
    public const decimal DefaultMaxDrawdownInflationRatio = 1.10m; // Max 10% drawdown increase
    public const int DefaultMinTradesRequired = 20;

    public static ChampionChallengerEvaluationResult Evaluate(
        StrategyPerformanceMetrics champion,
        StrategyPerformanceMetrics challenger,
        decimal minSharpeImprovement = DefaultMinSharpeImprovement,
        decimal maxDrawdownInflationRatio = DefaultMaxDrawdownInflationRatio,
        int minTradesRequired = DefaultMinTradesRequired)
    {
        ArgumentNullException.ThrowIfNull(champion);
        ArgumentNullException.ThrowIfNull(challenger);

        var sharpeDelta = challenger.SharpeRatio - champion.SharpeRatio;
        var ddDelta = challenger.MaxDrawdownRate - champion.MaxDrawdownRate;

        // Gate 1: Minimum sample size
        if (challenger.TotalTradesCount < minTradesRequired)
        {
            return new ChampionChallengerEvaluationResult(
                champion.StrategyId,
                challenger.StrategyId,
                PromotionApproved: false,
                SharpeImprovementDelta: sharpeDelta,
                DrawdownDelta: ddDelta,
                DecisionReason: $"REJECTED: Challenger trade count ({challenger.TotalTradesCount}) is below required minimum ({minTradesRequired}).");
        }

        // Gate 2: Sharpe improvement threshold
        if (sharpeDelta < minSharpeImprovement)
        {
            return new ChampionChallengerEvaluationResult(
                champion.StrategyId,
                challenger.StrategyId,
                PromotionApproved: false,
                SharpeImprovementDelta: sharpeDelta,
                DrawdownDelta: ddDelta,
                DecisionReason: $"REJECTED: Sharpe delta ({sharpeDelta:F2}) does not achieve required improvement ({minSharpeImprovement:F2}).");
        }

        // Gate 3: Drawdown tolerance check
        var maxAllowedDrawdown = champion.MaxDrawdownRate * maxDrawdownInflationRatio;
        if (challenger.MaxDrawdownRate > maxAllowedDrawdown)
        {
            return new ChampionChallengerEvaluationResult(
                champion.StrategyId,
                challenger.StrategyId,
                PromotionApproved: false,
                SharpeImprovementDelta: sharpeDelta,
                DrawdownDelta: ddDelta,
                DecisionReason: $"REJECTED: Challenger max drawdown ({challenger.MaxDrawdownRate:P2}) exceeds tolerance cap ({maxAllowedDrawdown:P2}).");
        }

        // Gate 4: Positive net return required
        if (challenger.NetReturnRate <= 0m)
        {
            return new ChampionChallengerEvaluationResult(
                champion.StrategyId,
                challenger.StrategyId,
                PromotionApproved: false,
                SharpeImprovementDelta: sharpeDelta,
                DrawdownDelta: ddDelta,
                DecisionReason: $"REJECTED: Challenger net return rate ({challenger.NetReturnRate:P2}) must be strictly positive.");
        }

        return new ChampionChallengerEvaluationResult(
            champion.StrategyId,
            challenger.StrategyId,
            PromotionApproved: true,
            SharpeImprovementDelta: Math.Round(sharpeDelta, 4),
            DrawdownDelta: Math.Round(ddDelta, 4),
            DecisionReason: $"PROMOTION_APPROVED: Challenger exceeded champion Sharpe by {sharpeDelta:F2} while satisfying risk and drawdown gates.");
    }
}
