using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MultiStrategyDecisionOrchestratorTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void OrchestratorYieldsNoTradeWhenHistoryIsInsufficient()
    {
        var bars = Bars(100m, 101m, 102m);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.True(result.IsNoTrade);
        Assert.Equal(MarketRegime.IlliquidOrStale, result.Regime.Regime);
        Assert.Contains("INSUFFICIENT", result.OrchestrationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorYieldsNoTradeWhenRegimeIsHighVolatilityChop()
    {
        // Extreme swings trigger Chop regime: 100, 150, 70, 160, 60, 170
        var bars = Bars(100m, 150m, 70m, 160m, 60m, 170m);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.True(result.IsNoTrade);
        Assert.Equal(MarketRegime.HighVolatilityChop, result.Regime.Regime);
        Assert.Contains("prohibits new exposure", result.OrchestrationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorSelectsMeanReversionInRangeWhenOversold()
    {
        // 5 bars at 100, then dip to 95. In a range regime, MeanReversion should trigger EnterLong.
        var bars = Bars(100m, 100m, 100m, 100m, 100m, 95m);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.False(result.IsNoTrade);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.Equal(OrderSide.Buy, result.SelectedSide);
        Assert.Equal(1000m, result.SelectedNotional);
    }

    [Fact]
    public void OrchestratorSelectsBreakoutInBullTrend()
    {
        // High of past bars is 103, last bar breaks out to 106. Upward slope triggers BullTrend.
        var bars = Bars(100m, 101m, 102m, 103m, 102m, 106m);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.False(result.IsNoTrade);
        Assert.Equal(OrderSide.Buy, result.SelectedSide);
        Assert.Contains("SELECTED", result.OrchestrationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorAllowsMeanReversionAfterStabilizedRangeFollowingOlderSelloff()
    {
        var prices = new List<decimal>(99);
        for (var i = 0; i < 50; i++)
        {
            prices.Add(82900m - (i * 13m));
        }

        for (var i = 0; i < 48; i++)
        {
            prices.Add(82250m + ((i % 2 == 0) ? 2m : -2m));
        }

        prices.Add(82215m);

        var bars = Bars(prices.ToArray());
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.False(result.IsNoTrade);
        Assert.Equal(MarketRegime.MeanRevertingRange, result.Regime.Regime);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.Equal(OrderSide.Buy, result.SelectedSide);
    }

    [Fact]
    public void OrchestratorAllowsMeanReversionOnHigherLowPullbackInRecoveringRange()
    {
        var prices = new List<decimal>(36);
        for (var i = 0; i < 16; i++)
        {
            prices.Add(83150m - (i * 17m));
        }

        decimal[] recovery =
        [
            82895m, 82902m, 82908m, 82914m, 82920m,
            82924m, 82928m, 82932m, 82936m, 82940m,
            82945m, 82950m, 82955m, 82960m, 82965m,
            82970m, 82970m, 82970m, 82968m, 82930m,
        ];
        prices.AddRange(recovery);

        var bars = Bars(prices.ToArray());
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.False(result.IsNoTrade);
        Assert.Equal(MarketRegime.MeanRevertingRange, result.Regime.Regime);
        Assert.True(result.Regime.TrendSlopeRate >= 0.000015m);
        Assert.Equal("mean-reversion-btcusdt", result.SelectedStrategyId);
        Assert.Equal(OrderSide.Buy, result.SelectedSide);
    }

    [Fact]
    public void OrchestratorBlocksMeanReversionOnSingleBarShockWithNegativeSlope()
    {
        decimal[] prices =
        [
            82620m, 82615m, 82610m, 82605m, 82600m,
            82595m, 82590m, 82585m, 82580m, 82575m,
            82570m, 82568m, 82572m, 82574m, 82576m,
            82578m, 82580m, 82587m, 82616m, 82546m,
        ];

        var bars = Bars(prices);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime);

        Assert.True(result.IsNoTrade);
        Assert.Contains(
            result.Candidates,
            c => c.StrategyId == "mean-reversion-btcusdt" && c.Reason == "FallingKnifeBreakdownBlocked");
    }

    [Fact]
    public void OrchestratorEmitsProtectiveTrendExitWhenExposedInBearTrend()
    {
        var prices = new List<decimal>(25);
        for (var i = 0; i < 25; i++)
        {
            prices.Add(83000m - (i * 15m));
        }

        var bars = Bars(prices.ToArray());
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime, hasOpenPosition: true);

        Assert.False(result.IsNoTrade);
        Assert.Equal(MarketRegime.BearTrend, result.Regime.Regime);
        Assert.Equal(OrderSide.Sell, result.SelectedSide);
        Assert.Contains(
            result.Candidates,
            c => c.StrategyId == "trend-following-btcusdt" && c.RecommendedSide == OrderSide.Sell);
    }

    [Fact]
    public void OrchestratorSuppressesBreakoutEntryWhenQuarantined()
    {
        // High of past bars is 103, last bar breaks out to 106.
        var bars = Bars(100m, 101m, 102m, 103m, 102m, 106m);
        var quarantined = new HashSet<string>(["breakout-momentum"], StringComparer.OrdinalIgnoreCase);
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(BtcUsdt, bars, 1000m, TestTime, quarantinedStrategies: quarantined);

        Assert.True(result.IsNoTrade);
        var breakoutCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "breakout-momentum-btcusdt");
        Assert.Null(breakoutCandidate.RecommendedSide);
        Assert.Equal("StrategyQuarantinedForNegativeExpectancy", breakoutCandidate.Reason);
    }

    [Fact]
    public void OrchestratorSuppressesExitWhenPositionOwnedByAnotherStrategy()
    {
        var prices = new List<decimal>(25);
        for (var i = 0; i < 25; i++)
        {
            prices.Add(83000m - (i * 15m));
        }

        var bars = Bars(prices.ToArray());
        // Position was opened by "mean-reversion-btcusdt", so trend-following exit is suppressed
        var result = MultiStrategyDecisionOrchestrator.Orchestrate(
            BtcUsdt,
            bars,
            1000m,
            TestTime,
            hasOpenPosition: true,
            positionOriginatingStrategyId: "mean-reversion-btcusdt");

        var trendCandidate = Assert.Single(result.Candidates, c => c.StrategyId == "trend-following-btcusdt");
        Assert.Null(trendCandidate.RecommendedSide);
        Assert.Equal("PositionOwnedByAnotherStrategy", trendCandidate.Reason);
    }

    private static List<CloseBar> Bars(params decimal[] prices)
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);
        var list = new List<CloseBar>(prices.Length);
        for (var i = 0; i < prices.Length; i++)
        {
            list.Add(new CloseBar(BtcUsdt, start.AddMinutes(i), prices[i]));
        }

        return list;
    }
}
