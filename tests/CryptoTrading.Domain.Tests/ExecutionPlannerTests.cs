using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExecutionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuyUsesBidAndProducesDeterministicPassivePlan()
    {
        var request = Request(OrderSide.Buy, attempt: 0);

        var first = ExecutionPlanner.Plan(request);
        var second = ExecutionPlanner.Plan(request);

        Assert.NotNull(first.Plan);
        Assert.Equal(first.Plan, second.Plan);
        Assert.Equal(new Price(100m), first.Plan!.LimitPrice);
        Assert.Equal(10m, first.Plan.Quantity.Value);
        Assert.Equal(Now.AddMinutes(2), first.Plan.ExpiresAtUtc);
    }

    [Fact]
    public void SellUsesAskAndAttemptChangesClientIdentity()
    {
        var first = ExecutionPlanner.Plan(Request(OrderSide.Sell, attempt: 0)).Plan;
        var replacement = ExecutionPlanner.Plan(Request(OrderSide.Sell, attempt: 1)).Plan;

        Assert.NotNull(first);
        Assert.NotNull(replacement);
        Assert.Equal(new Price(101m), first!.LimitPrice);
        Assert.NotEqual(first.ClientOrderId, replacement!.ClientOrderId);
        Assert.NotEqual(first.IdempotencyKey, replacement.IdempotencyKey);
        Assert.Equal(first.AuthorizedNotional, replacement.AuthorizedNotional);
    }

    [Theory]
    [InlineData(false, false, "EXECUTION_MARKET_DATA_UNAVAILABLE")]
    [InlineData(true, true, "EXECUTION_MARKET_DATA_STALE")]
    public void IncompleteOrStaleEvidenceFailsClosed(bool stale, bool complete, string reason)
    {
        var market = new ExecutionMarketSnapshot(
            new Price(100m),
            new Price(101m),
            2_000m,
            stale ? Now.AddMinutes(-10) : Now,
            isComplete: complete);
        var result = ExecutionPlanner.Plan(Request(OrderSide.Buy, market));

        Assert.True(result.IsNoTrade);
        Assert.Equal(reason, result.NoTradeReason);
    }

    [Fact]
    public void InsufficientLiquidityDoesNotPartiallyInventAnAuthorization()
    {
        var result = ExecutionPlanner.Plan(Request(
            OrderSide.Buy,
            new ExecutionMarketSnapshot(new Price(100m), new Price(101m), 99m, Now)));

        Assert.True(result.IsNoTrade);
        Assert.Equal(ExecutionPlanner.LiquidityUnavailable, result.NoTradeReason);
    }

    [Fact]
    public void BuyAtOrAboveFreeQuoteBalanceFailsClosed()
    {
        var result = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            Request(OrderSide.Buy).Intent,
            Request(OrderSide.Buy).Decision,
            Request(OrderSide.Buy).Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 1_000m));

        Assert.True(result.IsNoTrade);
        Assert.Equal(ExecutionPlanner.AccountLiquidityUnavailable, result.NoTradeReason);
    }

    [Fact]
    public void BuyRequiresStrictHeadroomAfterConfiguredFeeReserve()
    {
        var request = Request(OrderSide.Buy);
        var exactReserve = new ExecutionPlanRequest(
            request.Intent,
            request.Decision,
            request.Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 1_010m,
            feeReservePolicy: new FeeReservePolicy(100m));
        var enoughHeadroom = new ExecutionPlanRequest(
            request.Intent,
            request.Decision,
            request.Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 1_010.01m,
            feeReservePolicy: new FeeReservePolicy(100m));

        var blocked = ExecutionPlanner.Plan(exactReserve);
        var allowed = ExecutionPlanner.Plan(enoughHeadroom);

        Assert.True(blocked.IsNoTrade);
        Assert.Equal(ExecutionPlanner.AccountLiquidityUnavailable, blocked.NoTradeReason);
        Assert.False(allowed.IsNoTrade);
    }

    [Fact]
    public void SellDoesNotConsumeConfiguredQuoteFeeReserve()
    {
        var request = Request(OrderSide.Sell);
        var result = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            request.Intent,
            request.Decision,
            request.Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 0m,
            feeReservePolicy: new FeeReservePolicy(FeeReservePolicy.MaximumReserveBps)));

        Assert.False(result.IsNoTrade);
    }

    [Fact]
    public void FeeReservePolicyRejectsUnsafeConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeeReservePolicy(-1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeeReservePolicy(FeeReservePolicy.MaximumReserveBps + 1m));
        Assert.False(new FeeReservePolicy().TryCalculate(decimal.MaxValue, out _));
    }

    [Fact]
    public void SellDoesNotRequireQuoteBalance()
    {
        var request = Request(OrderSide.Sell);
        var result = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            request.Intent,
            request.Decision,
            request.Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 0m));

        Assert.False(result.IsNoTrade);
    }

    private static ExecutionPlanRequest Request(
        OrderSide side,
        ExecutionMarketSnapshot? market = null,
        int attempt = 0)
    {
        var intent = new OrderIntent(
            new OrderId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new ClientOrderId("ct-intent"),
            new Symbol("BTCUSDT"),
            side,
            OrderType.Limit,
            new Quantity(10m),
            new Price(100m),
            new CorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new IdempotencyKey("decision:test"),
            new RiskEvaluationRequest(1_000m, side == OrderSide.Sell ? 1_000m : 0m, 0m, 0m, Now, Now),
            Now);
        var decision = new RiskDecision(RiskDecisionStatus.Approved, 1_000m, 1_000m, "risk-1", [RiskReasonCodes.TargetWithinLimits]);
        return new(
            intent,
            decision,
            market ?? new ExecutionMarketSnapshot(new Price(100m), new Price(101m), 2_000m, Now),
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            attempt);
    }
}
