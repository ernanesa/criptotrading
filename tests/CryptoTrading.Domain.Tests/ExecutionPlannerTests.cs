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

    [Fact]
    public void EffectiveCommissionUsesTakerRateUnlessMakerEligibilityIsExplicit()
    {
        var request = Request(OrderSide.Buy);
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .003m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            false, false, null, null, false, Now);
        var conservative = new ExecutionPlanRequest(
            request.Intent, request.Decision, request.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission, commissionMaximumAge: TimeSpan.FromMinutes(1));
        var makerPermitted = new ExecutionPlanRequest(
            request.Intent, request.Decision, request.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission, commissionMaximumAge: TimeSpan.FromMinutes(1), makerFeeEligible: true);

        Assert.Equal(.003m, ExecutionPlanner.Plan(conservative).Plan!.EstimatedCommissionRate);
        Assert.Equal(.001m, ExecutionPlanner.Plan(makerPermitted).Plan!.EstimatedCommissionRate);
        Assert.Equal(1_000m, ExecutionPlanner.Plan(conservative).Plan!.AuthorizedNotional);
    }

    [Theory]
    [InlineData(OrderSide.Buy, OrderType.Market, "0.0088")]
    [InlineData(OrderSide.Buy, OrderType.Limit, "0.0088")]
    [InlineData(OrderSide.Sell, OrderType.Market, "0.01")]
    [InlineData(OrderSide.Sell, OrderType.Limit, "0.01")]
    public void ConservativeCommissionReserveUsesUndiscountedWorstCaseAndPreservesSnapshot(
        OrderSide side,
        OrderType orderType,
        string expectedRateText)
    {
        var baseline = Request(side);
        var original = baseline.Intent;
        var intent = new OrderIntent(
            original.OrderId,
            original.ClientOrderId,
            original.Symbol,
            original.Side,
            orderType,
            original.Quantity,
            original.ReferencePrice,
            original.CorrelationId,
            original.IdempotencyKey,
            original.RiskEvaluation,
            original.CreatedAtUtc);
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .003m, .004m, .005m),
            new CommissionRateSet(.0001m, .0002m, .0003m, .0004m),
            new CommissionRateSet(.0005m, .0006m, .0007m, .0008m),
            true, true, "BNB", .25m, false, Now,
            source: "test:commission",
            discountAssetQuotePrice: null,
            discountAssetValuationAtUtc: null,
            discountAssetValuationSource: null);
        var request = new ExecutionPlanRequest(
            intent,
            baseline.Decision,
            baseline.Market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            availableAccountLiquidityNotional: 2_000m,
            effectiveCommission: commission,
            commissionMaximumAge: TimeSpan.FromMinutes(1),
            useConservativeCommissionReserve: true);

        var plan = ExecutionPlanner.Plan(request).Plan;

        Assert.NotNull(plan);
        Assert.Equal(decimal.Parse(expectedRateText, System.Globalization.CultureInfo.InvariantCulture), plan!.EstimatedCommissionRate);
        Assert.Equal(commission.CanonicalPayloadHash, plan.CommissionSnapshotHash);
        Assert.Equal("test:commission", plan.CommissionSource);
        Assert.Equal(commission.ObservedAtUtc, plan.CommissionObservedAtUtc);
        Assert.Equal(commission.ObservedAtUtc.AddMinutes(1), plan.CommissionValidUntilUtc);
    }

    [Fact]
    public void ConservativeCommissionReserveDoesNotChangeDefaultDiscountValidation()
    {
        var baseline = Request(OrderSide.Buy);
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .003m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            true, true, "BNB", .25m, false, Now);

        var defaultResult = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            baseline.Intent, baseline.Decision, baseline.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission));
        var conservativeResult = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            baseline.Intent, baseline.Decision, baseline.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission, useConservativeCommissionReserve: true));

        Assert.Equal(ExecutionPlanner.CommissionUnavailable, defaultResult.NoTradeReason);
        Assert.Equal(.003m, conservativeResult.Plan!.EstimatedCommissionRate);
    }

    [Fact]
    public void ConservativeCommissionReserveFailsClosedForRateAboveBound()
    {
        var baseline = Request(OrderSide.Buy);
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(1.000001m, 1.000001m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            false, false, null, null, false, Now);

        var result = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            baseline.Intent, baseline.Decision, baseline.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission, useConservativeCommissionReserve: true));

        Assert.Equal(ExecutionPlanner.CommissionUnavailable, result.NoTradeReason);
    }

    [Fact]
    public void EffectiveCommissionFailsClosedForFractionRateAboveOne()
    {
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(1.000001m, 1.000001m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            false, false, null, null, false, Now);

        var available = commission.TryGetEffectiveRate(OrderSide.Buy, false, Now, TimeSpan.FromMinutes(1), out var rate, out var reason);

        Assert.False(available);
        Assert.Equal(0m, rate);
        Assert.Equal("COMMISSION_RATE_INVALID", reason);
    }

    [Fact]
    public void ConservativeCommissionReserveFailsClosedWhenRateArithmeticOverflows()
    {
        var baseline = Request(OrderSide.Buy);
        var commission = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            false, false, null, null, false, Now);

        var result = ExecutionPlanner.Plan(new ExecutionPlanRequest(
            baseline.Intent, baseline.Decision, baseline.Market, Now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            effectiveCommission: commission, useConservativeCommissionReserve: true));

        Assert.Equal(ExecutionPlanner.CommissionUnavailable, result.NoTradeReason);
    }

    [Fact]
    public void ExecutionPlanAlignsQuantityAndPriceToStepAndTickSize()
    {
        var market = new ExecutionMarketSnapshot(
            new Price(84184.053m),
            new Price(84185.123m),
            50_000m,
            Now,
            isComplete: true,
            quantityStepSize: 0.00001m,
            priceTickSize: 0.01m);

        var quantity = new Quantity(0.0004m);
        var referencePrice = new Price(80000m);
        var notional = quantity.Value * referencePrice.Value;
        var intent = new OrderIntent(
            new OrderId(Guid.NewGuid()),
            new ClientOrderId("ct-intent"),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Limit,
            quantity,
            referencePrice,
            new CorrelationId(Guid.NewGuid()),
            new IdempotencyKey("decision:step-test"),
            new RiskEvaluationRequest(notional, 0m, 0m, 0m, Now, Now),
            Now);
        var decision = new RiskDecision(RiskDecisionStatus.Approved, 33.33m, 33.33m, "risk-1", [RiskReasonCodes.TargetWithinLimits]);
        var request = new ExecutionPlanRequest(
            intent,
            decision,
            market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            attempt: 0);

        var plan = ExecutionPlanner.Plan(request).Plan;

        Assert.NotNull(plan);
        Assert.Equal(84184.05m, plan.LimitPrice.Value);
        Assert.Equal(0.00039m, plan.Quantity.Value);
        Assert.Equal(0.00039m * 84184.05m, plan.AuthorizedNotional);
        Assert.Equal(0m, plan.Quantity.Value % 0.00001m);
    }

    [Fact]
    public void ExecutionPlanRespectsPassiveMakerPeggedWithOffset()
    {
        var market = new ExecutionMarketSnapshot(
            new Price(100.00m),
            new Price(100.10m),
            10_000m,
            Now,
            isComplete: true,
            quantityStepSize: 0.01m,
            priceTickSize: 0.01m);

        var intent = new OrderIntent(
            new OrderId(Guid.NewGuid()),
            new ClientOrderId("ct-peg-test"),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Limit,
            new Quantity(1m),
            new Price(100m),
            new CorrelationId(Guid.NewGuid()),
            new IdempotencyKey("decision:peg-test"),
            new RiskEvaluationRequest(100m, 0m, 0m, 0m, Now, Now),
            Now);

        var decision = new RiskDecision(RiskDecisionStatus.Approved, 100m, 100m, "risk-1", [RiskReasonCodes.TargetWithinLimits]);
        var tactic = new ExecutionTacticDecision(
            ExecutionTactic.PassiveMakerPegged,
            0.20m,
            0.5m,
            1.0m,
            false,
            "MAKER_PEGGED",
            PegOffsetTicks: 1);

        var request = new ExecutionPlanRequest(
            intent,
            decision,
            market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            attempt: 0,
            tactic: tactic);

        var plan = ExecutionPlanner.Plan(request).Plan;

        Assert.NotNull(plan);
        Assert.Equal(100.01m, plan.LimitPrice.Value); // 1 tick inside spread
    }

    [Fact]
    public void ExecutionPlanRespectsPassiveMakerDeep()
    {
        var market = new ExecutionMarketSnapshot(
            new Price(100.00m),
            new Price(100.10m),
            10_000m,
            Now,
            isComplete: true,
            quantityStepSize: 0.01m,
            priceTickSize: 0.01m);

        var intent = new OrderIntent(
            new OrderId(Guid.NewGuid()),
            new ClientOrderId("ct-deep-test"),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Limit,
            new Quantity(1m),
            new Price(100m),
            new CorrelationId(Guid.NewGuid()),
            new IdempotencyKey("decision:deep-test"),
            new RiskEvaluationRequest(100m, 0m, 0m, 0m, Now, Now),
            Now);

        var decision = new RiskDecision(RiskDecisionStatus.Approved, 100m, 100m, "risk-1", [RiskReasonCodes.TargetWithinLimits]);
        var tactic = new ExecutionTacticDecision(
            ExecutionTactic.PassiveMakerDeep,
            0.10m,
            0.0m,
            2.0m,
            false,
            "MAKER_DEEP");

        var request = new ExecutionPlanRequest(
            intent,
            decision,
            market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            attempt: 0,
            tactic: tactic);

        var plan = ExecutionPlanner.Plan(request).Plan;

        Assert.NotNull(plan);
        Assert.Equal(99.99m, plan.LimitPrice.Value); // 1 tick behind best bid
    }

    [Fact]
    public void ExecutionPlanRespectsPassiveMakerPeggedSellWithOffset()
    {
        var market = new ExecutionMarketSnapshot(
            new Price(100.00m),
            new Price(100.10m),
            10_000m,
            Now,
            isComplete: true,
            quantityStepSize: 0.01m,
            priceTickSize: 0.01m);

        var intent = new OrderIntent(
            new OrderId(Guid.NewGuid()),
            new ClientOrderId("ct-peg-sell-test"),
            new Symbol("BTCUSDT"),
            OrderSide.Sell,
            OrderType.Limit,
            new Quantity(1m),
            new Price(100m),
            new CorrelationId(Guid.NewGuid()),
            new IdempotencyKey("decision:peg-sell-test"),
            new RiskEvaluationRequest(100m, 100m, 0m, 0m, Now, Now),
            Now);

        var decision = new RiskDecision(RiskDecisionStatus.Approved, 100m, 100m, "risk-1", [RiskReasonCodes.TargetWithinLimits]);
        var tactic = new ExecutionTacticDecision(
            ExecutionTactic.PassiveMakerPegged,
            0.20m,
            0.5m,
            1.0m,
            false,
            "MAKER_PEGGED",
            PegOffsetTicks: 1);

        var request = new ExecutionPlanRequest(
            intent,
            decision,
            market,
            Now,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            attempt: 0,
            tactic: tactic);

        var plan = ExecutionPlanner.Plan(request).Plan;

        Assert.NotNull(plan);
        Assert.Equal(100.09m, plan.LimitPrice.Value); // 1 tick inside spread from ask
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
