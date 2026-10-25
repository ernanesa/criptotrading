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
        bool hasOpenPosition = false,
        IReadOnlySet<string>? quarantinedStrategies = null,
        string? positionOriginatingStrategyId = null)
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
#pragma warning disable CA1308 // Lowercase is the stable strategy-id suffix format, not an equality operation.
        var symKey = symbol.Value.ToLowerInvariant();
#pragma warning restore CA1308
        var trendStrategyId = $"trend-following-{symKey}";
        var breakoutStrategyId = $"breakout-momentum-{symKey}";
        var meanRevStrategyId = $"mean-reversion-{symKey}";
        var squeezeStrategyId = $"volatility-squeeze-{symKey}";
        var candidates = new List<StrategyCandidateEvaluation>(4);
        var indicators = bars.Count >= 20
            ? TechnicalIndicatorCalculator.Evaluate(bars)
            : TechnicalIndicatorSnapshot.Empty(bars[^1].ClosePrice.Value);

        var fastMaExtensionClamp = bars.Count >= 20
            ? Math.Min(0.0060m, Math.Max(0.0018m, 0.40m * indicators.AtrBps / 10_000m))
            : 0.0018m;
        var slowMaExtensionClamp = bars.Count >= 20
            ? Math.Min(0.0100m, Math.Max(0.0035m, 0.60m * indicators.AtrBps / 10_000m))
            : 0.0035m;
        var breakoutExtensionClamp = bars.Count >= 20
            ? Math.Min(0.0080m, Math.Max(0.0025m, 0.40m * indicators.AtrBps / 10_000m))
            : 0.0025m;
        var maxBreakoutVolatility = bars.Count >= 20
            ? Math.Min(0.0080m, Math.Max(0.0020m, 1.20m * indicators.AtrBps / 10_000m))
            : 0.0020m;

        // 1. Evaluate Trend Following
        var slowPeriod = bars.Count >= 20 ? 15 : bars.Count;
        var fastPeriod = Math.Min(5, bars.Count - 1);
        var trendStrategy = new TrendFollowingStrategy(new TrendFollowingParameters(
            trendStrategyId, "1.0.0", symbol, fastPeriod: fastPeriod, slowPeriod: slowPeriod, 0.001m, 0.0005m));
        var trendDecisions = trendStrategy.Evaluate(bars);
        var lastTrend = trendDecisions[^1];
        var trendSignal = lastTrend.Signal;
        var trendReason = lastTrend.Reason;
        var isConfirmedBullTrend = regime.Regime == MarketRegime.BullTrend &&
            regime.TrendSlopeRate >= 0.00008m &&
            (macroRegime.Regime == MarketRegime.BullTrend ||
             (macroRegime.Regime == MarketRegime.MeanRevertingRange &&
              macroRegime.TrendSlopeRate >= 0.000010m &&
              regime.TrendSlopeRate >= 0.000125m));
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
                 lastTrend.ClosePrice.Value >= bullFastMa &&
                 (bullFastMa - bullSlowMa) / bullSlowMa >= 0.00025m &&
                 (lastTrend.ClosePrice.Value - bullFastMa) / bullFastMa <= fastMaExtensionClamp &&
                 (lastTrend.ClosePrice.Value - bullSlowMa) / bullSlowMa <= slowMaExtensionClamp)
        {
            trendSignal = TrendFollowingSignal.EnterLong;
            trendReason = TrendFollowingReason.FastCrossedAboveSlow;
        }
        var trendReasonText = trendReason.ToString();
        if (IsQuarantined(trendStrategyId, quarantinedStrategies) && trendSignal == TrendFollowingSignal.EnterLong)
        {
            trendSignal = TrendFollowingSignal.NoTrade;
            trendReasonText = "StrategyQuarantinedForNegativeExpectancy";
        }
        else if (bars.Count >= 20 && trendSignal == TrendFollowingSignal.EnterLong && !isConfirmedBullTrend)
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
        var trendAdxBoost = trendSide == OrderSide.Buy && indicators.Adx >= 22m && indicators.PlusDi > indicators.MinusDi && indicators.VwapDeviationBps >= 0m
            ? 0.15m * Math.Min(indicators.RelativeVolume, 1.5m)
            : 0.0m;
        decimal trendScore = trendSide.HasValue ? Math.Round((regime.FavorsTrendFollowing ? 1.5m : 0.4m) + trendAdxBoost, 4) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            trendStrategyId, "1.0.0", trendSide, trendScore, trendSignal.ToString(), trendReasonText));

        // 2. Evaluate Breakout Momentum
        var slowMaExtension = lastTrend.SlowEma is { } slowEmaVal && slowEmaVal > 0m
            ? (lastTrend.ClosePrice.Value - slowEmaVal) / slowEmaVal
            : 0m;
        var breakoutEntryPeriod = bars.Count >= 20 ? 12 : Math.Min(5, bars.Count - 1);
        var breakoutExitPeriod = bars.Count >= 20 ? 6 : Math.Min(3, bars.Count - 2);
        var breakoutStrategy = new BreakoutStrategy(new BreakoutParameters(
            breakoutStrategyId, "1.0.0", symbol, entryPeriod: breakoutEntryPeriod, exitPeriod: breakoutExitPeriod, 0.001m, 0.0005m));
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
                 regime.VolatilityRate <= maxBreakoutVolatility &&
                 slowMaExtension <= slowMaExtensionClamp &&
                 lastBreakout.EntryChannelHigh is { } confirmedHigh &&
                 (lastBreakout.ClosePrice.Value > confirmedHigh * 1.0005m ||
                  (lastBreakout.ClosePrice.Value >= confirmedHigh &&
                   indicators.Adx >= 25m &&
                   indicators.PlusDi > indicators.MinusDi &&
                   indicators.RelativeVolume >= 1.15m &&
                   indicators.VwapDeviationBps > 0m)) &&
                 (lastBreakout.ClosePrice.Value - confirmedHigh) / confirmedHigh <= breakoutExtensionClamp)
        {
            breakoutSignal = BreakoutSignal.EnterLong;
            breakoutReason = BreakoutReason.ChannelBreakoutAbove;
        }
        var severeChannelBreakdown = lastBreakout.ExitChannelLow is { } channelLow &&
            channelLow > 0m &&
            (channelLow - lastBreakout.ClosePrice.Value) / channelLow >= 0.0008m;
        var breakoutReasonText = breakoutReason.ToString();
        if (IsQuarantined(breakoutStrategyId, quarantinedStrategies) && breakoutSignal == BreakoutSignal.EnterLong)
        {
            breakoutSignal = BreakoutSignal.NoTrade;
            breakoutReasonText = "StrategyQuarantinedForNegativeExpectancy";
        }
        else if (bars.Count >= 20 && breakoutSignal == BreakoutSignal.EnterLong && !isConfirmedBullTrend)
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
        var breakoutRvolBoost = breakoutSide == OrderSide.Buy && indicators.RelativeVolume >= 1.15m && indicators.Adx >= 20m && indicators.VwapDeviationBps > 0m
            ? 0.20m
            : 0.0m;
        decimal breakoutScore = breakoutSide.HasValue ? Math.Round((regime.Regime == MarketRegime.BullTrend ? 1.3m : 0.5m) + breakoutRvolBoost, 4) : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            breakoutStrategyId, "1.0.0", breakoutSide, breakoutScore, breakoutSignal.ToString(), breakoutReasonText));

        // 3. Evaluate Mean Reversion
        var meanRevEntryZScoreThreshold = bars.Count >= 20 ? -1.30m : -1.2m;
        var meanRevExitZScoreThreshold = bars.Count >= 20
            ? (regime.Regime == MarketRegime.BullTrend
                ? (regime.TrendSlopeRate >= 0.00018m && slowMaExtension < 0.00165m ? 1.35m : 0.90m)
                : 0.35m)
            : 0.0m;
        var meanRevStrategy = new MeanReversionStrategy(new MeanReversionParameters(
            meanRevStrategyId, "1.0.0", symbol, period: Math.Min(5, bars.Count), entryZScoreThreshold: meanRevEntryZScoreThreshold, exitZScoreThreshold: meanRevExitZScoreThreshold, 0.10m, 0.001m, 0.0005m));
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
                 lastMeanRev.ZScore is { } entryZScore &&
                 (entryZScore <= meanRevEntryZScoreThreshold ||
                  (bars.Count >= 20 &&
                   regime.Regime == MarketRegime.MeanRevertingRange &&
                   regime.TrendSlopeRate >= 0.000015m &&
                   meanRevDecisions.Count >= 2 &&
                   meanRevDecisions[^2].ZScore is { } prevZScore &&
                   prevZScore <= -1.20m &&
                   entryZScore <= -0.95m &&
                   bars[^1].ClosePrice.Value >= bars[^2].ClosePrice.Value) ||
                  (bars.Count >= 20 &&
                   regime.Regime == MarketRegime.MeanRevertingRange &&
                   indicators.Rsi <= 32m &&
                   indicators.VwapDeviationBps <= -10m &&
                   entryZScore <= -1.05m &&
                   bars[^1].ClosePrice.Value >= bars[^2].ClosePrice.Value)))
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
        if (IsQuarantined(meanRevStrategyId, quarantinedStrategies) && meanRevSignal == MeanReversionSignal.EnterLong)
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "StrategyQuarantinedForNegativeExpectancy";
        }
        else if (meanRevSignal == MeanReversionSignal.EnterLong && isFallingKnifeBreakdown)
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "FallingKnifeBreakdownBlocked";
        }
        else if (bars.Count >= 20 &&
                 meanRevSignal == MeanReversionSignal.EnterLong &&
                 regime.Regime == MarketRegime.BullTrend &&
                 (!isConfirmedBullTrend || recent1BarReturn <= -0.00045m))
        {
            meanRevSignal = MeanReversionSignal.NoTrade;
            meanRevReasonText = "UnconfirmedBullTrendPullbackBlocked";
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
            meanRevStrategyId, "1.0.0", meanRevSide, meanRevScore, meanRevSignal.ToString(), meanRevReasonText));

        // 4. Evaluate Volatility Squeeze (Carter Compression Breakout)
        var squeezePeriod = bars.Count >= 20 ? 20 : Math.Max(5, bars.Count);
        var bollingerMultiplier = regime.VolatilityRate < 0.0003m ? 1.8m : 2.0m;
        var squeezeStrategy = new VolatilitySqueezeStrategy(new VolatilitySqueezeParameters(
            squeezeStrategyId, "1.0.0", symbol, period: squeezePeriod, bollingerMultiplier: bollingerMultiplier, keltnerMultiplier: 1.5m, feeRate: 0.001m, slippageRate: 0.0005m));
        var squeezeDecisions = squeezeStrategy.Evaluate(bars);
        var lastSqueeze = squeezeDecisions[^1];
        var squeezeSignal = bars.Count >= 20 ? lastSqueeze.Signal : VolatilitySqueezeSignal.NoTrade;
        var squeezeReason = bars.Count >= 20 ? lastSqueeze.Reason : VolatilitySqueezeReason.InsufficientHistory;

        if (hasOpenPosition && string.Equals(positionOriginatingStrategyId, squeezeStrategyId, StringComparison.OrdinalIgnoreCase) &&
            squeezeSignal == VolatilitySqueezeSignal.NoTrade &&
            lastSqueeze.RollingMean is { } squeezeMean &&
            (lastSqueeze.Momentum < 0m || lastSqueeze.ClosePrice.Value < squeezeMean))
        {
            squeezeSignal = VolatilitySqueezeSignal.ExitLong;
            squeezeReason = VolatilitySqueezeReason.MomentumDecayExit;
        }

        if (hasOpenPosition && !string.Equals(positionOriginatingStrategyId, squeezeStrategyId, StringComparison.OrdinalIgnoreCase) &&
            squeezeSignal == VolatilitySqueezeSignal.ExitLong)
        {
            squeezeSignal = VolatilitySqueezeSignal.NoTrade;
            squeezeReason = VolatilitySqueezeReason.WithinNormalBand;
        }

        var squeezeReasonText = squeezeReason.ToString();
        if (IsQuarantined(squeezeStrategyId, quarantinedStrategies) && squeezeSignal == VolatilitySqueezeSignal.EnterLong)
        {
            squeezeSignal = VolatilitySqueezeSignal.NoTrade;
            squeezeReasonText = "StrategyQuarantinedForNegativeExpectancy";
        }
        else if (squeezeSignal == VolatilitySqueezeSignal.EnterLong && isFallingKnifeBreakdown)
        {
            squeezeSignal = VolatilitySqueezeSignal.NoTrade;
            squeezeReasonText = "FallingKnifeBreakdownBlocked";
        }
        else if (bars.Count >= 20 && squeezeSignal == VolatilitySqueezeSignal.EnterLong && !isConfirmedBullTrend && !isRecoveringRange)
        {
            squeezeSignal = VolatilitySqueezeSignal.NoTrade;
            squeezeReasonText = "SqueezeEntryBlockedOutsideConfirmedRegime";
        }
        else if (bars.Count >= 20 &&
                 ((!hasOpenPosition && squeezeSignal == VolatilitySqueezeSignal.ExitLong) ||
                  (hasOpenPosition && squeezeSignal == VolatilitySqueezeSignal.EnterLong)))
        {
            squeezeSignal = VolatilitySqueezeSignal.NoTrade;
            squeezeReasonText = VolatilitySqueezeReason.WithinNormalBand.ToString();
        }

        OrderSide? squeezeSide = squeezeSignal switch
        {
            VolatilitySqueezeSignal.EnterLong => OrderSide.Buy,
            VolatilitySqueezeSignal.ExitLong => OrderSide.Sell,
            _ => null
        };
        decimal squeezeScore = squeezeSide.HasValue
            ? (regime.Regime == MarketRegime.BullTrend ? 1.4m : (isRecoveringRange ? 1.1m : 0.5m))
            : 0.0m;
        candidates.Add(new StrategyCandidateEvaluation(
            squeezeStrategyId, "1.0.0", squeezeSide, squeezeScore, squeezeSignal.ToString(), squeezeReasonText));

        // Regimes prohibit new exposure, never an existing-position exit.  The
        // caller owns the independent risk-reduction policy for an open position.
        if (regime.ProhibitsNewExposure && !hasOpenPosition)
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

        // Isolate position ownership: When an open position is owned by a specific strategy,
        // prevent other strategies from triggering exits on it.
        if (hasOpenPosition && !string.IsNullOrWhiteSpace(positionOriginatingStrategyId))
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.RecommendedSide == OrderSide.Sell &&
                    !string.Equals(candidate.StrategyId, positionOriginatingStrategyId, StringComparison.OrdinalIgnoreCase))
                {
                    candidates[i] = candidate with
                    {
                        RecommendedSide = null,
                        ExpectedUtilityScore = 0m,
                        RawSignal = "NoTrade",
                        Reason = "PositionOwnedByAnotherStrategy"
                    };
                }
            }
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

    private static bool IsQuarantined(string strategyId, IReadOnlySet<string>? quarantinedStrategies)
    {
        if (quarantinedStrategies is null || quarantinedStrategies.Count == 0)
        {
            return false;
        }

        return quarantinedStrategies.Contains(strategyId) ||
               quarantinedStrategies.Any(q => strategyId.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Point-in-time SOTA technical indicator snapshot computed from closed OHLCV bars:
/// Wilder's True OHLC ATR, ADX (+DI/-DI), Rolling VWAP + Relative Volume (RVOL), Wilder's 14-period RSI,
/// Cumulative Volume Delta (CVD) Imbalance, and Volume-Synchronized Probability of Informed Trading (VPIN).
/// </summary>
public readonly record struct TechnicalIndicatorSnapshot(
    decimal Atr,
    decimal AtrBps,
    decimal Adx,
    decimal PlusDi,
    decimal MinusDi,
    decimal Rsi,
    decimal Vwap,
    decimal VwapDeviationBps,
    decimal RelativeVolume,
    decimal CvdImbalance = 0m,
    decimal Vpin = 0m)
{
    public static TechnicalIndicatorSnapshot Empty(decimal closePrice) =>
        new(0m, 0m, 15m, 15m, 15m, 50m, closePrice, 0m, 1.0m, 0m, 0m);
}

public static class TechnicalIndicatorCalculator
{
    public static TechnicalIndicatorSnapshot Evaluate(IReadOnlyList<CloseBar> bars, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count < 2 || period < 2)
        {
            return TechnicalIndicatorSnapshot.Empty(bars.Count > 0 ? bars[^1].ClosePrice.Value : 0m);
        }

        var n = bars.Count;
        var effectivePeriod = Math.Min(period, n - 1);
        var startIdx = n - effectivePeriod;
        var latestClose = bars[^1].ClosePrice.Value;

        // 1. True OHLC ATR & Wilder's Directional Movement (+DI, -DI, ADX)
        decimal trSum = 0m;
        decimal plusDmSum = 0m;
        decimal minusDmSum = 0m;
        decimal gainSum = 0m;
        decimal lossSum = 0m;

        for (var i = startIdx; i < n; i++)
        {
            var curr = bars[i];
            var prev = bars[i - 1];
            var highLow = curr.HighPrice.Value - curr.LowPrice.Value;
            var highPrevClose = Math.Abs(curr.HighPrice.Value - prev.ClosePrice.Value);
            var lowPrevClose = Math.Abs(curr.LowPrice.Value - prev.ClosePrice.Value);
            var tr = Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
            trSum += tr;

            var upMove = curr.HighPrice.Value - prev.HighPrice.Value;
            var downMove = prev.LowPrice.Value - curr.LowPrice.Value;
            if (upMove > downMove && upMove > 0m)
            {
                plusDmSum += upMove;
            }

            if (downMove > upMove && downMove > 0m)
            {
                minusDmSum += downMove;
            }

            var change = curr.ClosePrice.Value - prev.ClosePrice.Value;
            if (change > 0m)
            {
                gainSum += change;
            }
            else if (change < 0m)
            {
                lossSum += -change;
            }
        }

        var atr = trSum / effectivePeriod;
        var atrBps = latestClose > 0m ? Math.Round(atr / latestClose * 10_000m, 4) : 0m;
        var plusDi = trSum > 0m ? Math.Clamp(Math.Round(plusDmSum / trSum * 100m, 2), 0m, 100m) : 0m;
        var minusDi = trSum > 0m ? Math.Clamp(Math.Round(minusDmSum / trSum * 100m, 2), 0m, 100m) : 0m;
        var diSum = plusDi + minusDi;
        var adx = diSum > 0m ? Math.Clamp(Math.Round(Math.Abs(plusDi - minusDi) / diSum * 100m, 2), 0m, 100m) : 0m;

        // 2. Wilder's Relative Strength Index (RSI)
        var avgGain = gainSum / effectivePeriod;
        var avgLoss = lossSum / effectivePeriod;
        var rsi = avgLoss <= 0m
            ? (avgGain > 0m ? 100m : 50m)
            : Math.Clamp(Math.Round(100m - (100m / (1m + (avgGain / avgLoss))), 2), 0m, 100m);

        // 3. Rolling Window VWAP, Relative Volume (RVOL), CVD Imbalance & VPIN
        var vwapWindow = Math.Min(20, n);
        var vwapStart = n - vwapWindow;
        decimal pvSum = 0m;
        decimal volSum = 0m;
        decimal typicalSum = 0m;
        decimal priorVolSum = 0m;
        decimal cvdDeltaSum = 0m;
        decimal absOrderImbalanceSum = 0m;
        decimal aggressorVolSum = 0m;

        for (var i = vwapStart; i < n; i++)
        {
            var b = bars[i];
            var typicalPrice = (b.HighPrice.Value + b.LowPrice.Value + b.ClosePrice.Value) / 3m;
            typicalSum += typicalPrice;
            pvSum += b.QuoteVolume > 0m ? b.QuoteVolume : typicalPrice * b.Volume;
            volSum += b.Volume;
            if (i < n - 1)
            {
                priorVolSum += b.Volume;
            }

            var hasAggressorTelemetry = b.TakerBuyBaseVolume > 0m || b.QuoteVolume > 0m || b.NumberOfTrades > 0;
            if (hasAggressorTelemetry && b.Volume > 0m)
            {
                var buyVol = Math.Clamp(b.TakerBuyBaseVolume, 0m, b.Volume);
                var sellVol = b.Volume - buyVol;
                var delta = buyVol - sellVol;
                cvdDeltaSum += delta;
                absOrderImbalanceSum += Math.Abs(delta);
                aggressorVolSum += b.Volume;
            }
        }

        var vwap = volSum > 0m ? pvSum / volSum : typicalSum / vwapWindow;
        var vwapDeviationBps = vwap > 0m ? Math.Round((latestClose - vwap) / vwap * 10_000m, 4) : 0m;
        var priorAvgVol = vwapWindow > 1 ? priorVolSum / (vwapWindow - 1) : 0m;
        var rvol = priorAvgVol > 0m && bars[^1].Volume > 0m
            ? Math.Clamp(Math.Round(bars[^1].Volume / priorAvgVol, 4), 0.05m, 10.0m)
            : 1.0m;
        var cvdImbalance = aggressorVolSum > 0m
            ? Math.Clamp(Math.Round(cvdDeltaSum / aggressorVolSum, 4), -1m, 1m)
            : 0m;
        var vpin = aggressorVolSum > 0m
            ? Math.Clamp(Math.Round(absOrderImbalanceSum / aggressorVolSum, 4), 0m, 1m)
            : 0m;

        return new TechnicalIndicatorSnapshot(
            Math.Round(atr, 8),
            atrBps,
            adx,
            plusDi,
            minusDi,
            rsi,
            Math.Round(vwap, 8),
            vwapDeviationBps,
            rvol,
            cvdImbalance,
            vpin);
    }
}
