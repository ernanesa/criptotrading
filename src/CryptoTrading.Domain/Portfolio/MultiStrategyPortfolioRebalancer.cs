namespace CryptoTrading.Domain.Portfolio;

/// <summary>Intent to adjust capital allocation for an individual strategy (US-076).</summary>
public sealed record StrategyRebalanceInstruction(
    string StrategyId,
    decimal CurrentNotional,
    decimal TargetNotional,
    decimal DeltaNotional,
    bool RebalanceRequired,
    string Reason);

/// <summary>Portfolio-wide rebalance execution plan minimizing turnover (US-076).</summary>
public sealed record PortfolioRebalancePlan(
    IReadOnlyList<StrategyRebalanceInstruction> Instructions,
    decimal TotalTurnoverNotional,
    bool RebalancingNeeded,
    string Summary);

/// <summary>
/// Multi-strategy portfolio rebalancer (US-076).
/// Computes minimum turnover rebalancing adjustments while enforcing capacity caps and hysteresis bands.
/// </summary>
public static class MultiStrategyPortfolioRebalancer
{
    public const decimal DefaultHysteresisThreshold = 0.05m; // 5% minimum shift to trigger rebalance
    public const decimal DefaultMaxStrategyCap = 0.60m; // Max 60% of total capital in any single strategy

    public static PortfolioRebalancePlan PlanRebalance(
        IReadOnlyList<(string StrategyId, decimal CurrentNotional, decimal ProposedWeight)> strategyInputs,
        decimal totalCapitalNotional,
        decimal hysteresisThreshold = DefaultHysteresisThreshold,
        decimal maxStrategyCap = DefaultMaxStrategyCap)
    {
        ArgumentNullException.ThrowIfNull(strategyInputs);

        if (totalCapitalNotional <= 0m || strategyInputs.Count == 0)
        {
            return new PortfolioRebalancePlan([], 0m, RebalancingNeeded: false, "NO_CAPITAL_OR_STRATEGIES");
        }

        var instructions = new List<StrategyRebalanceInstruction>(strategyInputs.Count);
        decimal totalTurnover = 0m;
        var anyRebalance = false;

        foreach (var (strategyId, currentNotional, proposedWeight) in strategyInputs)
        {
            // Cap target weight at maxStrategyCap
            var cappedWeight = Math.Min(proposedWeight, maxStrategyCap);
            var targetNotional = Math.Round(totalCapitalNotional * cappedWeight, 2);
            var deltaNotional = targetNotional - currentNotional;

            // Hysteresis threshold check relative to strategy target
            var deltaRate = targetNotional > 0m ? Math.Abs(deltaNotional) / targetNotional : 0m;
            var rebalanceNeeded = deltaRate >= hysteresisThreshold && Math.Abs(deltaNotional) > 10.0m;

            if (rebalanceNeeded)
            {
                anyRebalance = true;
                totalTurnover += Math.Abs(deltaNotional);
            }

            var reason = rebalanceNeeded
                ? $"REBALANCE_TRIGGERED: Delta {deltaNotional:+$#,##0.00;-$#,##0.00} ({deltaRate:P1}) exceeds hysteresis threshold {hysteresisThreshold:P1}."
                : "WITHIN_BAND: Allocation drift is within hysteresis threshold; turnover suppressed.";

            instructions.Add(new StrategyRebalanceInstruction(
                strategyId,
                currentNotional,
                targetNotional,
                deltaNotional,
                rebalanceNeeded,
                reason));
        }

        var summary = anyRebalance
            ? $"REBALANCE_RECOMMENDED: Total turnover required is ${totalTurnover:F2}."
            : "PORTFOLIO_BALANCED: All strategies remain within tolerance bands. Zero unnecessary turnover.";

        return new PortfolioRebalancePlan(instructions, Math.Round(totalTurnover, 2), anyRebalance, summary);
    }
}
