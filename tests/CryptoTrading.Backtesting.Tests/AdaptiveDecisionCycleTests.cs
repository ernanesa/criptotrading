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
            EvaluatedAtUtc: TestTime)
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
            EvaluatedAtUtc: TestTime)
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
            EvaluatedAtUtc: TestTime)
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
            EvaluatedAtUtc: TestTime)
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
            EvaluatedAtUtc: TestTime)
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
        // 24 bars: position entered earlier, swings up to 84_350 (+41.6 bps gain from cost basis 84_000),
        // then pulls back to 84_210 (+25.0 bps gain, a 16.6 bps pullback >= 15.0 bps trailing gap)
        var bars = Bars(
            84_000m, 84_020m, 84_050m, 84_080m, 84_120m, 84_150m,
            84_180m, 84_220m, 84_260m, 84_300m, 84_330m, 84_350m,
            84_340m, 84_320m, 84_300m, 84_280m, 84_260m, 84_240m,
            84_230m, 84_220m, 84_215m, 84_210m, 84_210m, 84_210m);
        // Cost basis 10.0000 USDT at 84_000 -> held qty = 10 / 84000 = 0.0001190476 BTC
        // Current price 84_210 -> gross exposure = 0.0001190476 * 84210 = 10.0250 USDT (+25.0 bps gain)
        var portfolio = CreatePortfolio(10.0250m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_209m),
                new Price(84_211m),
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
        // Position entered at 11.6000 and current notional is 11.6220 (+18.96 bps gain, above roundTripFeeBps + 3)
        var portfolio = CreatePortfolio(11.6220m) with { PositionCostNotional = 11.6000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime)
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
            84_000m, 84_020m, 84_050m, 84_080m, 84_120m, 84_150m,
            84_180m, 84_220m, 84_260m, 84_300m, 84_350m // peak at index 10
        };
        for (var i = 0; i < 24; i++)
        {
            prices.Add(84_210m);
        }

        var bars = Bars(prices.ToArray());
        var portfolio = CreatePortfolio(10.0250m) with { PositionCostNotional = 10.0000m };
        var request = new AdaptiveDecisionCycleRequest(
            bars,
            portfolio,
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 12m,
            EvaluatedAtUtc: TestTime,
            LifetimePeakProfitBps: 41.6m) // 41.6 bps peak preserved across lifetime
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(84_209m),
                new Price(84_211m),
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
        var portfolio = CreatePortfolio(10.0250m) with { PositionCostNotional = 10.0000m };
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
                new Price(84_209m),
                new Price(84_211m),
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
