using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class AdaptiveDecisionCycleTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    private static RiskPolicy CreateRiskPolicy() =>
        new(
            version: "v1.0.0-test",
            maxOrderNotional: 1000m,
            maxExposureNotional: 5000m,
            maxDailyLoss: 200m,
            maxDrawdown: 500m,
            maxMarketDataAge: TimeSpan.FromMinutes(5),
            withdrawalsEnabled: false,
            maxSlippageBps: 50m,
            maxVolatilityBps: 300m,
            minLiquidityNotional: 1000m);

    private static PortfolioSnapshot CreatePortfolio(decimal grossExposure = 0m) =>
        new(
            Balances: new Dictionary<Asset, Money>
            {
                [new Asset("USDT")] = new(10_000m, new Asset("USDT")),
                [new Asset("BTC")] = new(grossExposure / 60_000m, new Asset("BTC")),
            },
            GrossExposureNotional: grossExposure,
            DailyLossNotional: 0m,
            Drawdown: 0m,
            PositionCostNotional: grossExposure,
            RiskContextComplete: true);

    [Fact]
    public void FailsClosedWhenMarketIsInHighVolatilityChop()
    {
        var cycle = new AdaptiveDecisionCycle();
        // High volatility chop prices
        var bars = Bars(100m, 160m, 60m, 170m, 50m, 180m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal(MarketRegime.HighVolatilityChop, result.Regime.Regime);
        Assert.Contains("REGIME_PROHIBITS_NEW_EXPOSURE", result.NoTradeReason, StringComparison.Ordinal);
        Assert.Null(result.Intent);
        Assert.Contains("HighVolatilityChop", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void HighVolatilityChopForcesRiskReducingExitForOpenPosition()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            100m, 160m, 60m, 170m, 50m, 180m,
            55m, 175m, 45m, 165m, 50m, 155m,
            60m, 150m, 55m, 145m, 60m, 140m,
            65m, 135m, 70m, 130m, 75m, 125m);
        var portfolio = CreatePortfolio(95m) with { PositionCostNotional = 100m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 95m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(124m),
                new Price(126m),
                10_000m,
                TestTime,
                quantityStepSize: 0.0001m,
                priceTickSize: 0.01m,
                bidQuantity: 10m,
                askQuantity: 10m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(MarketRegime.HighVolatilityChop, result.Regime.Regime);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.NotNull(result.Intent);
        Assert.Equal(OrderType.Limit, result.Intent!.Type);
        Assert.Contains("HIGH_VOLATILITY_CHOP_RISK_EXIT", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FailsClosedWhenOperationalControlBlocksEntries()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(100m, 101m, 102m, 103m, 104m, 105m);
        var safeModeControl = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: TestTime,
            ChangedBy: "supervisor",
            Reason: "HEALTH_CHECK_FAILED");

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            safeModeControl,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Contains("OPERATIONAL_CONTROL_BLOCKED:SafeMode", result.NoTradeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeModeAllowsRiskReducingExitWhenPositionIsOpen()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        var portfolio = CreatePortfolio(11.7880m) with { PositionCostNotional = 11.6500m };
        var safeModeControl = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: TestTime,
            ChangedBy: "supervisor",
            Reason: "HEALTH_CHECK_FAILED");

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            safeModeControl,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.10m,
                askQuantity: 0.90m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
    }

    [Fact]
    public void KillSwitchAlwaysBlocksExitEvenWhenPositionIsOpen()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        var portfolio = CreatePortfolio(11.7880m) with { PositionCostNotional = 11.6500m };
        var killSwitchControl = new OperationalControlSnapshot(
            OperationalMode.KillSwitch,
            Revision: 1,
            ChangedAtUtc: TestTime,
            ChangedBy: "operator",
            Reason: "EMERGENCY_HALT");

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            killSwitchControl,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.10m,
                askQuantity: 0.90m),
        };

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Contains("OPERATIONAL_CONTROL_BLOCKED:KillSwitch", result.NoTradeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectsMeanReversionInRangeWhenOversoldAndExecutesDispatchableOrder()
    {
        var cycle = new AdaptiveDecisionCycle();
        // Flat bars then dip to 95 -> oversold range
        var request = CreateDispatchableRequest();

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.NotNull(result.Intent);
        Assert.Equal(OrderSide.Buy, result.RecommendedSide);
        Assert.True(result.Execution!.IsDispatchable);
        Assert.Equal(.004m, result.Execution!.Command!.EstimatedCommissionRate);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.NotNull(result.TacticRecommendation);
        Assert.Contains("mean-reversion-btcusdt", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FailsClosedWhenExecutionPlannerRejectsIncompleteMarketData()
    {
        var request = CreateDispatchableRequest() with
        {
            ExecutionMarket = new ExecutionMarketSnapshot(new Price(99m), new Price(101m), 10_000m, TestTime, isComplete: false),
        };

        AssertPlannerRejectionFailsClosed(request, ExecutionPlanner.MarketDataUnavailable);
    }

    [Fact]
    public void FailsClosedWhenExecutionPlannerRejectsStaleMarketData()
    {
        var request = CreateDispatchableRequest() with
        {
            ExecutionMarket = new ExecutionMarketSnapshot(new Price(99m), new Price(101m), 10_000m, TestTime.AddMinutes(-6)),
        };

        AssertPlannerRejectionFailsClosed(request, ExecutionPlanner.MarketDataStale);
    }

    [Fact]
    public void FailsClosedWhenExecutionPlannerRejectsInsufficientLiquidity()
    {
        var request = CreateDispatchableRequest() with
        {
            ExecutionMarket = new ExecutionMarketSnapshot(new Price(99m), new Price(101m), 1m, TestTime),
        };

        AssertPlannerRejectionFailsClosed(request, ExecutionPlanner.LiquidityUnavailable);
    }

    [Fact]
    public void FailsClosedWhenExecutionPlannerRejectsStaleCommission()
    {
        var request = CreateDispatchableRequest() with
        {
            EffectiveCommission = CreateCommission(TestTime.AddMinutes(-2)),
        };

        AssertPlannerRejectionFailsClosed(request, ExecutionPlanner.CommissionUnavailable);
    }

    [Fact]
    public void SelectsBreakoutInStrongBullTrend()
    {
        var cycle = new AdaptiveDecisionCycle();
        // Clear breakout pattern in bull trend
        var bars = Bars(100m, 101m, 102m, 103m, 102m, 107m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.NotNull(result.Intent);
        Assert.Equal(OrderSide.Buy, result.RecommendedSide);
        Assert.Equal(MarketRegime.BullTrend, result.Regime.Regime);
        Assert.NotNull(result.NettingProposal);
        Assert.True(result.NettingProposal.NettedQuantity > 0m);
    }

    [Fact]
    public void SuppressesBreakoutEntryWhenQuarantinedInBullTrend()
    {
        var cycle = new AdaptiveDecisionCycle(quarantinedStrategies: ["breakout-momentum"]);
        var bars = Bars(100m, 101m, 102m, 103m, 102m, 106m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        var candidate = Assert.Single(result.Candidates, c => c.StrategyId == "breakout-momentum-btcusdt");
        Assert.Null(candidate.RecommendedSide);
        Assert.Equal("StrategyQuarantinedForNegativeExpectancy", candidate.Reason);
    }

    [Fact]
    public void BlocksMeanReversionFallingKnifeEntryDuringIntradayBreakdown()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 24 intraday 1m bars drifting down and breaking below the 6-bar Donchian channel low on the final bar
        var bars = Bars(
            84_300m, 84_290m, 84_280m, 84_270m, 84_260m, 84_250m,
            84_240m, 84_230m, 84_220m, 84_210m, 84_200m, 84_190m,
            84_180m, 84_170m, 84_160m, 84_150m, 84_140m, 84_130m,
            84_120m, 84_115m, 84_110m, 84_108m, 84_105m, 84_000m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        var meanRevCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "mean-reversion-btcusdt");
        Assert.Null(meanRevCandidate.RecommendedSide);
        Assert.Equal("FallingKnifeBreakdownBlocked", meanRevCandidate.Reason);
    }

    [Fact]
    public void BlocksBreakoutEntryInMeanRevertingRangeOnIntradayWindow()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 24 intraday 1m bars oscillating in a flat MeanRevertingRange and poking slightly above the 12-bar high on the final bar
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_199m, 84_200m, 84_201m, 84_200m, 84_201m, 84_205m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal(MarketRegime.MeanRevertingRange, result.Regime.Regime);
        var breakoutCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "breakout-momentum-btcusdt");
        Assert.Null(breakoutCandidate.RecommendedSide);
        Assert.Equal("BreakoutEntryBlockedOutsideBullTrend", breakoutCandidate.Reason);
    }

    [Fact]
    public void AllowsNormalMeanReversionDipInRangeOnIntradayWindow()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 24 intraday 1m bars in a flat MeanRevertingRange with a normal ~3.5 bps oversold dip on the final bar
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(MarketRegime.MeanRevertingRange, result.Regime.Regime);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.Equal(OrderSide.Buy, result.RecommendedSide);
    }

    [Fact]
    public void BlocksEntryWhenOrderBookImbalanceIsSeverelyAdverseOnIntradayWindow()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_169m),
                new Price(84_171m),
                10_000m,
                TestTime,
                bidQuantity: 0.05m,
                askQuantity: 1.95m),
        };

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("ADVERSE_ORDER_BOOK_IMBALANCE", result.NoTradeReason);
        Assert.Contains("\"OrderBookImbalance\":-0.95", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BlocksEntryWhenModerateOrderBookImbalanceAndLowMlWinProbabilityAlign()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_169m),
                new Price(84_171m),
                10_000m,
                TestTime,
                bidQuantity: 0.24m,
                askQuantity: 0.76m),
        };

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("ADVERSE_ORDER_BOOK_IMBALANCE", result.NoTradeReason);
        Assert.Contains("\"OrderBookImbalance\":-0.52", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsEntryWhenMlWinProbabilityIsBelowFiftyPercentFloor()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_169m),
                new Price(84_171m),
                10_000m,
                TestTime,
                bidQuantity: 0.35m,
                askQuantity: 0.65m),
        };

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("LOW_DECISION_CONFIDENCE", result.NoTradeReason);
    }

    [Fact]
    public void LocksProfitWhenAdverseOrderBookImbalanceHitsProfitableOpenPosition()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        var portfolio = CreatePortfolio(11.7880m) with { PositionCostNotional = 11.6500m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.10m,
                askQuantity: 0.90m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
    }

    [Fact]
    public void ScalesUpEntryNotionalOnHighConvictionKellyConfluence()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_169m),
                new Price(84_171m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.90m,
                askQuantity: 0.10m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Buy, result.RecommendedSide);
        Assert.True(result.RecommendedNotional > 12m);
    }

    [Fact]
    public void LocksProfitWhenExpandedTargetReachedAndMomentumSoftens()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        // Position entered at 11.6000 and current notional is 11.6560 (+48.27 bps gain >= 45 bps target)
        var portfolio = CreatePortfolio(11.6560m) with { PositionCostNotional = 11.6000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.40m,
                askQuantity: 0.60m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
    }

    [Fact]
    public void LocksProfitOnTrailingStopChandelierExitWhenPricePullbacksFromPeak()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 24 bars: position entered earlier, swings up to 85_200 (+142.8 bps gain from cost basis 84_000),
        // then pulls back to 84_500 (+59.5 bps gain, a 83.3 bps pullback >= trailing gap)
        var bars = Bars(
            84_000m, 84_050m, 84_150m, 84_300m, 84_500m, 84_700m,
            84_900m, 85_050m, 85_150m, 85_200m, 85_100m, 85_000m,
            84_900m, 84_800m, 84_700m, 84_650m, 84_600m, 84_500m,
            84_500m, 84_500m, 84_500m, 84_500m, 84_500m, 84_500m);
        // Current price 84_500 -> gross exposure = 10.0595 USDT (+59.5 bps gain)
        var portfolio = CreatePortfolio(10.0595m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_499m),
                new Price(84_501m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.50m,
                askQuantity: 0.50m), // Neutral imbalance: trailing stop triggers purely on price pullback
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.Contains("TRAILING_STOP_CHANDELIER_EXIT", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LocksProfitAtBreakEvenWhenMomentumSoftens()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        // Position entered at 11.6000 and current notional is 11.6400 (+34.5 bps gain).
        // The break-even floor must cover fees plus slippage, not merely fee + 2 bps.
        var portfolio = CreatePortfolio(11.6400m) with { PositionCostNotional = 11.6000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.30m,
                askQuantity: 0.70m), // Imbalance = -0.40m <= -0.35m
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
    }

    [Fact]
    public void EmitsConfluenceEntryWhenMlWinProbabilityAndOrderBookAreStrong()
    {
        var cycle = new AdaptiveDecisionCycle();
        var prices = Enumerable.Range(0, 25).Select(i => 100m + (i * 0.50m)).ToArray();
        var bars = Bars(prices);
        var request = CreateDispatchableRequest() with
        {
            Bars = bars,
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(106.0m), new Price(106.1m), 10_000m, TestTime,
                isComplete: true, quantityStepSize: 0.0001m, priceTickSize: 0.01m,
                bidQuantity: 18.0m, askQuantity: 2.0m)
        };

        var result = cycle.Evaluate(request);
        Assert.False(result.IsNoTrade);
        Assert.NotNull(result.Execution);
        Assert.Equal(OrderSide.Buy, result.RecommendedSide);
        Assert.Equal("trend-following-btcusdt", result.SelectedStrategyId);
        Assert.Contains("ML_ORDERBOOK_CONFLUENCE_ENTRY", result.DecisionTraceJson, StringComparison.Ordinal);
        var trendCandidate = result.Candidates.First(c => c.StrategyId == "trend-following-btcusdt");
        Assert.Equal(OrderSide.Buy, trendCandidate.RecommendedSide);
        Assert.Equal("MlOrderBookConfluence", trendCandidate.Reason);
    }

    [Fact]
    public void ConfluenceEntryIsSuppressedWhenTrendFollowingIsQuarantined()
    {
        var cycle = new AdaptiveDecisionCycle(quarantinedStrategies: ["trend-following-btcusdt"]);
        var prices = Enumerable.Range(0, 25).Select(i => 100m + (i * 0.50m)).ToArray();
        var bars = Bars(prices);
        var request = CreateDispatchableRequest() with
        {
            Bars = bars,
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(106.0m), new Price(106.1m), 10_000m, TestTime,
                isComplete: true, quantityStepSize: 0.0001m, priceTickSize: 0.01m,
                bidQuantity: 18.0m, askQuantity: 2.0m)
        };

        var result = cycle.Evaluate(request);
        Assert.True(result.IsNoTrade);
        Assert.DoesNotContain("ML_ORDERBOOK_CONFLUENCE_ENTRY", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfluenceEntryIsSuppressedWhenRegimeIsBearTrend()
    {
        var cycle = new AdaptiveDecisionCycle();
        // Declining prices produce a BearTrend
        var prices = Enumerable.Range(0, 25).Select(i => 120m - (i * 0.50m)).ToArray();
        var bars = Bars(prices);
        var request = CreateDispatchableRequest() with
        {
            Bars = bars,
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(107.0m), new Price(107.1m), 10_000m, TestTime,
                isComplete: true, quantityStepSize: 0.0001m, priceTickSize: 0.01m,
                bidQuantity: 18.0m, askQuantity: 2.0m)
        };

        var result = cycle.Evaluate(request);
        Assert.True(result.IsNoTrade);
        Assert.DoesNotContain("ML_ORDERBOOK_CONFLUENCE_ENTRY", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    private static AdaptiveDecisionCycleRequest CreateDispatchableRequest() => new(
        Bars(100m, 100m, 100m, 100m, 100m, 95m),
        CreatePortfolio(),
        CreateRiskPolicy(),
        OperationalControlSnapshot.Initial,
        EntryTargetNotional: 500m,
        EvaluatedAtUtc: TestTime)
    {
        ExecutionMarket = new ExecutionMarketSnapshot(new Price(99m), new Price(101m), 10_000m, TestTime),
        ExecutionMarketMaxAge = TimeSpan.FromMinutes(5),
        AvailableAccountLiquidityNotional = 10_000m,
        EffectiveCommission = CreateCommission(TestTime),
        CommissionMaximumAge = TimeSpan.FromMinutes(1),
        UseConservativeCommissionReserve = true,
    };

    [Fact]
    public void LocksProfitOnTrailingStopWithLifetimePeakAcrossWindowConsolidation()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 35 bars: position entered at 84_000, spiked to 84_350 at bar index 10 (>20 bars ago),
        // followed by 24 bars of top consolidation and slight pullback to 84_210 (+25.0 bps gain)
        var prices = new List<decimal>
        {
            84_000m, 84_100m, 84_300m, 84_500m, 84_700m, 85_000m // peak at index 5
        };
        for (var i = 0; i < 24; i++)
        {
            prices.Add(84_350m);
        }

        var bars = Bars(prices.ToArray());
        var portfolio = CreatePortfolio(10.0416m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            LifetimePeakProfitBps: 119.0m) // 119.0 bps peak preserved across lifetime
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_349m),
                new Price(84_351m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.50m,
                askQuantity: 0.50m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Contains("TRAILING_STOP_CHANDELIER_EXIT", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributesExitToPositionOriginatingStrategy()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_000m, 84_020m, 84_050m, 84_080m, 84_120m, 84_150m,
            84_180m, 84_220m, 84_260m, 84_300m, 84_330m, 84_350m,
            84_340m, 84_320m, 84_300m, 84_280m, 84_260m, 84_240m,
            84_230m, 84_220m, 84_215m, 84_210m, 84_210m, 84_210m);
        var portfolio = CreatePortfolio(10.0400m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "trend-following-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_349m),
                new Price(84_351m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.50m,
                askQuantity: 0.50m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Equal("trend-following-btcusdt", result.SelectedStrategyId);
    }

    [Fact]
    public void DoesNotTriggerPhantomTrailingStopWhenPreEntryHighExceedsDipEntry()
    {
        var cycle = new AdaptiveDecisionCycle();
        // Pre-entry bars were at 85_000, dipped to 84_000 where position was opened,
        // and now sits at 84_160 (+19.0 bps gain). LifetimePeakProfitBps is 19.0 bps.
        // Without lifetimePeakProfitBps precedence, 85_000 would look like a +119 bps peak and trigger a phantom exit.
        var prices = new List<decimal>
        {
            85_000m, 84_900m, 84_800m, 84_700m, 84_600m,
            84_500m, 84_400m, 84_300m, 84_200m, 84_100m,
            84_050m, 84_040m, 84_030m, 84_020m, 84_010m,
            84_000m, 84_020m, 84_050m, 84_080m, 84_110m,
            84_130m, 84_140m, 84_150m, 84_160m
        };
        var bars = Bars(prices.ToArray());
        var portfolio = CreatePortfolio(10.0190m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "mean-reversion-btcusdt",
            LifetimePeakProfitBps: 19.0m)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_159m),
                new Price(84_161m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.60m,
                askQuantity: 0.40m),
        };

        var result = cycle.Evaluate(request);

        Assert.DoesNotContain("TRAILING_STOP_CHANDELIER_EXIT", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionMarketSnapshotCalculatesStoikovMicroPriceAndDivergence()
    {
        var snapshot = new ExecutionMarketSnapshot(
            new Price(10_000m),
            new Price(10_010m),
            50_000m,
            TestTime,
            bidQuantity: 3.0m,
            askQuantity: 1.0m);

        Assert.Equal(10_005m, snapshot.MidPrice);
        // MicroPrice = (3 * 10010 + 1 * 10000) / 4 = 40030 / 4 = 10007.5
        Assert.Equal(10_007.5m, snapshot.MicroPrice);
        // DivergenceBps = (10007.5 - 10005) / 10005 * 10000 = 2.4988 bps
        Assert.True(snapshot.MicroPriceDivergenceBps > 2.0m);
        Assert.Equal(0.50m, snapshot.OrderBookImbalance);
    }

    [Fact]
    public void ExecutionPlannerAppliesPennyJumpWhenStoikovMicroPriceDivergenceIsStrongAndSpreadHasHeadroom()
    {
        var market = new ExecutionMarketSnapshot(
            new Price(10_000m),
            new Price(10_005m),
            50_000m,
            TestTime,
            quantityStepSize: 0.001m,
            priceTickSize: 1.0m,
            bidQuantity: 4.0m,
            askQuantity: 1.0m);

        var riskReq = new RiskEvaluationRequest(100m, 0m, 0m, 0m, TestTime, TestTime);
        var intent = new OrderIntent(
            OrderId.New(),
            ClientOrderId.New(),
            BtcUsdt,
            OrderSide.Buy,
            OrderType.Limit,
            new Quantity(0.01m),
            new Price(10_000m),
            CorrelationId.New(),
            IdempotencyKey.New(),
            riskReq,
            TestTime);
        var decision = new RiskDecision(RiskDecisionStatus.Approved, 100m, 100m, "v1", [RiskReasonCodes.TargetWithinLimits]);
        var tactic = new ExecutionTacticDecision(
            ExecutionTactic.PassiveMakerPegged,
            UrgencyScore: 0.40m,
            ExpectedSlippageBps: 1.0m,
            NetExpectedBenefitBps: 5.0m,
            RequiresImmediateFill: false,
            Reason: "Maker pegged",
            PegOffsetTicks: 0);

        var planResult = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            intent,
            decision,
            market,
            TestTime,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(1),
            tactic: tactic));

        Assert.NotNull(planResult.Plan);
        // BestBid is 10_000, BestAsk is 10_005, tickSize is 1.0.
        // Because MicroPriceDivergenceBps > +0.25 bps and spreadTicks >= 2, Penny-Jump places limit order at 10_001!
        Assert.Equal(10_001m, planResult.Plan!.LimitPrice.Value);
    }

    [Fact]
    public void FailsClosedWhenPostTradeCooldownIsActive()
    {
        var cycle = new AdaptiveDecisionCycle();
        var prices = new decimal[25];
        for (var i = 0; i < 25; i++)
        {
            prices[i] = 84_000m + (i * 20m);
        }
        var bars = Bars(prices);

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime,
            LastClosedTradeAtUtc: TestTime.AddMinutes(-2)); // 2 minutes ago < 4 minutes cooldown

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("POST_TRADE_COOLDOWN_ACTIVE", result.NoTradeReason);
    }

    [Fact]
    public void AllowsEntryWhenPostTradeCooldownHasExpired()
    {
        var cycle = new AdaptiveDecisionCycle();
        var prices = new decimal[25];
        for (var i = 0; i < 25; i++)
        {
            prices[i] = 84_000m + (i * 20m);
        }
        var bars = Bars(prices);

        var request = CreateDispatchableRequest() with
        {
            Bars = bars,
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_480m), new Price(84_481m), 10_000m, TestTime,
                isComplete: true, quantityStepSize: 0.0001m, priceTickSize: 0.01m,
                bidQuantity: 18.0m, askQuantity: 2.0m),
            LastClosedTradeAtUtc = TestTime.AddMinutes(-35) // 35 minutes ago >= 30 minutes cooldown
        };

        var result = cycle.Evaluate(request);

        // Result is not blocked by cooldown
        Assert.NotEqual("POST_TRADE_COOLDOWN_ACTIVE", result.NoTradeReason);
    }

    [Fact]
    public void BlocksMomentumEntryInMeanRevertingRangeWithoutConfluence()
    {
        var cycle = new AdaptiveDecisionCycle();
        // 25 bars in a flat range
        var prices = new decimal[25];
        for (var i = 0; i < 25; i++)
        {
            prices[i] = 84_000m + ((i % 2 == 0) ? 1m : -1m);
        }
        var bars = Bars(prices);

        var request = CreateDispatchableRequest() with
        {
            Bars = bars,
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_000m), new Price(84_001m), 10_000m, TestTime,
                isComplete: true, quantityStepSize: 0.0001m, priceTickSize: 0.01m,
                bidQuantity: 5.0m, askQuantity: 5.0m) // orderBookImbalance = 0.0m < 0.10m threshold
        };

        var result = cycle.Evaluate(request);

        // If a momentum entry attempted in MeanRevertingRange without book imbalance >= 0.10, blocked
        if (result.RecommendedSide == OrderSide.Buy &&
            result.SelectedStrategyId is not null &&
            !result.SelectedStrategyId.StartsWith("mean-reversion", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(result.IsNoTrade);
            Assert.Equal("RANGE_MOMENTUM_REQUIRES_ML_AND_BOOK_CONFIRMATION", result.NoTradeReason);
        }
    }

    [Fact]
    public void ChronologicalTripleBarrierResolvesStopLossBeforeTakeProfitOnSameBarAndLaterBars()
    {
        var start = TestTime.AddMinutes(-5);
        var entryBar = new CloseBar(BtcUsdt, start, 100m, openPrice: 100m, highPrice: 100m, lowPrice: 100m, volume: 10m);
        // Bar 1 hits stop-loss (-0.20%) and Bar 2 hits take-profit (+0.30%) => must resolve as false (stop-loss hit first chronologically)
        var bar1Stop = new CloseBar(BtcUsdt, start.AddMinutes(1), 99.85m, openPrice: 100m, highPrice: 100.05m, lowPrice: 99.75m, volume: 10m);
        var bar2Target = new CloseBar(BtcUsdt, start.AddMinutes(2), 100.40m, openPrice: 99.85m, highPrice: 100.45m, lowPrice: 99.85m, volume: 10m);
        var barsStopFirst = new List<CloseBar> { entryBar, bar1Stop, bar2Target };

        Assert.False(MlNetSystem1DecisionEngine.EvaluateChronologicalTripleBarrier(
            barsStopFirst, entryIndex: 0, horizon: 2, OrderSide.Buy, takeProfitBps: 25m, stopLossBps: 18m));

        // Bar 1 hits take-profit (+0.30%) cleanly before any stop-loss => must resolve as true
        var bar1CleanWin = new CloseBar(BtcUsdt, start.AddMinutes(1), 100.30m, openPrice: 100m, highPrice: 100.32m, lowPrice: 99.95m, volume: 10m);
        var barsTargetFirst = new List<CloseBar> { entryBar, bar1CleanWin, bar1Stop };
        Assert.True(MlNetSystem1DecisionEngine.EvaluateChronologicalTripleBarrier(
            barsTargetFirst, entryIndex: 0, horizon: 2, OrderSide.Buy, takeProfitBps: 25m, stopLossBps: 18m));
    }

    [Fact]
    public void TechnicalIndicatorCalculatorComputesOhlcAtrAdxRsiVwapAndRelativeVolume()
    {
        var start = TestTime.AddMinutes(-25);
        var bars = new List<CloseBar>(25);
        for (var i = 0; i < 25; i++)
        {
            var close = 100m + (i * 0.40m);
            bars.Add(new CloseBar(
                BtcUsdt,
                start.AddMinutes(i + 1),
                closePrice: close,
                openPrice: close - 0.20m,
                highPrice: close + 0.30m,
                lowPrice: close - 0.30m,
                volume: i == 24 ? 30m : 10m));
        }

        var snapshot = TechnicalIndicatorCalculator.Evaluate(bars);
        Assert.True(snapshot.Atr > 0.40m);
        Assert.True(snapshot.AtrBps > 40m);
        Assert.True(snapshot.Adx >= 20m);
        Assert.True(snapshot.PlusDi > snapshot.MinusDi);
        Assert.True(snapshot.Rsi > 60m);
        Assert.True(snapshot.Vwap > 100m && snapshot.Vwap < bars[^1].ClosePrice.Value);
        Assert.True(snapshot.RelativeVolume > 2.0m);
    }

    [Fact]
    public void ProtectiveStopLossExitFiresWhenPositionDropsBeyondAtrStopThreshold()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_200m, 84_200m);
        // Position cost 10.0000 USDT, current exposure 9.8800 USDT (-120.0 bps unrealized loss <= -100.0 bps stop)
        var portfolio = CreatePortfolio(9.8800m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 10m,
            EvaluatedAtUtc: TestTime,
            PositionOriginatingStrategyId: "trend-following-btcusdt")
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_199m),
                new Price(84_201m),
                10_000m,
                TestTime,
                quantityStepSize: 0.00001m,
                bidQuantity: 0.50m,
                askQuantity: 0.50m),
        };

        var result = cycle.Evaluate(request);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Sell, result.RecommendedSide);
        Assert.Contains("PROTECTIVE_STOP_LOSS_EXIT", result.DecisionTraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeExpectedNetEdgeBlocksEntryWhenSlippageAndFeesExceedPayoff()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime,
            MarketRisk: new MarketRiskSnapshot(
                expectedSlippageBps: 200m,
                volatilityBps: 10,
                availableLiquidityNotional: 10_000m,
                TestTime));

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("NEGATIVE_EXPECTED_NET_EDGE", result.NoTradeReason);
    }

    [Fact]
    public void BlocksEntryWhenToxicVpinAndNegativeCvdImbalanceAlign()
    {
        var cycle = new AdaptiveDecisionCycle();
        var prices = new[]
        {
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m,
        };
        var start = TestTime.AddMinutes(-prices.Length);
        var bars = prices
            .Select((p, idx) => new CloseBar(
                BtcUsdt,
                start.AddMinutes(idx + 1),
                p,
                openPrice: p,
                highPrice: p + 2m,
                lowPrice: p - 2m,
                volume: 100m,
                takerBuyBaseVolume: 5m, // 95% taker sell aggression -> VPIN = 0.90, CVD = -0.90
                quoteVolume: p * 100m,
                numberOfTrades: 250))
            .ToArray();

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("TOXIC_VPIN_FLOW_BLOCKED", result.NoTradeReason);
    }

    [Fact]
    public void BlocksEntryWhenMaxOpenPositionCorrelationExceedsHardLimit()
    {
        var cycle = new AdaptiveDecisionCycle();
        var bars = Bars(
            84_200m, 84_202m, 84_198m, 84_201m, 84_199m, 84_200m,
            84_201m, 84_199m, 84_200m, 84_202m, 84_198m, 84_200m,
            84_199m, 84_201m, 84_200m, 84_198m, 84_201m, 84_200m,
            84_201m, 84_200m, 84_201m, 84_200m, 84_201m, 84_170m);
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime,
            MaxOpenPositionCorrelation: 0.91m);

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("CORRELATED_EXPOSURE_LIMIT_EXCEEDED", result.NoTradeReason);
    }

    [Fact]
    public void CrossSectionalPortfolioAllocatorRanksByEdgeAndAppliesVolatilityParityAndCorrelationPenalty()
    {
        var barsA = Bars(100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m);
        var barsB = Bars(200m, 202m, 204m, 206m, 208m, 210m, 212m, 214m);
        var corr = CrossSectionalPortfolioAllocator.ComputePairwiseReturnCorrelation(barsA, barsB);
        Assert.True(corr >= 0.99m);

        var candidates = new[]
        {
            new CrossSectionalCandidate("ETHUSDT", "trend-following-ethusdt", ExpectedNetEdgeBps: 18m, AtrBps: 40m, BaseTargetNotional: 20m, MaxOpenPositionCorrelation: 0.20m),
            new CrossSectionalCandidate("BTCUSDT", "trend-following-btcusdt", ExpectedNetEdgeBps: 28m, AtrBps: 10m, BaseTargetNotional: 20m, MaxOpenPositionCorrelation: 0.10m),
            new CrossSectionalCandidate("SOLUSDT", "trend-following-solusdt", ExpectedNetEdgeBps: 22m, AtrBps: 20m, BaseTargetNotional: 20m, MaxOpenPositionCorrelation: 0.90m),
            new CrossSectionalCandidate("BNBUSDT", "trend-following-bnbusdt", ExpectedNetEdgeBps: -3m, AtrBps: 20m, BaseTargetNotional: 20m, MaxOpenPositionCorrelation: 0.10m),
        };

        var allocations = CrossSectionalPortfolioAllocator.AllocateCrossSectional(
            candidates,
            availableCapitalNotional: 100m,
            maxSingleOrderNotional: 35m,
            maxOpenPositions: 3,
            targetAtrBps: 20m);

        // Ranked 1st: BTCUSDT (Edge 28 bps, ATR 10 bps vs target 20 bps => 1.50x multiplier => 30 USDT)
        Assert.Equal("BTCUSDT", allocations[0].Symbol);
        Assert.True(allocations[0].IsApproved);
        Assert.Equal(1.50m, allocations[0].VolatilityParityMultiplier);
        Assert.Equal(30m, allocations[0].AllocatedNotional);

        // Ranked 2nd by edge: SOLUSDT, but blocked by correlation 0.90 >= 0.85
        Assert.Equal("SOLUSDT", allocations[1].Symbol);
        Assert.False(allocations[1].IsApproved);
        Assert.Equal("CORRELATED_EXPOSURE_LIMIT_EXCEEDED", allocations[1].RejectionReason);

        // Ranked 3rd: ETHUSDT (Edge 18 bps, ATR 40 bps vs target 20 bps => 0.50x multiplier => 10 USDT)
        Assert.Equal("ETHUSDT", allocations[2].Symbol);
        Assert.True(allocations[2].IsApproved);
        Assert.Equal(0.50m, allocations[2].VolatilityParityMultiplier);
        Assert.Equal(10m, allocations[2].AllocatedNotional);

        // Ranked 4th: BNBUSDT (Negative edge => blocked)
        Assert.Equal("BNBUSDT", allocations[3].Symbol);
        Assert.False(allocations[3].IsApproved);
        Assert.Equal("NEGATIVE_EXPECTED_NET_EDGE", allocations[3].RejectionReason);
    }

    [Fact]
    public void WalkForwardModelPromotionEvaluatesBrierScoreAndDeflatedSharpeRatio()
    {
        var strongEvaluations = Enumerable.Range(0, 24)
            .Select(i => (
                PredictedWinProbability: i % 4 == 0 ? 0.25m : 0.78m,
                RealizedOutcome: i % 4 != 0,
                RealizedNetReturnBps: i % 4 == 0 ? -8m : 24m))
            .ToArray();

        var promoted = MlNetSystem1DecisionEngine.EvaluateWalkForwardModelPromotion(
            strongEvaluations,
            independentTrials: 6,
            maxBrierScore: 0.25m,
            minDeflatedSharpeRatio: 0.65m,
            minSampleCount: 10);

        Assert.True(promoted.PromotionAuthorized);
        Assert.Null(promoted.RejectionReason);
        Assert.True(promoted.BrierScore < 0.20m);
        Assert.True(promoted.OutOfSampleSharpeRatio > 0.50m);
        Assert.True(promoted.DeflatedSharpeRatio >= 0.65m);

        var weakEvaluations = Enumerable.Range(0, 16)
            .Select(i => (
                PredictedWinProbability: 0.80m,
                RealizedOutcome: i % 3 == 0,
                RealizedNetReturnBps: i % 3 == 0 ? 10m : -15m))
            .ToArray();

        var rejected = MlNetSystem1DecisionEngine.EvaluateWalkForwardModelPromotion(weakEvaluations);
        Assert.False(rejected.PromotionAuthorized);
        Assert.NotNull(rejected.RejectionReason);
    }

    private static EffectiveCommissionSnapshot CreateCommission(DateTimeOffset observedAtUtc) => new(
        BtcUsdt, "testnet", "account",
        new CommissionRateSet(.004m, .002m, 0m, 0m),
        new CommissionRateSet(0m, 0m, 0m, 0m),
        new CommissionRateSet(0m, 0m, 0m, 0m),
        false, false, null, null, false, observedAtUtc);

    private static void AssertPlannerRejectionFailsClosed(
        AdaptiveDecisionCycleRequest request,
        string expectedReason)
    {
        var result = new AdaptiveDecisionCycle().Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Null(result.Intent);
        Assert.Null(result.Execution);
        Assert.Equal(expectedReason, result.NoTradeReason);
        Assert.Contains("\"Outcome\":\"NO_TRADE\"", result.DecisionTraceJson, StringComparison.Ordinal);
        Assert.Contains(expectedReason, result.DecisionTraceJson, StringComparison.Ordinal);
    }

    private static List<CloseBar> Bars(params decimal[] prices)
    {
        var start = TestTime.AddMinutes(-prices.Length);
        var list = new List<CloseBar>(prices.Length);
        for (var i = 0; i < prices.Length; i++)
        {
            list.Add(new CloseBar(BtcUsdt, start.AddMinutes(i + 1), prices[i]));
        }

        return list;
    }
}
