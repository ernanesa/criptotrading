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
