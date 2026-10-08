using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Portfolio;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>Request payload for one adaptive multi-strategy autonomy cycle.</summary>
public sealed record AdaptiveDecisionCycleRequest(
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
    decimal ExplorationFactor = 0.10m)
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

/// <summary>Outcome of an adaptive decision cycle with complete candidate and regime trace.</summary>
public sealed record AdaptiveDecisionCycleResult(
    MarketRegimeSnapshot Regime,
    IReadOnlyList<StrategyCandidateEvaluation> Candidates,
    string? SelectedStrategyId,
    string SelectedStrategyVersion,
    OrderSide? RecommendedSide,
    decimal RecommendedNotional,
    ExecutionTacticDecision? TacticRecommendation,
    OrderIntent? Intent,
    OrderExecutionResult? Execution,
    NettedOrderProposal? NettingProposal,
    string? NoTradeReason,
    string DecisionTraceJson)
{
    public bool IsNoTrade => Intent is null;
}

/// <summary>
/// State-Of-The-Art Adaptive Decision Cycle (A3 - A5).
/// Coordinates regime detection, multi-strategy candidacy, contextual weighting,
/// portfolio netting, adaptive execution tactic scoring, and independent risk enforcement.
/// </summary>
public sealed class AdaptiveDecisionCycle
{
    public const string DefaultStrategyVersion = "3.0.0-adaptive";
    public const string DefaultStrategyId = "adaptive-multi-strategy-orchestrator";

    private readonly ContextualMetaAllocator _metaAllocator;

    public AdaptiveDecisionCycle(ContextualMetaAllocator? metaAllocator = null)
    {
        _metaAllocator = metaAllocator ?? new ContextualMetaAllocator();
    }

    public static string StrategyId => DefaultStrategyId;
    public static string StrategyVersion => DefaultStrategyVersion;

    /// <summary>Loads durable reward aggregates before this cycle starts evaluating strategies.</summary>
    public void LoadStrategyRegimePerformances(IEnumerable<StrategyRegimePerformance> performances) =>
        _metaAllocator.LoadPerformances(performances);

    public AdaptiveDecisionCycleResult Evaluate(AdaptiveDecisionCycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var bars = request.Bars;
        var latestBar = bars[^1];
        var symbol = latestBar.Symbol;

        // 1. Minimum historical bars check
        if (bars.Count < 5)
        {
            var staleRegime = new MarketRegimeSnapshot(
                symbol,
                request.EvaluatedAtUtc,
                MarketRegime.IlliquidOrStale,
                0m, 0m, 0m,
                "INSUFFICIENT_DATA: Less than 5 closed bars.");

            return CreateNoTradeResult(
                staleRegime,
                [],
                null,
                "INSUFFICIENT_MARKET_HISTORY: Less than 5 bars available for regime analysis.");
        }

        // 2. Regime Classification
        var closePrices = new List<decimal>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            closePrices.Add(bars[i].ClosePrice.Value);
        }

        var regime = MarketRegimeClassifier.Classify(symbol, closePrices, request.EvaluatedAtUtc);

        // 3. Operational Mode & Risk Pre-checks
        if (request.OperationalControl.BlocksNewEntries)
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"OPERATIONAL_CONTROL_BLOCKED:{request.OperationalControl.Mode}");
        }

        // 4. Enforce fail-closed regime protection: Chop or Stale forbids new exposure
        if (regime.ProhibitsNewExposure)
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"REGIME_PROHIBITS_NEW_EXPOSURE:{regime.Regime}:{regime.Reason}");
        }

        // 5. Evaluate Multi-Strategy Candidacy
        var orchestration = MultiStrategyDecisionOrchestrator.Orchestrate(
            symbol,
            bars,
            request.EntryTargetNotional,
            request.EvaluatedAtUtc);

        if (orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId))
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                orchestration.OrchestrationReason);
        }

        var isEntry = orchestration.SelectedSide.Value == OrderSide.Buy;
        if (isEntry && request.Portfolio.GrossExposureNotional > 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                "ALREADY_EXPOSED");
        }

        if (!isEntry && request.Portfolio.GrossExposureNotional <= 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                "NO_POSITION_TO_EXIT");
        }

        // 6. Contextual Meta-Allocation Sizing
        var candidateIds = orchestration.Candidates.Select(c => c.StrategyId).Distinct().ToArray();
        var weights = _metaAllocator.ComputeWeights(
            regime.Regime,
            candidateIds,
            request.EntryTargetNotional,
            request.ExplorationFactor);

        var selectedWeight = weights.FirstOrDefault(w => string.Equals(w.StrategyId, orchestration.SelectedStrategyId, StringComparison.OrdinalIgnoreCase));
        var targetNotional = selectedWeight?.AllocatedCapital ?? request.EntryTargetNotional;
        if (targetNotional <= 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                $"ALLOCATION_ZERO: Meta-allocator assigned 0 capital to {orchestration.SelectedStrategyId} under {regime.Regime}.");
        }

        // 7. Portfolio Order Netting
        var referencePrice = latestBar.ClosePrice.Value;
        var rawQuantity = decimal.Round(targetNotional / referencePrice, 8);
        if (request.ExecutionMarket?.QuantityStepSize is { } stepSize && stepSize > 0m)
        {
            var steps = Math.Floor((targetNotional / referencePrice) / stepSize);
            rawQuantity = steps * stepSize;
        }
        var strategyIntents = new List<StrategyOrderIntent>
        {
            new(orchestration.SelectedStrategyId, symbol, orchestration.SelectedSide.Value, rawQuantity, referencePrice, request.EvaluatedAtUtc)
        };

        var nettingProposal = PortfolioNettingEngine.NetIntents(symbol, strategyIntents);
        if (!nettingProposal.ActionSide.HasValue || nettingProposal.NettedQuantity <= 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                nettingProposal,
                "NETTING_CANCELED: Internal portfolio netting resulted in zero net quantity.");
        }

        // 8. Adaptive Execution Tactic Selection
        var urgency = orchestration.SelectedSide == OrderSide.Sell ? 0.70m : 0.40m;
        var topOfBookSpreadBps = request.ExecutionMarket is not null && request.ExecutionMarket.BestBid.Value > 0m
            ? Math.Round((request.ExecutionMarket.BestAsk.Value - request.ExecutionMarket.BestBid.Value) / request.ExecutionMarket.BestBid.Value * 10_000m, 2)
            : 3.0m;
        var expectedSlippageBps = request.MarketRisk?.ExpectedSlippageBps ?? 5.0m;
        var makerFee = request.EffectiveCommission?.Standard.Maker ?? 0.0005m;
        var takerFee = request.EffectiveCommission?.Standard.Taker ?? 0.0010m;

        var tacticRecommendation = AdaptiveExecutionTacticScorer.SelectTactic(
            urgency,
            topOfBookSpreadBps,
            expectedSlippageBps,
            makerFee,
            takerFee);

        // 9. Independent Risk Engine Evaluation via OrderExecutionPipeline
        var identity = BuildIdentity(orchestration.SelectedStrategyId, DefaultStrategyVersion, symbol, latestBar.CloseTimeUtc, orchestration.SelectedSide.Value.ToString());
        var nettedNotional = nettingProposal.NettedQuantity * referencePrice;
        var marketRisk = request.MarketRisk ?? new MarketRiskSnapshot(
            expectedSlippageBps: 5m,
            volatilityBps: (long)Math.Round(regime.VolatilityRate * 10_000m),
            availableLiquidityNotional: request.AvailableAccountLiquidityNotional ?? 10_000m,
            latestBar.CloseTimeUtc);

        var requestRisk = new RiskEvaluationRequest(
            nettedNotional,
            request.Portfolio.GrossExposureNotional,
            request.Portfolio.DailyLossNotional,
            request.Portfolio.Drawdown,
            latestBar.CloseTimeUtc,
            request.EvaluatedAtUtc,
            marketRisk: marketRisk,
            riskContextComplete: request.Portfolio.RiskContextComplete);

        var intent = new OrderIntent(
            identity.OrderId,
            identity.ClientOrderId,
            symbol,
            orchestration.SelectedSide.Value,
            tacticRecommendation.RequiresImmediateFill ? OrderType.Market : OrderType.Limit,
            new Quantity(nettingProposal.NettedQuantity),
            new Price(referencePrice),
            identity.CorrelationId,
            identity.IdempotencyKey,
            requestRisk,
            latestBar.CloseTimeUtc);

        var execution = OrderExecutionPipeline.Evaluate(intent, request.RiskPolicy, request.OperationalControl);
        if (!execution.IsDispatchable)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                nettingProposal,
                string.Join(',', execution.Decision.ReasonCodes),
                tacticRecommendation);
        }

        // 10. Bounded Passive Execution Planning if order book is provided
        OrderIntent finalIntent = intent;
        OrderExecutionResult finalExecution = execution;

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
                    commissionMaximumAge: request.CommissionMaximumAge));

            if (plan.Plan is not null)
            {
                var passiveRisk = new RiskEvaluationRequest(
                    plan.Plan.AuthorizedNotional,
                    request.Portfolio.GrossExposureNotional,
                    request.Portfolio.DailyLossNotional,
                    request.Portfolio.Drawdown,
                    latestBar.CloseTimeUtc,
                    request.EvaluatedAtUtc,
                    marketRisk: request.MarketRisk,
                    riskContextComplete: request.Portfolio.RiskContextComplete);

                finalIntent = new OrderIntent(
                    plan.Plan.OrderId,
                    plan.Plan.ClientOrderId,
                    symbol,
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

                finalExecution = OrderExecutionPipeline.Evaluate(finalIntent, request.RiskPolicy, request.OperationalControl);
            }
        }

        var trace = JsonSerializer.Serialize(new
        {
            Outcome = finalExecution.IsDispatchable ? "DISPATCHABLE" : "BLOCKED",
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            TrendSlope = regime.TrendSlopeRate,
            Volatility = regime.VolatilityRate,
            SelectedStrategy = orchestration.SelectedStrategyId,
            SelectedSide = orchestration.SelectedSide.Value.ToString(),
            AuthorizedNotional = targetNotional,
            Tactic = tacticRecommendation.SelectedTactic.ToString(),
            Candidates = orchestration.Candidates.Select(c => new
            {
                c.StrategyId,
                c.RecommendedSide,
                c.ExpectedUtilityScore,
                c.RawSignal,
                c.Reason
            }),
            Netting = new
            {
                nettingProposal.NettedQuantity,
                nettingProposal.SavedTurnoverNotional,
                nettingProposal.EstimatedFeeSavings
            }
        });

        return new AdaptiveDecisionCycleResult(
            regime,
            orchestration.Candidates,
            orchestration.SelectedStrategyId,
            DefaultStrategyVersion,
            orchestration.SelectedSide.Value,
            targetNotional,
            tacticRecommendation,
            finalIntent,
            finalExecution,
            nettingProposal,
            NoTradeReason: finalExecution.IsDispatchable ? null : string.Join(',', finalExecution.Decision.ReasonCodes),
            trace);
    }

    private static AdaptiveDecisionCycleResult CreateNoTradeResult(
        MarketRegimeSnapshot regime,
        IReadOnlyList<StrategyCandidateEvaluation> candidates,
        NettedOrderProposal? nettingProposal,
        string noTradeReason,
        ExecutionTacticDecision? tactic = null)
    {
        var trace = JsonSerializer.Serialize(new
        {
            Outcome = "NO_TRADE",
            Reason = noTradeReason,
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            TrendSlope = regime.TrendSlopeRate,
            Volatility = regime.VolatilityRate,
            Candidates = candidates.Select(c => new
            {
                c.StrategyId,
                c.RecommendedSide,
                c.ExpectedUtilityScore,
                c.RawSignal,
                c.Reason
            })
        });

        return new AdaptiveDecisionCycleResult(
            regime,
            candidates,
            SelectedStrategyId: null,
            SelectedStrategyVersion: DefaultStrategyVersion,
            RecommendedSide: null,
            RecommendedNotional: 0m,
            tactic,
            Intent: null,
            Execution: null,
            nettingProposal,
            noTradeReason,
            trace);
    }

    private static DecisionIdentity BuildIdentity(string strategyId, string strategyVersion, Symbol symbol, DateTimeOffset closeTimeUtc, string side)
    {
        var canonical = string.Join('|', symbol.Value, strategyId, strategyVersion, closeTimeUtc.ToString("O"), side);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidBytes = hash[..16].ToArray();
        var orderId = new OrderId(new Guid(guidBytes));
        var correlationId = new CorrelationId(new Guid(hash[16..].Take(16).ToArray()));
        var token = Convert.ToHexString(hash[..16]);
        return new(orderId, new ClientOrderId($"ad-{token}"), correlationId, new IdempotencyKey($"decision:{token}"));
    }

    private sealed record DecisionIdentity(
        OrderId OrderId,
        ClientOrderId ClientOrderId,
        CorrelationId CorrelationId,
        IdempotencyKey IdempotencyKey);
}
