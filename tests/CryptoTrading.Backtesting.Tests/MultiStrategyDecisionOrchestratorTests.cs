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
