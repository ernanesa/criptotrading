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
    MarketRiskSnapshot? MarketRisk = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Bars);
        ArgumentNullException.ThrowIfNull(Portfolio);
        ArgumentNullException.ThrowIfNull(RiskPolicy);
        ArgumentNullException.ThrowIfNull(OperationalControl);
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

    public BaselineDecisionCycleResult Evaluate(BaselineDecisionCycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var decisions = strategy.Evaluate(request.Bars);
        var decision = decisions[^1];
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
        var requestRisk = new RiskEvaluationRequest(
            targetNotional,
            request.Portfolio.GrossExposureNotional,
            request.Portfolio.DailyLossNotional,
            request.Portfolio.Drawdown,
            decision.CloseTimeUtc,
            request.EvaluatedAtUtc,
            marketRisk: request.MarketRisk);
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
