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
    decimal ExplorationFactor = 0.10m,
    bool UseConservativeCommissionReserve = false,
    int ExecutionAttempt = 0,
    bool HasPendingExitReplan = false,
    string? PendingExitReplanStrategyId = null)
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
    public const decimal ProductionRealisticFeeFloorBps = 15.0m;

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
        var regimeWindowStart = bars.Count > 20 ? bars.Count - 20 : 0;
        var closePrices = new List<decimal>(bars.Count - regimeWindowStart);
        for (var i = regimeWindowStart; i < bars.Count; i++)
        {
            closePrices.Add(bars[i].ClosePrice.Value);
        }

        var trendSlopeThreshold = bars.Count >= 20
            ? 0.00008m
            : MarketRegimeClassifier.DefaultTrendSlopeThreshold;
        var regime = MarketRegimeClassifier.Classify(
            symbol,
            closePrices,
            request.EvaluatedAtUtc,
            trendSlopeThreshold: trendSlopeThreshold);
        var orderBookImbalance = request.ExecutionMarket?.OrderBookImbalance ?? 0m;
        var mlEval = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, orderBookImbalance);

        // 3. Operational Mode & Risk Pre-checks
        if (request.OperationalControl.BlocksNewEntries)
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"OPERATIONAL_CONTROL_BLOCKED:{request.OperationalControl.Mode}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval);
        }

        // 4. Enforce fail-closed regime protection: Chop or Stale forbids new exposure
        if (regime.ProhibitsNewExposure)
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"REGIME_PROHIBITS_NEW_EXPOSURE:{regime.Regime}:{regime.Reason}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval);
        }

        // 5. Evaluate Multi-Strategy Candidacy
        var minTradableExposure = request.EntryTargetNotional >= 6m && request.ExecutionMarket?.QuantityStepSize is not null
            ? 5m
            : 0m;
        var hasOpenPosition = request.Portfolio.GrossExposureNotional > minTradableExposure;

        var orchestration = MultiStrategyDecisionOrchestrator.Orchestrate(
            symbol,
            bars,
            request.EntryTargetNotional,
            request.EvaluatedAtUtc,
            hasOpenPosition);

        if (bars.Count >= 20 && orchestration.Candidates.Count > 0)
        {
            var adjustedCandidates = new List<StrategyCandidateEvaluation>(orchestration.Candidates.Count);
            foreach (var candidate in orchestration.Candidates)
            {
                var perf = _metaAllocator.GetPerformance(candidate.StrategyId, regime.Regime);
                if (((perf is { ObservationCount: >= 3, AverageReward: < 0m }) ||
                     (perf is { ObservationCount: >= 1, AverageReward: <= -0.0015m })) &&
                    candidate.RecommendedSide == OrderSide.Buy)
                {
                    adjustedCandidates.Add(candidate with
                    {
                        RecommendedSide = null,
                        ExpectedUtilityScore = 0m,
                        RawSignal = "NoTrade",
                        Reason = "LearnedNegativeExpectancyBlocked"
                    });
                }
                else if (perf is { ObservationCount: > 0 } && candidate.ExpectedUtilityScore > 0m)
                {
                    var multiplier = 1m + Math.Clamp(perf.AverageReward * 500m, -0.80m, 1.00m);
                    adjustedCandidates.Add(candidate with
                    {
                        ExpectedUtilityScore = Math.Round(candidate.ExpectedUtilityScore * multiplier, 4)
                    });
                }
                else
                {
                    adjustedCandidates.Add(candidate);
                }
            }

            var bestAdjusted = adjustedCandidates
                .Where(c => c.RecommendedSide.HasValue && c.ExpectedUtilityScore > 0m)
                .OrderByDescending(c => c.ExpectedUtilityScore)
                .FirstOrDefault();

            orchestration = bestAdjusted is null
                ? orchestration with
                {
                    Candidates = adjustedCandidates,
                    SelectedStrategyId = null,
                    SelectedSide = null,
                    SelectedNotional = 0m,
                    OrchestrationReason = "NO_TRADE: No candidate strategy emitted an actionable signal.",
                    IsNoTrade = true
                }
                : orchestration with
                {
                    Candidates = adjustedCandidates,
                    SelectedStrategyId = bestAdjusted.StrategyId,
                    SelectedSide = bestAdjusted.RecommendedSide,
                    SelectedNotional = request.EntryTargetNotional,
                    OrchestrationReason = $"SELECTED: {bestAdjusted.StrategyId} won with learned score {bestAdjusted.ExpectedUtilityScore:F2} in {regime.Regime}.",
                    IsNoTrade = false
                };
        }

        var roundTripFeeBps = request.EffectiveCommission is { } comm
            ? Math.Max((comm.Standard.Maker + comm.Standard.Taker) * 10_000m, ProductionRealisticFeeFloorBps)
            : ProductionRealisticFeeFloorBps;
        var minProfitBps = Math.Max(22.0m, roundTripFeeBps + 7.0m);
        var maxNoiseLossBps = bars.Count >= 20
            ? (regime.Regime == MarketRegime.BearTrend || regime.TrendSlopeRate <= -0.000025m ? 9m : 14m)
            : 12m;
        var isWithinCostBasisNoiseBand = request.Portfolio.PositionCostNotional > 0m &&
            (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional) / request.Portfolio.PositionCostNotional * 10_000m < minProfitBps &&
            (request.Portfolio.PositionCostNotional - request.Portfolio.GrossExposureNotional) / request.Portfolio.PositionCostNotional * 10_000m < maxNoiseLossBps;

        var currentUnrealizedProfitBps = request.Portfolio.PositionCostNotional > 0m
            ? (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional) / request.Portfolio.PositionCostNotional * 10_000m
            : 0m;

        if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
            request.HasPendingExitReplan &&
            request.Portfolio.GrossExposureNotional > minTradableExposure &&
            (bars.Count < 20 || !isWithinCostBasisNoiseBand))
        {
            var fallbackExitStrategy = !string.IsNullOrWhiteSpace(request.PendingExitReplanStrategyId)
                ? request.PendingExitReplanStrategyId
                : (orchestration.Candidates.FirstOrDefault(c => c.RecommendedSide == OrderSide.Sell)?.StrategyId ?? "trend-following-btcusdt");

            orchestration = orchestration with
            {
                SelectedStrategyId = fallbackExitStrategy,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = "SELECTED: Pending exit replan candidate after TTL expiration.",
                IsNoTrade = false
            };
        }
        else if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
                 bars.Count >= 20 &&
                 request.Portfolio.GrossExposureNotional > minTradableExposure &&
                 currentUnrealizedProfitBps >= minProfitBps &&
                 orderBookImbalance <= -0.60m &&
                 mlEval.MlWinProbability < 0.38m)
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = "mean-reversion-btcusdt",
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = "LOCK_PROFIT_ADVERSE_ORDER_BOOK: Locking net profit on strong adverse order-book imbalance.",
                IsNoTrade = false
            };
        }

        if (orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId))
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                orchestration.OrchestrationReason,
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval);
        }

        var isEntry = orchestration.SelectedSide.Value == OrderSide.Buy;
        if (isEntry && request.Portfolio.GrossExposureNotional > minTradableExposure)
        {
            var actionableExit = orchestration.Candidates
                .Where(c => c.RecommendedSide == OrderSide.Sell && c.ExpectedUtilityScore > 0m)
                .OrderByDescending(c => c.ExpectedUtilityScore)
                .FirstOrDefault();

            if (actionableExit is null)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "ALREADY_EXPOSED",
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            orchestration = orchestration with
            {
                SelectedStrategyId = actionableExit.StrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = $"SELECTED: Exit candidate {actionableExit.StrategyId} superseded entry while already exposed."
            };
            isEntry = false;
        }

        if (!isEntry && request.Portfolio.GrossExposureNotional <= minTradableExposure)
        {
            var actionableEntry = orchestration.Candidates
                .Where(c => c.RecommendedSide == OrderSide.Buy && c.ExpectedUtilityScore > 0m)
                .OrderByDescending(c => c.ExpectedUtilityScore)
                .FirstOrDefault();

            if (actionableEntry is null)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "NO_POSITION_TO_EXIT",
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            orchestration = orchestration with
            {
                SelectedStrategyId = actionableEntry.StrategyId,
                SelectedSide = OrderSide.Buy,
                SelectedNotional = request.EntryTargetNotional,
                OrchestrationReason = $"SELECTED: Entry candidate {actionableEntry.StrategyId} superseded exit while flat."
            };
            isEntry = true;
        }

        if (!isEntry &&
            request.Portfolio.PositionCostNotional > 0m &&
            (request.Portfolio.GrossExposureNotional < request.Portfolio.PositionCostNotional ||
             (bars.Count >= 20 &&
              (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional) / request.Portfolio.PositionCostNotional * 10_000m < minProfitBps)))
        {
            var unrealizedLossBps = (request.Portfolio.PositionCostNotional - request.Portfolio.GrossExposureNotional)
                / request.Portfolio.PositionCostNotional * 10_000m;

            if (unrealizedLossBps < maxNoiseLossBps)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "EXIT_BELOW_COST_BASIS_WITHIN_NOISE_BAND",
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            if (string.Equals(orchestration.SelectedStrategyId, "mean-reversion-btcusdt", StringComparison.OrdinalIgnoreCase))
            {
                var protectiveExit = orchestration.Candidates
                    .Where(c => c.RecommendedSide == OrderSide.Sell &&
                                !string.Equals(c.StrategyId, "mean-reversion-btcusdt", StringComparison.OrdinalIgnoreCase) &&
                                c.ExpectedUtilityScore > 0m)
                    .OrderByDescending(c => c.ExpectedUtilityScore)
                    .FirstOrDefault();

                if (protectiveExit is null)
                {
                    return CreateNoTradeResult(
                        regime,
                        orchestration.Candidates,
                        null,
                        "MEAN_REVERSION_EXIT_BELOW_COST_BASIS",
                        orderBookImbalance: orderBookImbalance,
                        mlEval: mlEval);
                }

                orchestration = orchestration with
                {
                    SelectedStrategyId = protectiveExit.StrategyId,
                    SelectedSide = protectiveExit.RecommendedSide,
                    OrchestrationReason = $"SELECTED: Protective exit {protectiveExit.StrategyId} superseded underwater mean-reversion exit."
                };
            }
        }

        // 5b. System-1 Calibrated Decision Confidence, ML.NET 6.0 Meta-Labeler, SSA Breakdown & Order-Book Imbalance Gate
        var selectedPerf = _metaAllocator.GetPerformance(orchestration.SelectedStrategyId!, regime.Regime)
            ?? (isEntry && regime.Regime == MarketRegime.BullTrend
                ? _metaAllocator.GetPerformance("mean-reversion-btcusdt", MarketRegime.BullTrend)
                : null);
        if (!isEntry)
        {
            mlEval = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Sell, orderBookImbalance, selectedPerf);
        }
        else if (selectedPerf is { ObservationCount: > 0 })
        {
            mlEval = MlNetSystem1DecisionEngine.Evaluate(bars, regime, OrderSide.Buy, orderBookImbalance, selectedPerf);
        }

        var perfBonus = selectedPerf is { ObservationCount: > 0 }
            ? Math.Clamp(selectedPerf.AverageReward * 150m, -0.25m, 0.12m)
            : 0m;
        var bookAlignment = isEntry ? orderBookImbalance : -orderBookImbalance;
        var bookBonus = Math.Clamp(bookAlignment * 0.15m, -0.22m, 0.10m);
        var baseConfidence = Math.Clamp(regime.Confidence + perfBonus + bookBonus, 0.05m, 0.99m);
        var decisionConfidence = bars.Count >= 20
            ? Math.Round(Math.Clamp((baseConfidence * 0.58m) + (mlEval.MlWinProbability * 0.42m), 0.05m, 0.99m), 4)
            : Math.Round(baseConfidence, 4);

        if (bars.Count >= 20)
        {
            if (isEntry && mlEval.IsSsaStructuralBreakdown)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "SSA_STRUCTURAL_BREAKDOWN_DETECTED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            if (isEntry &&
                (orderBookImbalance <= -0.75m ||
                 (orderBookImbalance <= -0.45m && mlEval.MlWinProbability < 0.45m) ||
                 (!string.Equals(orchestration.SelectedStrategyId, "mean-reversion-btcusdt", StringComparison.OrdinalIgnoreCase) &&
                  (mlEval.MlWinProbability < 0.45m || (orderBookImbalance <= -0.20m && mlEval.MlWinProbability < 0.50m)))))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "ADVERSE_ORDER_BOOK_IMBALANCE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            if (isEntry && (decisionConfidence < 0.65m || mlEval.MlWinProbability < 0.35m))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "LOW_DECISION_CONFIDENCE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }

            if (!isEntry &&
                request.Portfolio.PositionCostNotional > 0m &&
                (orderBookImbalance >= 0.70m ||
                 (regime.Regime == MarketRegime.BullTrend &&
                  regime.TrendSlopeRate >= 0.00018m &&
                  orderBookImbalance >= 0.25m &&
                  mlEval.MlWinProbability >= 0.60m)))
            {
                var unrealizedProfitBps = (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional)
                    / request.Portfolio.PositionCostNotional * 10_000m;
                var maxHoldProfitBps = regime.Regime == MarketRegime.BullTrend && regime.TrendSlopeRate >= 0.00018m ? 45m : 32m;
                if (unrealizedProfitBps >= minProfitBps && unrealizedProfitBps < maxHoldProfitBps)
                {
                    return CreateNoTradeResult(
                        regime,
                        orchestration.Candidates,
                        null,
                        "HOLD_WINNER_STRONG_BID_IMBALANCE",
                        decisionConfidence: decisionConfidence,
                        orderBookImbalance: orderBookImbalance,
                        mlEval: mlEval);
                }
            }
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
        if (!isEntry && request.Portfolio.GrossExposureNotional > 0m && request.Portfolio.GrossExposureNotional <= request.RiskPolicy.MaxOrderNotional)
        {
            targetNotional = request.Portfolio.GrossExposureNotional;
        }
        else if (bars.Count >= 20 && isEntry && request.ExecutionMarket?.QuantityStepSize is not null && request.EntryTargetNotional >= 6m && targetNotional > 0m)
        {
            var baseTarget = Math.Min(request.EntryTargetNotional, request.RiskPolicy.MaxOrderNotional);
            var kellyMultiplier = (decisionConfidence >= 0.82m && mlEval.MlWinProbability >= 0.65m && orderBookImbalance >= 0.25m)
                ? 1.35m
                : (decisionConfidence >= 0.74m && mlEval.MlWinProbability >= 0.54m)
                    ? 1.18m
                    : (decisionConfidence < 0.70m || mlEval.MlWinProbability < 0.45m)
                        ? 0.85m
                        : 1.06m;
            targetNotional = Math.Clamp(
                Math.Round(baseTarget * kellyMultiplier, 2),
                Math.Min(10m, request.RiskPolicy.MaxOrderNotional),
                request.RiskPolicy.MaxOrderNotional);
        }
        else if (request.EntryTargetNotional >= 6m && targetNotional > 0m && targetNotional < 10m)
        {
            targetNotional = Math.Min(request.EntryTargetNotional, request.RiskPolicy.MaxOrderNotional);
        }

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
            var steps = Math.Floor(decimal.Round(targetNotional / referencePrice, 8) / stepSize);
            rawQuantity = steps * stepSize;
        }
        var strategyIntents = new List<StrategyOrderIntent>
        {
            new(orchestration.SelectedStrategyId!, symbol, orchestration.SelectedSide!.Value, rawQuantity, referencePrice, request.EvaluatedAtUtc)
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
        var isMomentumEntry = orchestration.SelectedSide == OrderSide.Buy &&
            !string.Equals(orchestration.SelectedStrategyId, "mean-reversion-btcusdt", StringComparison.OrdinalIgnoreCase);
        var urgency = isMomentumEntry ? 0.80m : (orchestration.SelectedSide == OrderSide.Sell ? 0.70m : 0.40m);
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
                    attempt: request.ExecutionAttempt,
                    availableAccountLiquidityNotional: request.AvailableAccountLiquidityNotional,
                    feeReservePolicy: request.FeeReservePolicy,
                    effectiveCommission: request.EffectiveCommission,
                    commissionMaximumAge: request.CommissionMaximumAge,
                    useConservativeCommissionReserve: request.UseConservativeCommissionReserve));

            if (plan.Plan is not null)
            {
                var passiveRisk = new RiskEvaluationRequest(
                    plan.Plan.AuthorizedNotional,
                    request.Portfolio.GrossExposureNotional,
                    request.Portfolio.DailyLossNotional,
                    request.Portfolio.Drawdown,
                    latestBar.CloseTimeUtc,
                    request.EvaluatedAtUtc,
                    marketRisk: marketRisk,
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
            else
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    nettingProposal,
                    plan.NoTradeReason ?? ExecutionPlanner.MarketDataUnavailable,
                    tacticRecommendation,
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval);
            }
        }

        var trace = JsonSerializer.Serialize(new
        {
            Outcome = finalExecution.IsDispatchable ? "DISPATCHABLE" : "BLOCKED",
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            DecisionConfidence = decisionConfidence,
            MlWinProbability = mlEval.MlWinProbability,
            SsaChangePointPValue = mlEval.SsaChangePointPValue,
            SsaMartingaleScore = mlEval.SsaMartingaleScore,
            IsSsaStructuralBreakdown = mlEval.IsSsaStructuralBreakdown,
            OrderBookImbalance = orderBookImbalance,
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
        ExecutionTacticDecision? tactic = null,
        decimal? decisionConfidence = null,
        decimal? orderBookImbalance = null,
        MlNetSystem1Evaluation? mlEval = null)
    {
        var trace = JsonSerializer.Serialize(new
        {
            Outcome = "NO_TRADE",
            Reason = noTradeReason,
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            DecisionConfidence = decisionConfidence ?? regime.Confidence,
            MlWinProbability = mlEval?.MlWinProbability ?? regime.Confidence,
            SsaChangePointPValue = mlEval?.SsaChangePointPValue ?? 0.50m,
            SsaMartingaleScore = mlEval?.SsaMartingaleScore ?? 0m,
            IsSsaStructuralBreakdown = mlEval?.IsSsaStructuralBreakdown ?? false,
            OrderBookImbalance = orderBookImbalance ?? 0m,
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
