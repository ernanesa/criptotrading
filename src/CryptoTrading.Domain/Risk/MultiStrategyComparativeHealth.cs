namespace CryptoTrading.Domain.Risk;

/// <summary>Eligibility assessment for an individual strategy in multi-strategy portfolio coordination (US-077).</summary>
public sealed record StrategyEligibility(
    string StrategyId,
    bool IsEligible,
    StrategyHealthStatus HealthStatus,
    decimal NormalizedPerformanceScore,
    string Reason);

/// <summary>Summary of comparative multi-strategy health assessment (US-077).</summary>
public sealed record ComparativePortfolioHealth(
    IReadOnlyList<StrategyEligibility> EligibleStrategies,
    IReadOnlyList<StrategyEligibility> QuarantinedStrategies,
    bool HasAnyEligibleStrategy,
    string Summary);

/// <summary>
/// Comparative multi-strategy health and cross-quarantine engine (US-077).
/// Evaluates concurrent strategy health and enforces portfolio-level eligibility gates.
/// </summary>
public static class MultiStrategyComparativeHealth
{
    public static ComparativePortfolioHealth EvaluatePortfolio(
        IReadOnlyList<(string StrategyId, StrategyHealthSnapshot Health, decimal RecentSharpe)> strategyEvaluations)
    {
        ArgumentNullException.ThrowIfNull(strategyEvaluations);

        var eligible = new List<StrategyEligibility>();
        var quarantined = new List<StrategyEligibility>();

        foreach (var (strategyId, health, recentSharpe) in strategyEvaluations)
        {
            if (health.Status == StrategyHealthStatus.Healthy && recentSharpe >= 0.0m)
            {
                eligible.Add(new StrategyEligibility(
                    strategyId,
                    IsEligible: true,
                    health.Status,
                    NormalizedPerformanceScore: Math.Max(0.1m, recentSharpe),
                    Reason: "HEALTHY: Normal operations and positive risk-adjusted metrics."));
            }
            else
            {
                var reason = health.Status != StrategyHealthStatus.Healthy
                    ? $"HEALTH_FAILURE: Status is {health.Status} with issues: {string.Join(',', health.Reasons)}."
                    : $"NEGATIVE_SHARPE: Rolling Sharpe {recentSharpe:F2} is below minimum hurdle 0.0.";

                quarantined.Add(new StrategyEligibility(
                    strategyId,
                    IsEligible: false,
                    health.Status,
                    NormalizedPerformanceScore: 0m,
                    reason));
            }
        }

        var hasAny = eligible.Count > 0;
        var summary = hasAny
            ? $"PORTFOLIO_ACTIVE: {eligible.Count} of {strategyEvaluations.Count} strategies eligible for allocation."
            : "PORTFOLIO_HALTED: Zero strategies passed comparative health checks. All new entries blocked.";

        return new ComparativePortfolioHealth(eligible, quarantined, hasAny, summary);
    }
}
