using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;

namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>Summary evaluation for a single candidate strategy in the orchestrator.</summary>
public sealed record StrategyCandidateEvaluation(
    string StrategyId,
    string StrategyVersion,
    OrderSide? RecommendedSide,
    decimal ExpectedUtilityScore,
    string RawSignal,
    string Reason);

/// <summary>Consolidated outcome from the multi-strategy decision orchestrator.</summary>
public sealed record MultiStrategyOrchestrationResult(
    Symbol Symbol,
    DateTimeOffset EvaluatedAtUtc,
    MarketRegimeSnapshot Regime,
    IReadOnlyList<StrategyCandidateEvaluation> Candidates,
    string? SelectedStrategyId,
    OrderSide? SelectedSide,
    decimal SelectedNotional,
    string OrchestrationReason,
    bool IsNoTrade);

/// <summary>
/// Autonomous Multi-Strategy Orchestrator (US-074 & US-075).
/// Evaluates candidate strategies simultaneously, adjusts utility scores by the prevailing market regime,
/// and deterministically chooses the optimal strategy intent or decides NO_TRADE.
/// </summary>
public static class MultiStrategyDecisionOrchestrator
{
    public static MultiStrategyOrchestrationResult Orchestrate(
        Symbol symbol,
        IReadOnlyList<CloseBar> bars,
        decimal baseTargetNotional,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count < 5)
        {
            var emptyRegime = new MarketRegimeSnapshot(
                symbol,
                evaluatedAtUtc,
                MarketRegime.IlliquidOrStale,
                0m, 0m, 0m,
                "INSUFFICIENT_DATA: Requires at least 5 bars.");

            return new MultiStrategyOrchestrationResult(
                symbol,
                evaluatedAtUtc,
                emptyRegime,
                Candidates: [],
                SelectedStrategyId: null,
                SelectedSide: null,
                SelectedNotional: 0m,
                OrchestrationReason: "INSUFFICIENT_MARKET_HISTORY",
                IsNoTrade: true);
        }

        var closePrices = new List<decimal>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            closePrices.Add(bars[i].ClosePrice.Value);
        }

        var regime = MarketRegimeClassifier.Classify(symbol, closePrices, evaluatedAtUtc);
        var candidates = new List<StrategyCandidateEvaluation>(4);

        // 1. Evaluate Trend Following
        var trendStrategy = new TrendFollowingStrategy(new TrendFollowingParameters(
            "trend-following-btcusdt", "1.0.0", symbol, fastPeriod: Math.Min(5, bars.Count - 1), slowPeriod: bars.Count, 0.001m, 0.0005m));
        var trendDecisions = trendStrategy.Evaluate(bars);
        var lastTrend = trendDecisions[^1];
        OrderSide? trendSide = lastTrend.Signal switch
        {
            TrendFollowingSignal.EnterLong => OrderSide.Buy,
            TrendFollowingSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal trendScore = trendSide.HasValue ? (regime.FavorsTrendFollowing ? 1.5m : 0.4m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "trend-following-btcusdt", "1.0.0", trendSide, trendScore, lastTrend.Signal.ToString(), lastTrend.Reason.ToString()));

        // 2. Evaluate Breakout Momentum
        var breakoutStrategy = new BreakoutStrategy(new BreakoutParameters(
            "breakout-momentum-btcusdt", "1.0.0", symbol, entryPeriod: Math.Min(5, bars.Count - 1), exitPeriod: Math.Min(3, bars.Count - 2), 0.001m, 0.0005m));
        var breakoutDecisions = breakoutStrategy.Evaluate(bars);
        var lastBreakout = breakoutDecisions[^1];
        OrderSide? breakoutSide = lastBreakout.Signal switch
        {
            BreakoutSignal.EnterLong => OrderSide.Buy,
            BreakoutSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal breakoutScore = breakoutSide.HasValue ? (regime.Regime == MarketRegime.BullTrend ? 1.3m : 0.5m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "breakout-momentum-btcusdt", "1.0.0", breakoutSide, breakoutScore, lastBreakout.Signal.ToString(), lastBreakout.Reason.ToString()));

        // 3. Evaluate Mean Reversion
        var meanRevStrategy = new MeanReversionStrategy(new MeanReversionParameters(
            "mean-reversion-btcusdt", "1.0.0", symbol, period: Math.Min(5, bars.Count), entryZScoreThreshold: -1.2m, exitZScoreThreshold: 0.0m, 0.10m, 0.001m, 0.0005m));
        var meanRevDecisions = meanRevStrategy.Evaluate(bars);
        var lastMeanRev = meanRevDecisions[^1];
        OrderSide? meanRevSide = lastMeanRev.Signal switch
        {
            MeanReversionSignal.EnterLong => OrderSide.Buy,
            MeanReversionSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal meanRevScore = meanRevSide.HasValue ? (regime.FavorsMeanReversion ? 1.6m : 0.3m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "mean-reversion-btcusdt", "1.0.0", meanRevSide, meanRevScore, lastMeanRev.Signal.ToString(), lastMeanRev.Reason.ToString()));

        // If regime prohibits exposure (Chop or Stale), enforce NO_TRADE
        if (regime.ProhibitsNewExposure)
        {
            return new MultiStrategyOrchestrationResult(
                symbol,
                evaluatedAtUtc,
                regime,
                candidates,
                SelectedStrategyId: null,
                SelectedSide: null,
                SelectedNotional: 0m,
                OrchestrationReason: $"NO_TRADE: Market regime {regime.Regime} prohibits new exposure.",
                IsNoTrade: true);
        }

        // Select candidate with highest expected utility score > 0
        StrategyCandidateEvaluation? bestCandidate = null;
        decimal highestScore = 0m;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].RecommendedSide.HasValue && candidates[i].ExpectedUtilityScore > highestScore)
            {
                highestScore = candidates[i].ExpectedUtilityScore;
                bestCandidate = candidates[i];
            }
        }

        if (bestCandidate is null || highestScore <= 0m)
        {
            return new MultiStrategyOrchestrationResult(
                symbol,
                evaluatedAtUtc,
                regime,
                candidates,
                SelectedStrategyId: null,
                SelectedSide: null,
                SelectedNotional: 0m,
                OrchestrationReason: "NO_TRADE: No candidate strategy emitted an actionable signal.",
                IsNoTrade: true);
        }

        return new MultiStrategyOrchestrationResult(
            symbol,
            evaluatedAtUtc,
            regime,
            candidates,
            bestCandidate.StrategyId,
            bestCandidate.RecommendedSide,
            baseTargetNotional,
            OrchestrationReason: $"SELECTED: Strategy {bestCandidate.StrategyId} yielded highest utility {bestCandidate.ExpectedUtilityScore:F2} under regime {regime.Regime}.",
            IsNoTrade: false);
    }
}
