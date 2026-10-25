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
    string? PendingExitReplanStrategyId = null,
    string? PositionOriginatingStrategyId = null,
    decimal? LifetimePeakProfitBps = null,
    decimal? LeaderReturnBps = null,
    decimal? LeaderOrderBookImbalance = null,
    DateTimeOffset? LastClosedTradeAtUtc = null,
    decimal? MaxOpenPositionCorrelation = null,
    decimal? TargetVolatilityBps = null,
    int MlHorizonBars = 4,
    MarketRegime? LeaderRegime = null,
    decimal? LeaderTrendSlope = null,
    decimal? MarketBreadthRatio = null,
    decimal? EthBtcSpreadReturnBps = null,
    decimal? CanaryBasketReturnBps = null,
    decimal? CanaryBasketTrendSlope = null)
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

        if (LastClosedTradeAtUtc is { Offset: not { Ticks: 0 } })
        {
            throw new ArgumentException("Last closed trade time must be UTC.", nameof(LastClosedTradeAtUtc));
        }

        if (MlHorizonBars is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MlHorizonBars));
        }

        if (MarketBreadthRatio is < 0m or > 1.0m)
        {
            throw new ArgumentOutOfRangeException(nameof(MarketBreadthRatio), "MarketBreadthRatio must be between 0.0 and 1.0.");
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
    public const string DefaultStrategyVersion = "3.1.1-adaptive";
    public const string DefaultStrategyId = "adaptive-multi-strategy-orchestrator";
    public const decimal ProductionRealisticFeeFloorBps = 15.0m;

    private readonly ContextualMetaAllocator _metaAllocator;
    private readonly HashSet<string>? _quarantinedStrategies;

    public AdaptiveDecisionCycle(
        ContextualMetaAllocator? metaAllocator = null,
        IEnumerable<string>? quarantinedStrategies = null)
    {
        _metaAllocator = metaAllocator ?? new ContextualMetaAllocator();
        _quarantinedStrategies = quarantinedStrategies is not null
            ? new HashSet<string>(quarantinedStrategies, StringComparer.OrdinalIgnoreCase)
            : null;
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
#pragma warning disable CA1308 // Lowercase is the stable strategy-id suffix format, not an equality operation.
        var symKey = symbol.Value.ToLowerInvariant();
#pragma warning restore CA1308
        var meanReversionId = $"mean-reversion-{symKey}";
        var trendFollowingId = $"trend-following-{symKey}";

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
                "INSUFFICIENT_MARKET_HISTORY: Less than 5 bars available for regime analysis.",
                request: request);
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
        var orderBookImbalance = request.ExecutionMarket?.EffectiveOrderFlowImbalance ?? 0m;
        var microPriceDivergenceBps = request.ExecutionMarket?.MicroPriceDivergenceBps ?? 0m;
        var indicators = bars.Count >= 20
            ? TechnicalIndicatorCalculator.Evaluate(bars)
            : TechnicalIndicatorSnapshot.Empty(latestBar.ClosePrice.Value);
        var mlEval = MlNetSystem1DecisionEngine.Evaluate(
            bars,
            regime,
            OrderSide.Buy,
            orderBookImbalance,
            horizonBars: request.MlHorizonBars);

        // 3. Operational Mode & Risk Pre-checks
        // SafeMode blocks new exposure but must keep the risk-reducing exit path alive for an open position.
        // KillSwitch always blocks everything.
        var exitOnly = request.OperationalControl.BlocksNewEntries &&
                       request.OperationalControl.Mode == OperationalMode.SafeMode &&
                       request.Portfolio.GrossExposureNotional > 0m;
        if (request.OperationalControl.BlocksNewEntries && !exitOnly)
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"OPERATIONAL_CONTROL_BLOCKED:{request.OperationalControl.Mode}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval,
                request: request);
        }

        var minTradableExposure = request.EntryTargetNotional >= 6m && request.ExecutionMarket?.QuantityStepSize is not null
            ? 5m
            : 0m;
        var hasOpenPosition = request.Portfolio.GrossExposureNotional > minTradableExposure;

        // 4. Enforce fail-closed regime protection for entries.  A fresh high-volatility
        // snapshot is still sufficient to reduce an already-open position; stale or
        // insufficient data is not, because an exit cannot be priced safely.
        if (regime.ProhibitsNewExposure &&
            (!hasOpenPosition || regime.Regime == MarketRegime.IlliquidOrStale))
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"REGIME_PROHIBITS_NEW_EXPOSURE:{regime.Regime}:{regime.Reason}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval,
                request: request);
        }

        // 5. Evaluate Multi-Strategy Candidacy
        if (!hasOpenPosition && request.RiskPolicy.BudgetPolicy is { } budgetPolicy && !budgetPolicy.IsSymbolAllowed(symbol.Value))
        {
            return CreateNoTradeResult(
                regime,
                [],
                null,
                $"SYMBOL_NOT_AUTHORIZED_IN_BUDGET_POLICY:{symbol.Value}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval,
                request: request);
        }

        var orchestration = MultiStrategyDecisionOrchestrator.Orchestrate(
            symbol,
            bars,
            request.EntryTargetNotional,
            request.EvaluatedAtUtc,
            hasOpenPosition,
            _quarantinedStrategies,
            request.PositionOriginatingStrategyId);

        if (bars.Count >= 20 && orchestration.Candidates.Count > 0)
        {
            var adjustedCandidates = new List<StrategyCandidateEvaluation>(orchestration.Candidates.Count);
            foreach (var candidate in orchestration.Candidates)
            {
                var perf = _metaAllocator.GetPerformance(candidate.StrategyId, regime.Regime);
                var isNegativeExpectancy = (perf is { ObservationCount: >= 5, AverageReward: < 0m }) ||
                                           (perf is { ObservationCount: >= 3, AverageReward: <= -0.0020m });
                var canRehabilitateInShadow = request.ExplorationFactor > 0m &&
                                              mlEval.MlWinProbability >= 0.65m &&
                                              orderBookImbalance >= 0.30m;
                if (isNegativeExpectancy &&
                    !canRehabilitateInShadow &&
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
                else if (isNegativeExpectancy && canRehabilitateInShadow && candidate.ExpectedUtilityScore > 0m)
                {
                    adjustedCandidates.Add(candidate with
                    {
                        ExpectedUtilityScore = Math.Round(candidate.ExpectedUtilityScore * 0.50m, 4)
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
        var minProfitBps = Math.Max(50.0m, roundTripFeeBps * 2.0m);
        var volBps = bars.Count >= 20 ? regime.VolatilityRate * 10_000m : 10m;
        var effectiveVolBps = Math.Max(volBps, indicators.AtrBps);

        // Law 2: Stop loss placed outside the multi-bar noise band (1.8x Wilder's ATR clamped to [100 bps, 250 bps])
        var baseStopBps = Math.Max(100.0m, effectiveVolBps * 1.8m);
        var protectiveStopLossBps = Math.Clamp(baseStopBps, 100.0m, 250.0m);

        // Law 1: Fee amortization: Gross target profit scaled to 180 to 450 bps (1.8% to 4.5%)
        var totalRealisticCostBps = roundTripFeeBps + 3.0m;
        // A "break-even" exit must cover the full round trip plus a small
        // execution buffer, not merely fee + 2 bps.
        var breakEvenFloorBps = Math.Max(roundTripFeeBps + 15.0m, totalRealisticCostBps + 5.0m);
        var minTargetProfitBps = Math.Max(180.0m, totalRealisticCostBps * 4.0m);
        var maxTargetProfitBps = Math.Max(minTargetProfitBps, Math.Max(250.0m, effectiveVolBps * 4.5m));
        var targetProfitBps = Math.Clamp(effectiveVolBps * 3.5m, minTargetProfitBps, maxTargetProfitBps);

        var dynamicNoiseLossBps = bars.Count >= 20
            ? Math.Clamp(effectiveVolBps * 1.0m, 50.0m, 120.0m)
            : 60.0m;
        var maxNoiseLossBps = (regime.Regime == MarketRegime.BearTrend || regime.TrendSlopeRate <= -0.000025m)
            ? Math.Min(dynamicNoiseLossBps, 60.0m)
            : dynamicNoiseLossBps;
        var isWithinCostBasisNoiseBand = request.Portfolio.PositionCostNotional > 0m &&
            (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional) / request.Portfolio.PositionCostNotional * 10_000m < minProfitBps &&
            (request.Portfolio.PositionCostNotional - request.Portfolio.GrossExposureNotional) / request.Portfolio.PositionCostNotional * 10_000m < maxNoiseLossBps;

        var currentUnrealizedProfitBps = request.Portfolio.PositionCostNotional > 0m
            ? (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional) / request.Portfolio.PositionCostNotional * 10_000m
            : 0m;
        var exitStrategyId = !string.IsNullOrWhiteSpace(request.PositionOriginatingStrategyId)
            ? request.PositionOriginatingStrategyId
            : meanReversionId;

        var isAltcoin = !string.Equals(symbol.Value, "BTCUSDT", StringComparison.OrdinalIgnoreCase);
        var isLeaderRetreating = isAltcoin &&
                                 ((request.LeaderReturnBps is < -1.0m) ||
                                  (request.LeaderRegime is MarketRegime.BearTrend or MarketRegime.IlliquidOrStale) ||
                                  (request.LeaderTrendSlope is <= -0.00002m));
        var isCanaryBasketRetreating = isAltcoin &&
                                       ((request.CanaryBasketReturnBps is <= -12.0m) ||
                                        (request.CanaryBasketReturnBps is <= -6.0m && request.CanaryBasketTrendSlope is <= -0.00008m));
        var symbolReturnBps = bars.Count >= 2 && bars[^2].ClosePrice.Value > 0m
            ? Math.Round((bars[^1].ClosePrice.Value - bars[^2].ClosePrice.Value) / bars[^2].ClosePrice.Value * 10_000m, 4)
            : 0m;
        var effectiveLeaderReturnBps = request.CanaryBasketReturnBps is { } canaryRet && request.LeaderReturnBps is { } btcRet
            ? Math.Round((btcRet * 0.50m) + (canaryRet * 0.50m), 4)
            : (request.LeaderReturnBps ?? request.CanaryBasketReturnBps);
        var catchUpDivergenceBps = effectiveLeaderReturnBps is { } leadRet
            ? Math.Round(leadRet - symbolReturnBps, 4)
            : (decimal?)null;
        var hasCrossAssetLeaderMomentum = isAltcoin &&
                                          !isLeaderRetreating &&
                                          !isCanaryBasketRetreating &&
                                          ((request.LeaderReturnBps is >= 6.0m && request.LeaderOrderBookImbalance is >= 0.20m) ||
                                           (request.CanaryBasketReturnBps is >= 10.0m &&
                                            (request.EthBtcSpreadReturnBps is null or >= 0m) &&
                                            catchUpDivergenceBps is >= 4.0m));
        var isCrossAssetLeaderShock = isAltcoin &&
                                      ((request.LeaderReturnBps is <= -15.0m) ||
                                       (request.CanaryBasketReturnBps is <= -18.0m) ||
                                       (request.LeaderReturnBps is <= -10.0m &&
                                        request.CanaryBasketReturnBps is <= -12.0m &&
                                        request.EthBtcSpreadReturnBps is <= -8.0m));

        // High-volatility chop is a risk event, not merely the absence of an entry
        // signal.  Do not depend on alpha ownership or a profitable exit signal to
        // reduce an existing position while a fresh snapshot is available.
        if (regime.Regime == MarketRegime.HighVolatilityChop && hasOpenPosition)
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = "HIGH_VOLATILITY_CHOP_RISK_EXIT: Reducing open exposure on fresh high-volatility regime.",
                IsNoTrade = false
            };
        }
        else if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
                 bars.Count >= 20 &&
                 request.Portfolio.GrossExposureNotional > minTradableExposure &&
                 request.Portfolio.PositionCostNotional > 0m &&
                 currentUnrealizedProfitBps <= -protectiveStopLossBps)
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = $"PROTECTIVE_STOP_LOSS_EXIT: Cutting unrealized loss of {currentUnrealizedProfitBps:F1} bps beyond -{protectiveStopLossBps:F1} bps ATR stop.",
                IsNoTrade = false
            };
        }

        if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
            request.HasPendingExitReplan &&
            request.Portfolio.GrossExposureNotional > minTradableExposure)
        {
            var fallbackExitStrategy = !string.IsNullOrWhiteSpace(request.PendingExitReplanStrategyId)
                ? request.PendingExitReplanStrategyId
                : (!string.IsNullOrWhiteSpace(request.PositionOriginatingStrategyId)
                    ? request.PositionOriginatingStrategyId
                    : (orchestration.Candidates.FirstOrDefault(c => c.RecommendedSide == OrderSide.Sell)?.StrategyId ?? trendFollowingId));

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
                 request.Portfolio.PositionCostNotional > 0m &&
                 isCrossAssetLeaderShock &&
                 (currentUnrealizedProfitBps >= (roundTripFeeBps + 3.0m) ||
                  (currentUnrealizedProfitBps >= -maxNoiseLossBps &&
                   (orderBookImbalance < 0m || indicators.CvdImbalance < 0m || regime.TrendSlopeRate <= 0m))))
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = $"CROSS_ASSET_LEADER_SHOCK_EXIT: Preemptive exit on leader shock (BtcRet={request.LeaderReturnBps ?? 0m:+0.0;-0.0}bps, CanaryRet={request.CanaryBasketReturnBps ?? 0m:+0.0;-0.0}bps, Unrealized={currentUnrealizedProfitBps:+0.0;-0.0}bps).",
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
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = "LOCK_PROFIT_ADVERSE_ORDER_BOOK: Locking net profit on strong adverse order-book imbalance.",
                IsNoTrade = false
            };
        }
        else if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
                 bars.Count >= 20 &&
                 request.Portfolio.GrossExposureNotional > minTradableExposure &&
                 currentUnrealizedProfitBps >= targetProfitBps &&
                 (orderBookImbalance <= -0.15m || mlEval.MlWinProbability < 0.50m))
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = $"TAKE_PROFIT_EXPANDED_TARGET: Locking {currentUnrealizedProfitBps:F1} bps profit on expanded target in {regime.Regime}.",
                IsNoTrade = false
            };
        }
        else if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
                 bars.Count >= 20 &&
                 request.Portfolio.GrossExposureNotional > minTradableExposure &&
                 request.Portfolio.PositionCostNotional > 0m &&
                 bars[^1].ClosePrice.Value > 0m &&
                 currentUnrealizedProfitBps >= (roundTripFeeBps + 3.0m) &&
                 EvaluateTrailingStopPullback(bars, request.Portfolio, currentUnrealizedProfitBps, roundTripFeeBps, volBps, request.LifetimePeakProfitBps, mlEval.MlWinProbability, out var chandelierReason))
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = chandelierReason,
                IsNoTrade = false
            };
        }
        else if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
                 bars.Count >= 20 &&
                 request.Portfolio.GrossExposureNotional > minTradableExposure &&
                 currentUnrealizedProfitBps >= breakEvenFloorBps &&
                 (regime.Regime != MarketRegime.BullTrend ||
                  regime.TrendSlopeRate <= 0m ||
                  isLeaderRetreating ||
                  isCanaryBasketRetreating ||
                  request.LifetimePeakProfitBps is >= 75.0m ||
                  (orderBookImbalance <= -0.45m && mlEval.MlWinProbability < 0.42m)) &&
                 (orderBookImbalance <= -0.15m || mlEval.MlWinProbability < 0.46m))
        {
            orchestration = orchestration with
            {
                SelectedStrategyId = exitStrategyId,
                SelectedSide = OrderSide.Sell,
                SelectedNotional = request.Portfolio.GrossExposureNotional,
                OrchestrationReason = $"BREAK_EVEN_PROFIT_LOCK: Locking {currentUnrealizedProfitBps:F1} bps profit at break-even floor on softening momentum.",
                IsNoTrade = false
            };
        }

        var isTrendQuarantined = _quarantinedStrategies is not null && _quarantinedStrategies.Contains(trendFollowingId);
        var trendPerf = _metaAllocator.GetPerformance(trendFollowingId, regime.Regime);
        var isTrendNegativeExpectancy = (trendPerf is { ObservationCount: >= 5, AverageReward: < 0m }) ||
                                        (trendPerf is { ObservationCount: >= 3, AverageReward: <= -0.0020m });

        var hasAggressiveTakerDemand = indicators.CvdImbalance >= 0.08m && indicators.RelativeVolume >= 1.05m;
        var hasNegativeTakerDelta = indicators.CvdImbalance < -0.05m;
        var isWeakBreadth = request.MarketBreadthRatio is { } breadthRatio && breadthRatio < 0.40m;

        if ((orchestration.IsNoTrade || !orchestration.SelectedSide.HasValue || string.IsNullOrWhiteSpace(orchestration.SelectedStrategyId)) &&
            !hasOpenPosition &&
            bars.Count >= 20 &&
            !isTrendQuarantined &&
            !isTrendNegativeExpectancy &&
            !isLeaderRetreating &&
            !isCanaryBasketRetreating &&
            !hasNegativeTakerDelta &&
            !isWeakBreadth &&
            mlEval.MlWinProbability >= 0.51m &&
            (orderBookImbalance >= 0.25m ||
             (orderBookImbalance >= 0.15m && microPriceDivergenceBps >= 0.35m) ||
             (hasCrossAssetLeaderMomentum && orderBookImbalance >= 0.10m && microPriceDivergenceBps >= 0.15m) ||
             (hasAggressiveTakerDemand && orderBookImbalance >= -0.25m && microPriceDivergenceBps >= 0.0m)) &&
            regime.Regime == MarketRegime.BullTrend &&
            regime.TrendSlopeRate > 0m)
        {
            var updatedCandidates = orchestration.Candidates.Select(c =>
                string.Equals(c.StrategyId, trendFollowingId, StringComparison.OrdinalIgnoreCase)
                    ? c with { RecommendedSide = OrderSide.Buy, ExpectedUtilityScore = 1.0m, RawSignal = "EnterLong", Reason = "MlOrderBookConfluence" }
                    : c).ToList();

            orchestration = orchestration with
            {
                Candidates = updatedCandidates,
                SelectedStrategyId = trendFollowingId,
                SelectedSide = OrderSide.Buy,
                SelectedNotional = request.EntryTargetNotional,
                OrchestrationReason = $"ML_ORDERBOOK_CONFLUENCE_ENTRY: High-confidence bullish continuation (ML={mlEval.MlWinProbability:P1}, Imbalance={orderBookImbalance:+0.00;-0.00}, MicroPriceDiv={microPriceDivergenceBps:+0.00;-0.00}bps, LeaderRet={request.LeaderReturnBps ?? 0m:+0.0;-0.0}bps) in {regime.Regime}.",
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
                mlEval: mlEval,
                request: request);
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
                    mlEval: mlEval,
                    request: request);
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
                    mlEval: mlEval,
                    request: request);
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

        if (exitOnly && isEntry)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                $"OPERATIONAL_CONTROL_BLOCKED:{request.OperationalControl.Mode}",
                orderBookImbalance: orderBookImbalance,
                mlEval: mlEval,
                request: request);
        }

        if (!isEntry &&
            request.Portfolio.PositionCostNotional > 0m &&
            !request.HasPendingExitReplan &&
            !orchestration.OrchestrationReason.StartsWith("CROSS_ASSET_LEADER_SHOCK_EXIT", StringComparison.Ordinal) &&
            !orchestration.OrchestrationReason.StartsWith("BREAK_EVEN_PROFIT_LOCK", StringComparison.Ordinal) &&
            !orchestration.OrchestrationReason.StartsWith("TRAILING_STOP_CHANDELIER_EXIT", StringComparison.Ordinal) &&
            !orchestration.OrchestrationReason.StartsWith("PROTECTIVE_STOP_LOSS_EXIT", StringComparison.Ordinal) &&
            !orchestration.OrchestrationReason.StartsWith("HIGH_VOLATILITY_CHOP_RISK_EXIT", StringComparison.Ordinal) &&
            !(regime.Regime == MarketRegime.BearTrend && (request.LifetimePeakProfitBps is >= 60.0m || indicators.CvdImbalance < -0.05m)) &&
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
                    mlEval: mlEval,
                    request: request);
            }

        }

        // 5b. System-1 Calibrated Decision Confidence, ML.NET 6.0 Meta-Labeler, SSA Breakdown & Order-Book Imbalance Gate
        var selectedPerf = _metaAllocator.GetPerformance(orchestration.SelectedStrategyId!, regime.Regime)
            ?? (isEntry && regime.Regime == MarketRegime.BullTrend
                ? _metaAllocator.GetPerformance(meanReversionId, MarketRegime.BullTrend)
                : null);
        if (!isEntry)
        {
            mlEval = MlNetSystem1DecisionEngine.Evaluate(
                bars,
                regime,
                OrderSide.Sell,
                orderBookImbalance,
                selectedPerf,
                request.MlHorizonBars);
        }
        else if (selectedPerf is { ObservationCount: > 0 })
        {
            mlEval = MlNetSystem1DecisionEngine.Evaluate(
                bars,
                regime,
                OrderSide.Buy,
                orderBookImbalance,
                selectedPerf,
                request.MlHorizonBars);
        }

        var perfBonus = selectedPerf is { ObservationCount: > 0 }
            ? Math.Clamp(selectedPerf.AverageReward * 150m, -0.25m, 0.12m)
            : 0m;
        var bookAlignment = isEntry ? orderBookImbalance : -orderBookImbalance;
        var bookBonus = Math.Clamp(bookAlignment * 0.15m, -0.22m, 0.10m);
        var leaderBonus = isEntry && hasCrossAssetLeaderMomentum ? 0.035m : 0m;
        var baseConfidence = Math.Clamp(regime.Confidence + perfBonus + bookBonus + leaderBonus, 0.05m, 0.99m);
        var decisionConfidence = bars.Count >= 20
            ? Math.Round(Math.Clamp((baseConfidence * 0.58m) + (mlEval.MlWinProbability * 0.42m), 0.05m, 0.99m), 4)
            : Math.Round(baseConfidence, 4);

        var isMomentumEntry = orchestration.SelectedSide == OrderSide.Buy &&
            (orchestration.SelectedStrategyId is not null && !orchestration.SelectedStrategyId.StartsWith("mean-reversion", StringComparison.OrdinalIgnoreCase));

        var topOfBookSpreadEstBps = request.ExecutionMarket is not null && request.ExecutionMarket.BestBid.Value > 0m
            ? Math.Round((request.ExecutionMarket.BestAsk.Value - request.ExecutionMarket.BestBid.Value) / request.ExecutionMarket.BestBid.Value * 10_000m, 2)
            : 2.0m;
        var expectedSlippageCostBps = request.MarketRisk?.ExpectedSlippageBps ?? Math.Round(topOfBookSpreadEstBps * 0.5m, 2);
        var roundTripCostBps = roundTripFeeBps + expectedSlippageCostBps;
        var expectedGainBps = Math.Max(targetProfitBps, effectiveVolBps * 3.5m);
        var expectedLossBps = protectiveStopLossBps;
        var pWin = Math.Clamp((decisionConfidence * 0.50m) + (mlEval.MlWinProbability * 0.50m), 0.05m, 0.95m);
        var expectedNetEdgeBps = Math.Round((pWin * expectedGainBps) - ((1m - pWin) * expectedLossBps) - roundTripCostBps, 2);

        if (bars.Count >= 20)
        {
            if (isEntry && request.LastClosedTradeAtUtc is { } lastClose &&
                lastClose > DateTimeOffset.MinValue &&
                (request.EvaluatedAtUtc - lastClose) < TimeSpan.FromMinutes(30))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "POST_TRADE_COOLDOWN_ACTIVE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && mlEval.IsSsaStructuralBreakdown)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "SSA_STRUCTURAL_BREAKDOWN_DETECTED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && indicators.Vpin >= 0.78m && indicators.CvdImbalance <= -0.35m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "TOXIC_VPIN_FLOW_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && indicators.Rsi >= 78m && indicators.VwapDeviationBps >= 80m && indicators.CvdImbalance < 0m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "OVERBOUGHT_VWAP_STRETCH_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && indicators.CvdImbalance < -0.05m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "NEGATIVE_CVD_AGGRESSIVE_SELL_FLOW_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && isAltcoin && isLeaderRetreating)
            {
                var leaderReason = request.LeaderReturnBps is < -1.0m
                    ? "MACRO_LEADER_RETREATING_BLOCKED"
                    : "MACRO_LEADER_CORRECTION_REGIME_BLOCKED";
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    leaderReason,
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && isCanaryBasketRetreating)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "CANARY_BASKET_RETREATING_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && isAltcoin && symbolReturnBps >= 45.0m && catchUpDivergenceBps is <= -45.0m && indicators.Rsi >= 72m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "FOLLOWER_OVEREXTENDED_AGAINST_LEADERS_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry &&
                request.ExecutionMarket?.PriceTickSize is { } tickSizeVal &&
                tickSizeVal > 0m &&
                request.ExecutionMarket.BestBid.Value > 0m &&
                (tickSizeVal / request.ExecutionMarket.BestBid.Value * 10_000m) >= 15.0m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "EXCESSIVE_TICK_SPREAD_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && request.MarketBreadthRatio is { } breadth && breadth < 0.40m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "WEAK_MARKET_BREADTH_BLOCKED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && request.MaxOpenPositionCorrelation is { } maxCorr && maxCorr >= 0.85m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "CORRELATED_EXPOSURE_LIMIT_EXCEEDED",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry &&
                (orderBookImbalance <= -0.75m ||
                 (orderBookImbalance <= -0.45m && mlEval.MlWinProbability < 0.45m && !hasAggressiveTakerDemand) ||
                 (orchestration.SelectedStrategyId is not null && !orchestration.SelectedStrategyId.StartsWith("mean-reversion", StringComparison.OrdinalIgnoreCase) &&
                  (mlEval.MlWinProbability < 0.45m || (orderBookImbalance <= -0.20m && mlEval.MlWinProbability < 0.50m && !hasAggressiveTakerDemand)))))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "ADVERSE_ORDER_BOOK_IMBALANCE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && regime.Regime == MarketRegime.MeanRevertingRange &&
                isMomentumEntry &&
                (mlEval.MlWinProbability < 0.53m || orderBookImbalance < 0.10m))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "RANGE_MOMENTUM_REQUIRES_ML_AND_BOOK_CONFIRMATION",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && (decisionConfidence < 0.60m || mlEval.MlWinProbability < 0.50m))
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "LOW_DECISION_CONFIDENCE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (isEntry && expectedNetEdgeBps <= 0m)
            {
                return CreateNoTradeResult(
                    regime,
                    orchestration.Candidates,
                    null,
                    "NEGATIVE_EXPECTED_NET_EDGE",
                    decisionConfidence: decisionConfidence,
                    orderBookImbalance: orderBookImbalance,
                    mlEval: mlEval,
                    expectedNetEdgeBps: expectedNetEdgeBps,
                    request: request);
            }

            if (!isEntry &&
                request.Portfolio.PositionCostNotional > 0m &&
                !request.HasPendingExitReplan &&
                !orchestration.OrchestrationReason.StartsWith("TRAILING_STOP_CHANDELIER_EXIT", StringComparison.Ordinal) &&
                !orchestration.OrchestrationReason.StartsWith("CROSS_ASSET_LEADER_SHOCK_EXIT", StringComparison.Ordinal) &&
                (orderBookImbalance >= 0.70m ||
                 (regime.Regime == MarketRegime.BullTrend &&
                  regime.TrendSlopeRate >= 0.00018m &&
                  orderBookImbalance >= 0.25m &&
                  mlEval.MlWinProbability >= 0.60m)))
            {
                var unrealizedProfitBps = (request.Portfolio.GrossExposureNotional - request.Portfolio.PositionCostNotional)
                    / request.Portfolio.PositionCostNotional * 10_000m;
                // Keep the branch reachable after the fee-aware minimum-profit
                // increase.  A fixed 32/45 bps ceiling was below minProfitBps
                // (>=50 bps), making HOLD_WINNER unreachable.
                var maxHoldProfitBps = Math.Max(minProfitBps + 20m, targetProfitBps * 0.8m);
                if (unrealizedProfitBps >= minProfitBps && unrealizedProfitBps < maxHoldProfitBps)
                {
                    return CreateNoTradeResult(
                        regime,
                        orchestration.Candidates,
                        null,
                        "HOLD_WINNER_STRONG_BID_IMBALANCE",
                        decisionConfidence: decisionConfidence,
                        orderBookImbalance: orderBookImbalance,
                        mlEval: mlEval,
                        request: request);
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
        if (!isEntry && request.Portfolio.GrossExposureNotional > 0m)
        {
            targetNotional = Math.Min(request.Portfolio.GrossExposureNotional, request.RiskPolicy.MaxOrderNotional);
            if (request.ExecutionMarket is { AvailableLiquidityNotional: >= 10m } exitMarket &&
                exitMarket.AvailableLiquidityNotional < targetNotional)
            {
                targetNotional = exitMarket.AvailableLiquidityNotional;
            }
        }
        else if (bars.Count >= 20 && isEntry && request.ExecutionMarket?.QuantityStepSize is not null && request.EntryTargetNotional >= 6m && targetNotional > 0m)
        {
            var maxEntryOrderNotional = request.RiskPolicy.MaxOrderNotional >= 100m &&
                                        request.EntryTargetNotional <= request.RiskPolicy.MaxOrderNotional * 0.90m
                ? Math.Round(request.RiskPolicy.MaxOrderNotional * 0.90m, 2)
                : request.RiskPolicy.MaxOrderNotional;
            var baseTarget = Math.Min(request.EntryTargetNotional, maxEntryOrderNotional);
            var kellyMultiplier = (decisionConfidence >= 0.85m && mlEval.MlWinProbability >= 0.72m && orderBookImbalance >= 0.30m && microPriceDivergenceBps >= 0.35m)
                ? 1.55m
                : (decisionConfidence >= 0.82m && mlEval.MlWinProbability >= 0.65m && orderBookImbalance >= 0.25m)
                    ? 1.35m
                    : (decisionConfidence >= 0.74m && mlEval.MlWinProbability >= 0.54m)
                        ? 1.18m
                        : (decisionConfidence < 0.70m || mlEval.MlWinProbability < 0.45m)
                            ? 0.85m
                            : 1.06m;
            targetNotional = Math.Clamp(
                Math.Round(baseTarget * kellyMultiplier, 2),
                Math.Min(10m, maxEntryOrderNotional),
                maxEntryOrderNotional);
        }
        else if (request.EntryTargetNotional >= 6m && targetNotional > 0m && targetNotional < 10m)
        {
            targetNotional = Math.Min(request.EntryTargetNotional, request.RiskPolicy.MaxOrderNotional);
        }

        if (isEntry && targetNotional > 0m)
        {
            var volParityMult = request.TargetVolatilityBps is { } targetVolBps && targetVolBps > 0m && indicators.AtrBps > 0m
                ? CrossSectionalPortfolioAllocator.ComputeVolatilityParityMultiplier(indicators.AtrBps, targetVolBps)
                : 1.0m;
            var corrPenaltyMult = request.MaxOpenPositionCorrelation is { } openCorr && openCorr >= 0.65m
                ? CrossSectionalPortfolioAllocator.ComputeCorrelationPenaltyMultiplier(openCorr)
                : 1.0m;
            if (volParityMult != 1.0m || corrPenaltyMult != 1.0m)
            {
                targetNotional = Math.Clamp(
                    Math.Round(targetNotional * volParityMult * corrPenaltyMult, 2),
                    Math.Min(10m, request.RiskPolicy.MaxOrderNotional),
                    request.RiskPolicy.MaxOrderNotional);
            }
        }

        if (targetNotional <= 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                $"ALLOCATION_ZERO: Meta-allocator assigned 0 capital to {orchestration.SelectedStrategyId} under {regime.Regime}.",
                request: request);
        }

        // 7. Portfolio Order Netting
        var referencePrice = latestBar.ClosePrice.Value;
        var rawQuantity = decimal.Round(targetNotional / referencePrice, 8);
        if (request.ExecutionMarket?.QuantityStepSize is { } stepSize && stepSize > 0m)
        {
            var steps = Math.Floor(decimal.Round(targetNotional / referencePrice, 8) / stepSize);
            rawQuantity = steps * stepSize;
        }
        if (rawQuantity <= 0m)
        {
            return CreateNoTradeResult(
                regime,
                orchestration.Candidates,
                null,
                "NETTING_CANCELED: Target notional is below minimum quantity step size.",
                request: request);
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
                "NETTING_CANCELED: Internal portfolio netting resulted in zero net quantity.",
                request: request);
        }

        // 8. Adaptive Execution Tactic Selection
        var isUrgentBreakout = isMomentumEntry &&
            (mlEval.MlWinProbability >= 0.65m ||
             volBps >= 20.0m ||
             (request.ExecutionMarket is not null && request.ExecutionMarket.OrderBookImbalance >= 0.30m));
        var urgency = orchestration.SelectedSide == OrderSide.Sell
            ? 0.85m
            : (isUrgentBreakout ? 0.80m : (isMomentumEntry ? 0.50m : 0.30m));
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
            takerFee,
            orderBookImbalance: request.ExecutionMarket?.OrderBookImbalance ?? 0m,
            volatilityBps: volBps);

        // 9. Independent Risk Engine Evaluation via OrderExecutionPipeline
        var identity = BuildIdentity(orchestration.SelectedStrategyId ?? DefaultStrategyId, DefaultStrategyVersion, symbol, latestBar.CloseTimeUtc, orchestration.SelectedSide.Value.ToString());
        var nettedNotional = nettingProposal.NettedQuantity * referencePrice;
        var marketRisk = request.MarketRisk ?? new MarketRiskSnapshot(
            expectedSlippageBps: 5m,
            volatilityBps: (long)Math.Round(regime.VolatilityRate * 10_000m),
            availableLiquidityNotional: request.AvailableAccountLiquidityNotional ?? 10_000m,
            latestBar.CloseTimeUtc);

        var requestRiskExposure = orchestration.SelectedSide == OrderSide.Sell
            ? Math.Max(request.Portfolio.GrossExposureNotional, nettedNotional)
            : request.Portfolio.GrossExposureNotional;

        var requestRisk = new RiskEvaluationRequest(
            nettedNotional,
            requestRiskExposure,
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
            // The venue pre-trade boundary rejects every MARKET order because
            // notional cannot be proven without a trusted reference price.
            // ExecutionPlanner converts urgent intents into aggressive limit
            // plans when market data is available; without it, fail closed
            // instead of emitting an order that is guaranteed to be rejected.
            OrderType.Limit,
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
                tacticRecommendation,
                request: request);
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
                    useConservativeCommissionReserve: request.UseConservativeCommissionReserve,
                    tactic: tacticRecommendation));

            if (plan.Plan is not null)
            {
                var passiveNotional = plan.Plan.Quantity.Value * plan.Plan.LimitPrice.Value;
                var passiveExposure = plan.Plan.Side == OrderSide.Sell
                    ? Math.Max(request.Portfolio.GrossExposureNotional, passiveNotional)
                    : request.Portfolio.GrossExposureNotional;

                var passiveRisk = new RiskEvaluationRequest(
                    passiveNotional,
                    passiveExposure,
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
                    mlEval: mlEval,
                    request: request);
            }
        }

        var trace = JsonSerializer.Serialize(new
        {
            CodeVersion = CryptoTrading.Domain.Runtime.RuntimeCodeVersion.Current,
            StrategyVersion = DefaultStrategyVersion,
            Outcome = finalExecution.IsDispatchable ? "DISPATCHABLE" : "BLOCKED",
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            DecisionConfidence = decisionConfidence,
            MlWinProbability = mlEval.MlWinProbability,
            ExpectedNetEdgeBps = expectedNetEdgeBps,
            SsaChangePointPValue = mlEval.SsaChangePointPValue,
            SsaMartingaleScore = mlEval.SsaMartingaleScore,
            IsSsaStructuralBreakdown = mlEval.IsSsaStructuralBreakdown,
            OrderBookImbalance = orderBookImbalance,
            TemporalOfi = request.ExecutionMarket?.TemporalOrderFlowImbalance,
            TradeAggressionDelta = request.ExecutionMarket?.TradeAggressionDelta,
            MicroPriceDivergenceBps = microPriceDivergenceBps,
            TrendSlope = regime.TrendSlopeRate,
            Volatility = regime.VolatilityRate,
            AtrBps = indicators.AtrBps,
            Adx = indicators.Adx,
            Rsi = indicators.Rsi,
            VwapDeviationBps = indicators.VwapDeviationBps,
            RelativeVolume = indicators.RelativeVolume,
            CvdImbalance = indicators.CvdImbalance,
            Vpin = indicators.Vpin,
            MaxOpenPositionCorrelation = request.MaxOpenPositionCorrelation,
            LeaderReturnBps = request.LeaderReturnBps,
            LeaderRegime = request.LeaderRegime?.ToString(),
            LeaderTrendSlope = request.LeaderTrendSlope,
            MarketBreadthRatio = request.MarketBreadthRatio,
            EthBtcSpreadReturnBps = request.EthBtcSpreadReturnBps,
            CanaryBasketReturnBps = request.CanaryBasketReturnBps,
            CanaryBasketTrendSlope = request.CanaryBasketTrendSlope,
            CatchUpDivergenceBps = catchUpDivergenceBps,
            SelectedStrategy = orchestration.SelectedStrategyId,
            SelectedSide = orchestration.SelectedSide.Value.ToString(),
            AuthorizedNotional = targetNotional,
            Tactic = tacticRecommendation.SelectedTactic.ToString(),
            OrchestrationReason = orchestration.OrchestrationReason,
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
        MlNetSystem1Evaluation? mlEval = null,
        decimal? expectedNetEdgeBps = null,
        AdaptiveDecisionCycleRequest? request = null)
    {
        var trace = JsonSerializer.Serialize(new
        {
            CodeVersion = CryptoTrading.Domain.Runtime.RuntimeCodeVersion.Current,
            StrategyVersion = DefaultStrategyVersion,
            Outcome = "NO_TRADE",
            Reason = noTradeReason,
            Regime = regime.Regime.ToString(),
            RegimeConfidence = regime.Confidence,
            DecisionConfidence = decisionConfidence ?? regime.Confidence,
            MlWinProbability = mlEval?.MlWinProbability ?? regime.Confidence,
            ExpectedNetEdgeBps = expectedNetEdgeBps ?? 0m,
            SsaChangePointPValue = mlEval?.SsaChangePointPValue ?? 0.50m,
            SsaMartingaleScore = mlEval?.SsaMartingaleScore ?? 0m,
            IsSsaStructuralBreakdown = mlEval?.IsSsaStructuralBreakdown ?? false,
            OrderBookImbalance = orderBookImbalance ?? 0m,
            TrendSlope = regime.TrendSlopeRate,
            Volatility = regime.VolatilityRate,
            LeaderReturnBps = request?.LeaderReturnBps,
            LeaderRegime = request?.LeaderRegime?.ToString(),
            LeaderTrendSlope = request?.LeaderTrendSlope,
            MarketBreadthRatio = request?.MarketBreadthRatio,
            EthBtcSpreadReturnBps = request?.EthBtcSpreadReturnBps,
            CanaryBasketReturnBps = request?.CanaryBasketReturnBps,
            CanaryBasketTrendSlope = request?.CanaryBasketTrendSlope,
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

    private static bool EvaluateTrailingStopPullback(
        IReadOnlyList<CloseBar> bars,
        PortfolioSnapshot portfolio,
        decimal currentUnrealizedProfitBps,
        decimal roundTripFeeBps,
        decimal volBps,
        decimal? lifetimePeakProfitBps,
        decimal mlWinProbability,
        out string reason)
    {
        reason = string.Empty;
        if (bars.Count < 20 || portfolio.PositionCostNotional <= 0m || bars[^1].ClosePrice.Value <= 0m)
        {
            return false;
        }

        decimal peakProfitBps;
        if (lifetimePeakProfitBps.HasValue)
        {
            peakProfitBps = Math.Max(lifetimePeakProfitBps.Value, currentUnrealizedProfitBps);
        }
        else
        {
            var lookbackBars = Math.Min(15, bars.Count);
            var peakRecentClose = bars.TakeLast(lookbackBars).Max(b => b.ClosePrice.Value);
            var currentPrice = bars[^1].ClosePrice.Value;
            var heldQuantity = portfolio.GrossExposureNotional / currentPrice;
            var peakNotional = peakRecentClose * heldQuantity;
            peakProfitBps = (peakNotional - portfolio.PositionCostNotional) / portfolio.PositionCostNotional * 10_000m;
        }

        var isHighConviction = mlWinProbability >= 0.60m;
        var trailingGapBps = isHighConviction && peakProfitBps < 150.0m
            ? Math.Clamp(volBps * 2.2m, 40.0m, 100.0m)
            : Math.Clamp(volBps * 1.5m, 30.0m, 70.0m);
        var pullbackBps = peakProfitBps - currentUnrealizedProfitBps;

        var minProfitLockBps = Math.Max(
            40.0m,
            roundTripFeeBps + (isHighConviction && peakProfitBps >= 150.0m ? 40.0m : 15.0m));

        if (peakProfitBps >= (roundTripFeeBps + 60.0m) &&
            pullbackBps >= trailingGapBps &&
            currentUnrealizedProfitBps >= minProfitLockBps)
        {
            reason = $"TRAILING_STOP_CHANDELIER_EXIT: Securing {currentUnrealizedProfitBps:F1} bps profit after {pullbackBps:F1} bps pullback from {peakProfitBps:F1} bps peak (conviction={(isHighConviction ? "High" : "Standard")}).";
            return true;
        }

        return false;
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

/// <summary>Candidate entry opportunity evaluated by <see cref="CrossSectionalPortfolioAllocator"/>.</summary>
public sealed record CrossSectionalCandidate(
    string Symbol,
    string StrategyId,
    decimal ExpectedNetEdgeBps,
    decimal AtrBps,
    decimal BaseTargetNotional,
    decimal MaxOpenPositionCorrelation = 0m);

/// <summary>Allocated capital result for a symbol in a joint multi-asset cross-sectional portfolio decision.</summary>
public sealed record CrossSectionalAllocation(
    string Symbol,
    string StrategyId,
    decimal ExpectedNetEdgeBps,
    decimal VolatilityParityMultiplier,
    decimal CorrelationPenaltyMultiplier,
    decimal AllocatedNotional,
    bool IsApproved,
    string? RejectionReason = null);

/// <summary>
/// Institutional multi-asset cross-sectional portfolio allocator combining:
/// 1. Expected Net Edge cross-sectional ranking across concurrent candidate symbols.
/// 2. Inverse-ATR Volatility Parity sizing.
/// 3. Pairwise return correlation penalty and hard correlation gate.
/// </summary>
public static class CrossSectionalPortfolioAllocator
{
    public static decimal ComputePairwiseReturnCorrelation(
        IReadOnlyList<CloseBar> barsA,
        IReadOnlyList<CloseBar> barsB,
        int maxLookbackBars = 20)
    {
        ArgumentNullException.ThrowIfNull(barsA);
        ArgumentNullException.ThrowIfNull(barsB);
        var count = Math.Min(barsA.Count, barsB.Count);
        var returnsCount = Math.Min(maxLookbackBars, count - 1);
        if (returnsCount < 3)
        {
            return 0m;
        }

        var retA = new double[returnsCount];
        var retB = new double[returnsCount];
        var startA = barsA.Count - returnsCount;
        var startB = barsB.Count - returnsCount;

        for (var i = 0; i < returnsCount; i++)
        {
            var prevA = (double)barsA[startA + i - 1].ClosePrice.Value;
            var currA = (double)barsA[startA + i].ClosePrice.Value;
            var prevB = (double)barsB[startB + i - 1].ClosePrice.Value;
            var currB = (double)barsB[startB + i].ClosePrice.Value;
            retA[i] = prevA > 0.0 ? (currA - prevA) / prevA : 0.0;
            retB[i] = prevB > 0.0 ? (currB - prevB) / prevB : 0.0;
        }

        var meanA = retA.Average();
        var meanB = retB.Average();
        double cov = 0.0;
        double varA = 0.0;
        double varB = 0.0;
        for (var i = 0; i < returnsCount; i++)
        {
            var da = retA[i] - meanA;
            var db = retB[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        var denom = Math.Sqrt(varA * varB);
        if (denom <= 1e-12)
        {
            return 0m;
        }

        return Math.Clamp(Math.Round((decimal)(cov / denom), 4), -1m, 1m);
    }

    public static decimal ComputeVolatilityParityMultiplier(decimal assetAtrBps, decimal targetAtrBps = 20m)
    {
        if (assetAtrBps <= 0m || targetAtrBps <= 0m)
        {
            return 1.0m;
        }

        return Math.Clamp(Math.Round(targetAtrBps / assetAtrBps, 4), 0.50m, 1.50m);
    }

    public static decimal ComputeCorrelationPenaltyMultiplier(decimal maxOpenCorrelation)
    {
        if (maxOpenCorrelation < 0.65m)
        {
            return 1.0m;
        }

        if (maxOpenCorrelation >= 0.85m)
        {
            return 0m;
        }

        // Linearly taper from 1.0 at rho=0.65 down to 0.35 at rho=0.85
        var excess = (maxOpenCorrelation - 0.65m) / 0.20m;
        return Math.Clamp(Math.Round(1.0m - (0.65m * excess), 4), 0.35m, 1.0m);
    }

    public static IReadOnlyList<CrossSectionalAllocation> AllocateCrossSectional(
        IReadOnlyList<CrossSectionalCandidate> candidates,
        decimal availableCapitalNotional,
        decimal maxSingleOrderNotional,
        int maxOpenPositions = 4,
        decimal targetAtrBps = 20m)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return [];
        }

        var results = new List<CrossSectionalAllocation>(candidates.Count);
        var remainingCapital = Math.Max(0m, availableCapitalNotional);
        var approvedSlots = 0;

        foreach (var candidate in candidates.OrderByDescending(static c => c.ExpectedNetEdgeBps))
        {
            if (candidate.ExpectedNetEdgeBps <= 0m)
            {
                results.Add(new CrossSectionalAllocation(
                    candidate.Symbol,
                    candidate.StrategyId,
                    candidate.ExpectedNetEdgeBps,
                    1.0m,
                    1.0m,
                    0m,
                    false,
                    "NEGATIVE_EXPECTED_NET_EDGE"));
                continue;
            }

            if (candidate.MaxOpenPositionCorrelation >= 0.85m)
            {
                results.Add(new CrossSectionalAllocation(
                    candidate.Symbol,
                    candidate.StrategyId,
                    candidate.ExpectedNetEdgeBps,
                    1.0m,
                    0m,
                    0m,
                    false,
                    "CORRELATED_EXPOSURE_LIMIT_EXCEEDED"));
                continue;
            }

            if (approvedSlots >= maxOpenPositions || remainingCapital < 10m)
            {
                results.Add(new CrossSectionalAllocation(
                    candidate.Symbol,
                    candidate.StrategyId,
                    candidate.ExpectedNetEdgeBps,
                    1.0m,
                    1.0m,
                    0m,
                    false,
                    "CROSS_SECTIONAL_BUDGET_EXHAUSTED"));
                continue;
            }

            var volMult = ComputeVolatilityParityMultiplier(candidate.AtrBps, targetAtrBps);
            var corrMult = ComputeCorrelationPenaltyMultiplier(candidate.MaxOpenPositionCorrelation);
            var desiredNotional = Math.Round(candidate.BaseTargetNotional * volMult * corrMult, 2);
            var cappedNotional = Math.Min(desiredNotional, Math.Min(maxSingleOrderNotional, remainingCapital));

            if (cappedNotional < 10m)
            {
                results.Add(new CrossSectionalAllocation(
                    candidate.Symbol,
                    candidate.StrategyId,
                    candidate.ExpectedNetEdgeBps,
                    volMult,
                    corrMult,
                    0m,
                    false,
                    "BELOW_MINIMUM_EXCHANGE_NOTIONAL"));
                continue;
            }

            remainingCapital -= cappedNotional;
            approvedSlots++;
            results.Add(new CrossSectionalAllocation(
                candidate.Symbol,
                candidate.StrategyId,
                candidate.ExpectedNetEdgeBps,
                volMult,
                corrMult,
                cappedNotional,
                true));
        }

        return results;
    }
}
