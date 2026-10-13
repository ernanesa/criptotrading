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
        DateTimeOffset evaluatedAtUtc,
        bool hasOpenPosition = false)
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

        var macroWindowStart = bars.Count > 36 ? bars.Count - 36 : 0;
        var macroClosePrices = new List<decimal>(bars.Count - macroWindowStart);
        for (var i = macroWindowStart; i < bars.Count; i++)
        {
            macroClosePrices.Add(bars[i].ClosePrice.Value);
        }

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
            evaluatedAtUtc,
            trendSlopeThreshold: trendSlopeThreshold);
        var macroSlopeThreshold = bars.Count > 20
            ? Math.Max(0.000015m, 0.00008m * 20m / macroClosePrices.Count)
            : trendSlopeThreshold;
        var macroRegime = bars.Count > 20
            ? MarketRegimeClassifier.Classify(
                symbol,
                macroClosePrices,
                evaluatedAtUtc,
                trendSlopeThreshold: macroSlopeThreshold)
            : regime;
        var candidates = new List<StrategyCandidateEvaluation>(4);

        // 1. Evaluate Trend Following
        var slowPeriod = bars.Count >= 20 ? 15 : bars.Count;
        var fastPeriod = Math.Min(5, bars.Count - 1);
        var trendStrategy = new TrendFollowingStrategy(new TrendFollowingParameters(
            "trend-following-btcusdt", "1.0.0", symbol, fastPeriod: fastPeriod, slowPeriod: slowPeriod, 0.001m, 0.0005m));
        var trendDecisions = trendStrategy.Evaluate(bars);
        var lastTrend = trendDecisions[^1];
        var trendSignal = lastTrend.Signal;
        var trendReason = lastTrend.Reason;
        var isConfirmedBullTrend = regime.Regime == MarketRegime.BullTrend &&
            regime.TrendSlopeRate >= 0.000115m &&
            macroRegime.Regime == MarketRegime.BullTrend;
        if (bars.Count >= 20 && hasOpenPosition && trendSignal == TrendFollowingSignal.NoTrade &&
            lastTrend.FastEma is { } fastMa && lastTrend.SlowEma is { } slowMa &&
            fastMa < slowMa &&
            (regime.Regime == MarketRegime.BearTrend || (slowMa - fastMa) / slowMa >= 0.0005m))
        {
            trendSignal = TrendFollowingSignal.ExitLong;
            trendReason = TrendFollowingReason.FastCrossedBelowSlow;
        }
        else if (bars.Count >= 20 && !hasOpenPosition && trendSignal == TrendFollowingSignal.NoTrade &&
                 isConfirmedBullTrend &&
                 lastTrend.FastEma is { } bullFastMa && lastTrend.SlowEma is { } bullSlowMa &&
                 bullFastMa > bullSlowMa &&
                 (bullFastMa - bullSlowMa) / bullSlowMa >= 0.00080m &&
                 (lastTrend.ClosePrice.Value - bullSlowMa) / bullSlowMa <= 0.00175m)
        {
            trendSignal = TrendFollowingSignal.EnterLong;
            trendReason = TrendFollowingReason.FastCrossedAboveSlow;
        }
        var trendReasonText = trendReason.ToString();
        if (bars.Count >= 20 && trendSignal == TrendFollowingSignal.EnterLong && !isConfirmedBullTrend)
        {
            trendSignal = TrendFollowingSignal.NoTrade;
            trendReasonText = "TrendEntryBlockedOutsideBullTrend";
        }
        else if (bars.Count >= 20 &&
                 ((!hasOpenPosition && trendSignal == TrendFollowingSignal.ExitLong) ||
                  (hasOpenPosition && trendSignal == TrendFollowingSignal.EnterLong)))
        {
            trendSignal = TrendFollowingSignal.NoTrade;
            trendReasonText = TrendFollowingReason.TrendUnchanged.ToString();
        }
        OrderSide? trendSide = trendSignal switch
        {
            TrendFollowingSignal.EnterLong => OrderSide.Buy,
            TrendFollowingSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal trendScore = trendSide.HasValue ? (regime.FavorsTrendFollowing ? 1.5m : 0.4m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "trend-following-btcusdt", "1.0.0", trendSide, trendScore, trendSignal.ToString(), trendReasonText));

        // 2. Evaluate Breakout Momentum
        var breakoutEntryPeriod = bars.Count >= 20 ? 12 : Math.Min(5, bars.Count - 1);
        var breakoutExitPeriod = bars.Count >= 20 ? 6 : Math.Min(3, bars.Count - 2);
        var breakoutStrategy = new BreakoutStrategy(new BreakoutParameters(
            "breakout-momentum-btcusdt", "1.0.0", symbol, entryPeriod: breakoutEntryPeriod, exitPeriod: breakoutExitPeriod, 0.001m, 0.0005m));
        var breakoutDecisions = breakoutStrategy.Evaluate(bars);
        var lastBreakout = breakoutDecisions[^1];
        var breakoutSignal = lastBreakout.Signal;
        var breakoutReason = lastBreakout.Reason;
        if (hasOpenPosition && breakoutSignal == BreakoutSignal.NoTrade &&
            lastBreakout.ExitChannelLow is { } exitLow && lastBreakout.ClosePrice.Value < exitLow)
        {
            breakoutSignal = BreakoutSignal.ExitLong;
            breakoutReason = BreakoutReason.ChannelDroppedBelowExit;
        }
        else if (bars.Count < 20 && !hasOpenPosition && breakoutSignal == BreakoutSignal.NoTrade &&
                 lastBreakout.EntryChannelHigh is { } entryHigh && lastBreakout.ClosePrice.Value > entryHigh)
        {
            breakoutSignal = BreakoutSignal.EnterLong;
            breakoutReason = BreakoutReason.ChannelBreakoutAbove;
        }
        else if (bars.Count >= 20 && !hasOpenPosition && breakoutSignal == BreakoutSignal.NoTrade &&
                 isConfirmedBullTrend &&
                 lastBreakout.EntryChannelHigh is { } confirmedHigh &&
                 lastBreakout.ClosePrice.Value > confirmedHigh * 1.0005m &&
                 (lastBreakout.ClosePrice.Value - confirmedHigh) / confirmedHigh <= 0.0018m)
        {
            breakoutSignal = BreakoutSignal.EnterLong;
            breakoutReason = BreakoutReason.ChannelBreakoutAbove;
        }
        var severeChannelBreakdown = lastBreakout.ExitChannelLow is { } channelLow &&
            channelLow > 0m &&
            (channelLow - lastBreakout.ClosePrice.Value) / channelLow >= 0.0008m;
        var breakoutReasonText = breakoutReason.ToString();
        if (bars.Count >= 20 && breakoutSignal == BreakoutSignal.EnterLong && !isConfirmedBullTrend)
        {
            breakoutSignal = BreakoutSignal.NoTrade;
            breakoutReasonText = "BreakoutEntryBlockedOutsideBullTrend";
        }
        else if (bars.Count >= 20 &&
                 ((!hasOpenPosition && breakoutSignal == BreakoutSignal.ExitLong) ||
                  (hasOpenPosition && breakoutSignal == BreakoutSignal.EnterLong)))
        {
            breakoutSignal = BreakoutSignal.NoTrade;
            breakoutReasonText = BreakoutReason.ChannelUnchanged.ToString();
        }
        OrderSide? breakoutSide = breakoutSignal switch
        {
            BreakoutSignal.EnterLong => OrderSide.Buy,
            BreakoutSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal breakoutScore = breakoutSide.HasValue ? (regime.Regime == MarketRegime.BullTrend ? 1.3m : 0.5m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "breakout-momentum-btcusdt", "1.0.0", breakoutSide, breakoutScore, breakoutSignal.ToString(), breakoutReasonText));

        // 3. Evaluate Mean Reversion
        var slowMaExtension = lastTrend.SlowEma is { } slowEmaVal && slowEmaVal > 0m
            ? (lastTrend.ClosePrice.Value - slowEmaVal) / slowEmaVal
            : 0m;
        var meanRevEntryZScoreThreshold = bars.Count >= 20 ? -1.30m : -1.2m;
        var meanRevExitZScoreThreshold = bars.Count >= 20
            ? (regime.Regime == MarketRegime.BullTrend
                ? (regime.TrendSlopeRate >= 0.00018m && slowMaExtension < 0.00165m ? 1.35m : 0.90m)
                : 0.35m)
            : 0.0m;
        var meanRevStrategy = new MeanReversionStrategy(new MeanReversionParameters(
            "mean-reversion-btcusdt", "1.0.0", symbol, period: Math.Min(5, bars.Count), entryZScoreThreshold: meanRevEntryZScoreThreshold, exitZScoreThreshold: meanRevExitZScoreThreshold, 0.10m, 0.001m, 0.0005m));
        var meanRevDecisions = meanRevStrategy.Evaluate(bars);
        var lastMeanRev = meanRevDecisions[^1];
        var meanRevSignal = lastMeanRev.Signal;
        var meanRevReason = lastMeanRev.Reason;
        if (hasOpenPosition && meanRevSignal == MeanReversionSignal.NoTrade &&
            lastMeanRev.ZScore is { } zScore && zScore >= meanRevExitZScoreThreshold)
        {
            meanRevSignal = MeanReversionSignal.ExitLong;
            meanRevReason = MeanReversionReason.ZScoreRevertedToMean;
        }
        else if (!hasOpenPosition && meanRevSignal == MeanReversionSignal.NoTrade &&
                 lastMeanRev.ZScore is { } entryZScore && entryZScore <= meanRevEntryZScoreThreshold)
        {
            meanRevSignal = MeanReversionSignal.EnterLong;
            meanRevReason = MeanReversionReason.ZScoreOversold;
        }
        var meanRevReasonText = meanRevReason.ToString();
        var recent1BarReturn = bars.Count >= 2 && bars[^2].ClosePrice.Value > 0m
            ? (bars[^1].ClosePrice.Value - bars[^2].ClosePrice.Value) / bars[^2].ClosePrice.Value
            : 0m;
        var recent3BarReturn = bars.Count >= 4 && bars[^4].ClosePrice.Value > 0m
            ? (bars[^1].ClosePrice.Value - bars[^4].ClosePrice.Value) / bars[^4].ClosePrice.Value
            : 0m;
        var isRecoveringRange = regime.Regime == MarketRegime.MeanRevertingRange &&
            regime.TrendSlopeRate >= 0.000015m &&
            !severeChannelBreakdown &&
            recent1BarReturn > -0.00065m &&
            recent3BarReturn > -0.0008m;
        var isDowntrendDrift = regime.TrendSlopeRate <= -0.000045m ||
            (!isRecoveringRange && macroRegime.TrendSlopeRate * macroClosePrices.Count <= -0.0012m) ||
            (regime.TrendSlopeRate < 0m && recent1BarReturn <= -0.00065m) ||
            recent3BarReturn <= -0.0008m;
        var isFallingKnifeBreakdown = bars.Count >= 20 &&
            (regime.Regime == MarketRegime.BearTrend ||
             (!isRecoveringRange && macroRegime.Regime == MarketRegime.BearTrend) ||
             severeChannelBreakdown ||
             isDowntrendDrift);
        var twentyBarMean = closePrices.Count > 0 ? closePrices.Average() : lastMeanRev.ClosePrice.Value;
        if (meanRevSignal == MeanReversionSignal.EnterLong && isFallingKnifeBreakdown)
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "FallingKnifeBreakdownBlocked";
        }
        else if (bars.Count >= 20 &&
                 meanRevSignal == MeanReversionSignal.EnterLong &&
                 lastMeanRev.ClosePrice.Value >= twentyBarMean)
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "MeanReversionAboveMacroMeanBlocked";
        }
        else if (bars.Count >= 20 &&
                 hasOpenPosition &&
                 meanRevSignal == MeanReversionSignal.ExitLong &&
                 isConfirmedBullTrend &&
                 regime.TrendSlopeRate >= 0.00018m &&
                 recent1BarReturn >= 0m &&
                 lastTrend.FastEma is { } rideFastMa && lastTrend.SlowEma is { } rideSlowMa &&
                 rideSlowMa > 0m &&
                 rideFastMa > rideSlowMa &&
                 (lastTrend.ClosePrice.Value - rideSlowMa) / rideSlowMa < 0.00165m)
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "BullTrendRideActive";
        }
        else if (bars.Count >= 20 &&
                 ((!hasOpenPosition && meanRevSignal == MeanReversionSignal.ExitLong) ||
                  (hasOpenPosition && meanRevSignal == MeanReversionSignal.EnterLong)))
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = MeanReversionReason.WithinBands.ToString();
        }
        OrderSide? meanRevSide = meanRevSignal switch
        {
            MeanReversionSignal.EnterLong => OrderSide.Buy,
            MeanReversionSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal meanRevScore = meanRevSide.HasValue ? (regime.FavorsMeanReversion ? 1.6m : 0.3m) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            "mean-reversion-btcusdt", "1.0.0", meanRevSide, meanRevScore, meanRevSignal.ToString(), meanRevReasonText));

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
