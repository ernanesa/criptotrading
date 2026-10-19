using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests.Execution;

public sealed class OrderExecutionPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);
    private static readonly RiskPolicy Policy = new("risk-v1", 100m, 250m, 25m, 50m, TimeSpan.FromSeconds(5));

    [Fact]
    public void ApprovedIntentProducesCommandAndPendingOutboxWithTraceability()
    {
        var intent = NewIntent(80m);

        var result = OrderExecutionPipeline.Evaluate(intent, Policy);

        Assert.True(result.IsDispatchable);
        Assert.Equal(RiskDecisionStatus.Approved, result.Decision.Status);
        Assert.NotNull(result.Command);
        Assert.NotNull(result.OutboxRecord);
        Assert.Equal(intent.CorrelationId, result.Command!.CorrelationId);
        Assert.Equal(intent.IdempotencyKey, result.Command.IdempotencyKey);
        Assert.Equal("risk-v1", result.Command.PolicyVersion);
        Assert.Equal(result.Command, result.OutboxRecord!.Command);
        Assert.Equal(OutboxRecordStatus.Pending, result.OutboxRecord.Status);
        Assert.Equal(OrderExecutionPipeline.SubmitOrderMessageType, result.OutboxRecord.MessageType);
    }

    [Fact]
    public void AdjustedIntentProducesReducedQuantityAndAuthorizedNotional()
    {
        var intent = NewIntent(140m, quantity: 2m, referencePrice: 70m);

        var result = OrderExecutionPipeline.Evaluate(intent, Policy);

        Assert.Equal(RiskDecisionStatus.Adjusted, result.Decision.Status);
        Assert.Equal(100m, result.Decision.AuthorizedTargetNotional);
        Assert.Equal(100m / 70m, result.Command!.Quantity.Value);
        Assert.Equal(100m, result.Command.AuthorizedNotional);
        Assert.Equal(100m, result.Command.Quantity.Value * result.Command.ReferencePrice.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectedOrEmergencyBlockedIntentProducesNoCommandOrOutbox(bool emergencyStop, bool withdrawal)
    {
        var intent = NewIntent(80m, emergencyStop: emergencyStop, withdrawal: withdrawal);

        var result = OrderExecutionPipeline.Evaluate(intent, Policy);

        Assert.False(result.IsDispatchable);
        Assert.Null(result.Command);
        Assert.Null(result.OutboxRecord);
        Assert.Contains(result.Decision.Status, new[] { RiskDecisionStatus.EmergencyBlocked, RiskDecisionStatus.Rejected });
    }

    [Fact]
    public void LossLimitBlocksNewExposureAndDoesNotCreateSideEffects()
    {
        var result = OrderExecutionPipeline.Evaluate(NewIntent(80m, dailyLoss: 25m), Policy);

        Assert.Equal(RiskDecisionStatus.Rejected, result.Decision.Status);
        Assert.Null(result.OutboxRecord);
    }

    [Fact]
    public void SellReductionDuringLossLimitRemainsDispatchableWithSameTraceability()
    {
        var intent = NewIntent(10m, currentExposure: 20m, dailyLoss: 25m, side: OrderSide.Sell);

        var result = OrderExecutionPipeline.Evaluate(intent, Policy);

        Assert.Equal(RiskDecisionStatus.Adjusted, result.Decision.Status);
        Assert.True(result.IsDispatchable);
        Assert.Equal(intent.IdempotencyKey, result.OutboxRecord!.IdempotencyKey);
        Assert.Equal(intent.CorrelationId, result.OutboxRecord.CorrelationId);
    }

    [Fact]
    public void BuySmallerThanExistingExposureIsStillBlockedDuringLossLimit()
    {
        var intent = NewIntent(10m, currentExposure: 20m, dailyLoss: 25m, side: OrderSide.Buy);

        var result = OrderExecutionPipeline.Evaluate(intent, Policy);

        Assert.Equal(RiskDecisionStatus.Rejected, result.Decision.Status);
        Assert.False(result.IsDispatchable);
    }

    [Fact]
    public void IntentRejectsMismatchBetweenOrderQuantityAndRiskNotional()
    {
        Assert.Throws<ArgumentException>(() => NewIntent(80m, quantity: 2m, referencePrice: 70m));
    }

    [Fact]
    public void PipelineRequiresNonNullIntentAndPolicy()
    {
        Assert.Throws<ArgumentNullException>(() => OrderExecutionPipeline.Evaluate(null!, Policy));
        Assert.Throws<ArgumentNullException>(() => OrderExecutionPipeline.Evaluate(NewIntent(80m), null!));
    }

    [Fact]
    public void PlanProtectiveOcoAlignsPricesAndQuantitiesToExchangeTickAndStepSize()
    {
        var ocoPlan = ExecutionPlanner.PlanProtectiveOco(
            new Symbol("BTCUSDT"),
            new Quantity(0.0123456m),
            new Price(84_200.00m),
            takeProfitBps: 45m,
            stopLossBps: 18m,
            priceTickSize: 0.01m,
            quantityStepSize: 0.00010m,
            listSeed: "entry-1");

        Assert.Equal(OrderSide.Sell, ocoPlan.Side);
        Assert.Equal(0.0123m, ocoPlan.Quantity.Value);
        Assert.True(ocoPlan.AboveTakeProfitPrice.Value > 84_200.00m);
        Assert.True(ocoPlan.BelowStopLossTriggerPrice.Value < 84_200.00m);
        Assert.True(ocoPlan.BelowStopLossLimitPrice.Value < ocoPlan.BelowStopLossTriggerPrice.Value);
        Assert.StartsWith("ocol-", ocoPlan.ListClientOrderId.Value, StringComparison.Ordinal);
        Assert.StartsWith("ocotp-", ocoPlan.AboveClientOrderId.Value, StringComparison.Ordinal);
        Assert.StartsWith("ocosl-", ocoPlan.BelowClientOrderId.Value, StringComparison.Ordinal);
    }

    private static OrderIntent NewIntent(
        decimal requestedNotional,
        decimal? quantity = null,
        decimal? referencePrice = null,
        decimal currentExposure = 0m,
        decimal dailyLoss = 0m,
        bool emergencyStop = false,
        bool withdrawal = false,
        OrderSide side = OrderSide.Buy)
    {
        var price = referencePrice ?? 100m;
        var amount = quantity ?? requestedNotional / price;
        var evaluationAt = Now;
        var evaluation = new RiskEvaluationRequest(
            requestedNotional,
            currentExposure,
            dailyLoss,
            0m,
            evaluationAt.AddSeconds(-1),
            evaluationAt,
            emergencyStop,
            withdrawal);

        return new(
            OrderId.Parse("11111111111111111111111111111111"),
            new ClientOrderId("ct-pipeline-test"),
            new Symbol("BTCUSDT"),
            side,
            OrderType.Market,
            new Quantity(amount),
            new Price(price),
            CorrelationId.Parse("22222222222222222222222222222222"),
            new IdempotencyKey("pipeline-test-key"),
            evaluation,
            Now);
    }
}
