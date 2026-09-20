using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using System.Globalization;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class BaselineDecisionCycleTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void EntryCycleProducesDeterministicIntentAndRiskDecision()
    {
        var request = NewRequest(OperationalControlSnapshot.Initial);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m));

        var first = cycle.Evaluate(request);
        var second = cycle.Evaluate(request);

        Assert.False(first.IsNoTrade);
        Assert.NotNull(first.Intent);
        Assert.NotNull(first.Execution);
        Assert.True(first.Execution!.IsDispatchable);
        Assert.Equal(first.Intent!.ClientOrderId, second.Intent!.ClientOrderId);
        Assert.Equal(first.Intent.OrderId, second.Intent.OrderId);
        Assert.Equal("policy-1", first.Execution.Decision.PolicyVersion);
    }

    [Fact]
    public void SafeModeStillEvaluatesButDoesNotAuthorizeNewExposure()
    {
        var control = new OperationalControlSnapshot(OperationalMode.SafeMode, 4, Start, "test", "stale-data");
        var result = new BaselineDecisionCycle(
            new TrendFollowingParameters("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m))
            .Evaluate(NewRequest(control));

        Assert.NotNull(result.Intent);
        Assert.NotNull(result.Execution);
        Assert.False(result.Execution!.IsDispatchable);
        Assert.Contains(RiskReasonCodes.SafeMode, result.Execution.Decision.ReasonCodes);
    }

    [Fact]
    public void IncompleteAccountingContextBlocksAutonomousEntry()
    {
        var request = NewRequest(OperationalControlSnapshot.Initial) with
        {
            Portfolio = new PortfolioSnapshot(
                new Dictionary<Asset, Money>(),
                0m,
                0m,
                0m,
                RiskContextComplete: false,
                RiskContextUnavailableReason: "HIGH_WATER_MARK_UNAVAILABLE")
        };

        var result = new BaselineDecisionCycle(
            new TrendFollowingParameters("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m))
            .Evaluate(request);

        Assert.NotNull(result.Execution);
        Assert.False(result.Execution!.IsDispatchable);
        Assert.Contains(RiskReasonCodes.RiskContextUnavailable, result.Execution.Decision.ReasonCodes);
    }

    [Fact]
    public void FreshExecutionMarketEvidenceProducesPassiveLimitWithBalancedNotional()
    {
        var request = NewRequest(OperationalControlSnapshot.Initial) with
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(99m),
                new Price(101m),
                10_000m,
                Start.AddMinutes(3).AddSeconds(1)),
            ExecutionMarketMaxAge = TimeSpan.FromSeconds(5),
            ExecutionTimeToLive = TimeSpan.FromMinutes(1),
        };

        var result = new BaselineDecisionCycle(
            new TrendFollowingParameters("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m))
            .Evaluate(request);

        Assert.NotNull(result.Intent);
        Assert.Equal(OrderType.Limit, result.Intent!.Type);
        Assert.Equal(new Price(99m), result.Intent.ReferencePrice);
        Assert.NotNull(result.Execution);
        Assert.True(result.Execution!.IsDispatchable);
        Assert.Equal(
            result.Intent.Quantity.Value * result.Intent.ReferencePrice.Value,
            result.Execution.Command!.AuthorizedNotional);
    }

    [Fact]
    public void NoTradeSignalProducesNoIntent()
    {
        var bars = new[]
        {
            new CloseBar(BtcUsdt, Start, 100m),
            new CloseBar(BtcUsdt, Start.AddMinutes(1), 101m),
            new CloseBar(BtcUsdt, Start.AddMinutes(2), 102m)
        };
        var result = new BaselineDecisionCycle(
            new TrendFollowingParameters("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m))
            .Evaluate(NewRequest(OperationalControlSnapshot.Initial, bars));

        Assert.True(result.IsNoTrade);
        Assert.Equal("InsufficientHistory", result.NoTradeReason);
    }

    private static BaselineDecisionCycleRequest NewRequest(
        OperationalControlSnapshot control,
        IReadOnlyList<CloseBar>? bars = null) => new(
        bars ?? new[]
        {
            new CloseBar(BtcUsdt, Start, 100m),
            new CloseBar(BtcUsdt, Start.AddMinutes(1), 90m),
            new CloseBar(BtcUsdt, Start.AddMinutes(2), 80m),
            new CloseBar(BtcUsdt, Start.AddMinutes(3), 120m)
        },
        new PortfolioSnapshot(new Dictionary<Asset, Money>(), 0m, 0m, 0m),
        new RiskPolicy("policy-1", 1_000m, 2_000m, 500m, 500m, TimeSpan.FromMinutes(5)),
        control,
        100m,
        Start.AddMinutes(3).AddSeconds(1),
        new MarketRiskSnapshot(1m, 10m, 10_000m, Start.AddMinutes(3)));
}
