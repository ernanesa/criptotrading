namespace CryptoTrading.Domain.Risk;

public enum StrategyHealthStatus
{
    Healthy,
    NoTrade,
    Blocked
}

public sealed record StrategyHealthSnapshot(
    StrategyHealthStatus Status,
    IReadOnlyList<string> Reasons,
    DateTimeOffset EvaluatedAtUtc)
{
    public bool CanOpenNewExposure => Status == StrategyHealthStatus.Healthy;
}

public sealed record StrategyHealthInput(
    bool HasFreshMarketData,
    bool HasFreshSignal,
    bool RiskAllowsNewExposure,
    bool ReconciliationIsHealthy,
    bool OperationalControlAllowsTrading,
    DateTimeOffset EvaluatedAtUtc);

public static class StrategyHealthEvaluator
{
    public static StrategyHealthSnapshot Evaluate(StrategyHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evaluation timestamp must be UTC.", nameof(input));
        }

        var reasons = new List<string>();
        if (!input.HasFreshMarketData)
        {
            reasons.Add("MARKET_DATA_STALE");
        }

        if (!input.HasFreshSignal)
        {
            reasons.Add("SIGNAL_STALE");
        }

        if (!input.RiskAllowsNewExposure)
        {
            reasons.Add("RISK_BLOCKED");
        }

        if (!input.ReconciliationIsHealthy)
        {
            reasons.Add("RECONCILIATION_MISMATCH");
        }

        if (!input.OperationalControlAllowsTrading)
        {
            reasons.Add("OPERATIONAL_CONTROL_BLOCKED");
        }

        var status = reasons.Count == 0
            ? StrategyHealthStatus.Healthy
            : reasons.Contains("RECONCILIATION_MISMATCH") || reasons.Contains("OPERATIONAL_CONTROL_BLOCKED")
                ? StrategyHealthStatus.Blocked
                : StrategyHealthStatus.NoTrade;

        return new StrategyHealthSnapshot(status, reasons.AsReadOnly(), input.EvaluatedAtUtc);
    }
}
