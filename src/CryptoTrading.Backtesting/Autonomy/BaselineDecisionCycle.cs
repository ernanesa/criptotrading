using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>All point-in-time inputs needed to evaluate one baseline decision.</summary>
public sealed record BaselineDecisionCycleRequest(
    IReadOnlyList<CloseBar> Bars,
    PortfolioSnapshot Portfolio,
    RiskPolicy RiskPolicy,
    OperationalControlSnapshot OperationalControl,
    decimal EntryTargetNotional,
    DateTimeOffset EvaluatedAtUtc,
    MarketRiskSnapshot? MarketRisk = null,
    ExecutionMarketSnapshot? ExecutionMarket = null,
    TimeSpan? ExecutionMarketMaxAge = null,
    TimeSpan? ExecutionTimeToLive = null,
    decimal? AvailableAccountLiquidityNotional = null,
    FeeReservePolicy? FeeReservePolicy = null,
    EffectiveCommissionSnapshot? EffectiveCommission = null,
    TimeSpan? CommissionMaximumAge = null,
    BaselineFeatureSnapshot? FeatureSnapshot = null,
    bool UseConservativeCommissionReserve = false)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Bars);
        ArgumentNullException.ThrowIfNull(Portfolio);
        ArgumentNullException.ThrowIfNull(RiskPolicy);
        ArgumentNullException.ThrowIfNull(OperationalControl);
        if (FeatureSnapshot is not null && FeatureSnapshot.Bars.Count == 0)
        {
            throw new ArgumentException("Feature snapshot requires at least one closed bar.", nameof(FeatureSnapshot));
        }
        if (Bars.Count == 0)
        {
            throw new ArgumentException("At least one closed bar is required.", nameof(Bars));
        }

        if (EntryTargetNotional <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(EntryTargetNotional));
        }

        if (EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evaluation time must be UTC.", nameof(EvaluatedAtUtc));
        }

        if (ExecutionMarketMaxAge is { } maxAge && maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionMarketMaxAge));
        }

        if (ExecutionTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionTimeToLive));
        }

        if (AvailableAccountLiquidityNotional is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(AvailableAccountLiquidityNotional));
        }
    }
}

/// <summary>Result of one deterministic baseline cycle; no exchange I/O occurs here.</summary>
public sealed record BaselineDecisionCycleResult(
    TrendFollowingDecision Decision,
    OrderIntent? Intent,
    OrderExecutionResult? Execution,
    string? NoTradeReason)
{
    public bool IsNoTrade => Intent is null;
}

/// <summary>
/// Pure A2 baseline cycle: closed-bar strategy → intent → independent risk.
/// It is deliberately free of exchange, persistence and hosting side effects.
/// </summary>
public sealed class BaselineDecisionCycle
{
    private readonly TrendFollowingStrategy strategy;

    public BaselineDecisionCycle(TrendFollowingParameters parameters)
    {
        strategy = new TrendFollowingStrategy(parameters ?? throw new ArgumentNullException(nameof(parameters)));
    }

    /// <summary>Strategy identifier participates in the durable checkpoint scope and decision identity.</summary>
    public string StrategyId => strategy.Parameters.StrategyId;

    /// <summary>Version participates in the durable checkpoint scope and decision identity.</summary>
    public string StrategyVersion => strategy.Parameters.Version;

    /// <summary>Immutable parameters used to derive feature snapshots for this cycle.</summary>
    public TrendFollowingParameters Parameters => strategy.Parameters;

    public BaselineDecisionCycleResult Evaluate(BaselineDecisionCycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var snapshot = request.FeatureSnapshot ?? BaselineFeatureSnapshot.Create(
            request.Bars.Select((bar, index) => new ClosedBarEnvelope(index, bar, bar.CloseTimeUtc)).ToArray(),
            strategy.Parameters);
        snapshot.ValidateFor(strategy.Parameters, request.Bars);
        if (request.EvaluatedAtUtc < snapshot.AvailableAtUtc)
        {
            throw new InvalidOperationException("Baseline features cannot be evaluated before the latest bar is available.");
        }

        var decision = snapshot.Decision;
        if (decision.Signal == TrendFollowingSignal.NoTrade)
        {
            return new(decision, null, null, decision.Reason.ToString());
        }

        var isEntry = decision.Signal == TrendFollowingSignal.EnterLong;
        if (isEntry && request.Portfolio.GrossExposureNotional > 0m)
        {
            return new(decision, null, null, "ALREADY_EXPOSED");
        }

        if (!isEntry && request.Portfolio.GrossExposureNotional <= 0m)
        {
            return new(decision, null, null, "NO_POSITION_TO_EXIT");
        }

        var targetNotional = isEntry
            ? request.EntryTargetNotional
            : request.Portfolio.GrossExposureNotional;
        var quantity = new Quantity(targetNotional / decision.SymbolPrice().Value);
        var identity = BuildIdentity(decision, isEntry ? "BUY" : "SELL");
        var requestRiskExposure = !isEntry
            ? Math.Max(request.Portfolio.GrossExposureNotional, targetNotional)
            : request.Portfolio.GrossExposureNotional;
        var requestRisk = new RiskEvaluationRequest(
            targetNotional,
            requestRiskExposure,
            request.Portfolio.DailyLossNotional,
            request.Portfolio.Drawdown,
            decision.CloseTimeUtc,
            request.EvaluatedAtUtc,
            marketRisk: request.MarketRisk,
            riskContextComplete: request.Portfolio.RiskContextComplete);
        var intent = new OrderIntent(
            identity.OrderId,
            identity.ClientOrderId,
            decision.Symbol,
            isEntry ? OrderSide.Buy : OrderSide.Sell,
            OrderType.Market,
            quantity,
            decision.SymbolPrice(),
            identity.CorrelationId,
            identity.IdempotencyKey,
            requestRisk,
            decision.CloseTimeUtc);
        var execution = OrderExecutionPipeline.Evaluate(intent, request.RiskPolicy, request.OperationalControl);

        // The strategy/risk decision remains pure. When a fresh top-of-book
        // snapshot is supplied, replace the provisional market intent with one
        // deterministic passive-limit attempt. No exchange or persistence I/O
        // occurs in this layer.
        if (execution.IsDispatchable && request.ExecutionMarket is not null)
        {
            var plan = ExecutionPlanner.Plan(
                new ExecutionPlanRequest(
                    intent,
                    execution.Decision,
                    request.ExecutionMarket,
                    request.EvaluatedAtUtc,
                    request.ExecutionMarketMaxAge ?? request.RiskPolicy.MaxMarketDataAge,
                    request.ExecutionTimeToLive ?? TimeSpan.FromMinutes(1),
                    availableAccountLiquidityNotional: request.AvailableAccountLiquidityNotional,
                    feeReservePolicy: request.FeeReservePolicy,
                    effectiveCommission: request.EffectiveCommission,
                    commissionMaximumAge: request.CommissionMaximumAge,
                    useConservativeCommissionReserve: request.UseConservativeCommissionReserve));
            if (plan.Plan is null)
            {
                return new(
                    decision,
                    intent,
                    new OrderExecutionResult(
                        execution.Decision,
                        null,
                        null),
                    plan.NoTradeReason);
            }

            var passiveRiskExposure = !isEntry
                ? Math.Max(request.Portfolio.GrossExposureNotional, plan.Plan.AuthorizedNotional)
                : request.Portfolio.GrossExposureNotional;
            var passiveRisk = new RiskEvaluationRequest(
                plan.Plan.AuthorizedNotional,
                passiveRiskExposure,
                request.Portfolio.DailyLossNotional,
                request.Portfolio.Drawdown,
                decision.CloseTimeUtc,
                request.EvaluatedAtUtc,
                marketRisk: request.MarketRisk,
                riskContextComplete: request.Portfolio.RiskContextComplete);
            var passiveIntent = new OrderIntent(
                plan.Plan.OrderId,
                plan.Plan.ClientOrderId,
                decision.Symbol,
                plan.Plan.Side,
                OrderType.Limit,
                plan.Plan.Quantity,
                plan.Plan.LimitPrice,
                intent.CorrelationId,
                plan.Plan.IdempotencyKey,
                passiveRisk,
                request.EvaluatedAtUtc,
                plan.Plan.EstimatedCommissionRate,
                plan.Plan.CommissionSnapshotHash,
                plan.Plan.CommissionSource,
                plan.Plan.CommissionObservedAtUtc,
                plan.Plan.CommissionValidUntilUtc,
                plan.Plan.Attempt,
                plan.Plan.ExpiresAtUtc);
            execution = OrderExecutionPipeline.Evaluate(passiveIntent, request.RiskPolicy, request.OperationalControl);
            return new(decision, passiveIntent, execution, execution.IsDispatchable ? null : string.Join(',', execution.Decision.ReasonCodes));
        }

        return new(decision, intent, execution, execution.IsDispatchable ? null : string.Join(',', execution.Decision.ReasonCodes));
    }

    private static DecisionIdentity BuildIdentity(TrendFollowingDecision decision, string side)
    {
        var canonical = string.Join('|', decision.Symbol.Value, decision.StrategyId, decision.StrategyVersion, decision.CloseTimeUtc.ToString("O"), side);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidBytes = hash[..16].ToArray();
        var orderId = new OrderId(new Guid(guidBytes));
        var correlationId = new CorrelationId(new Guid(hash[16..].Take(16).ToArray()));
        var token = Convert.ToHexString(hash[..16]);
        return new(orderId, new ClientOrderId($"ct-{token}"), correlationId, new IdempotencyKey($"decision:{token}"));
    }

    private sealed record DecisionIdentity(
        OrderId OrderId,
        ClientOrderId ClientOrderId,
        CorrelationId CorrelationId,
        IdempotencyKey IdempotencyKey);
}

internal static class TrendFollowingDecisionExtensions
{
    public static Price SymbolPrice(this TrendFollowingDecision decision) =>
        decision is null ? throw new ArgumentNullException(nameof(decision)) : decision.ClosePrice;
}
