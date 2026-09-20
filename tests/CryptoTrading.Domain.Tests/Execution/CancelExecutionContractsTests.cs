using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests.Execution;

public sealed class CancelExecutionContractsTests
{
    private static readonly DateTimeOffset ExpiredAt = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExpiredAcknowledgedPlanProducesDeterministicCancelOutbox()
    {
        var snapshot = Snapshot(OrderStatus.Acknowledged);

        var first = CancelLifecyclePlanner.Plan(snapshot);
        var second = CancelLifecyclePlanner.Plan(snapshot);

        Assert.True(first.IsDispatchable);
        Assert.Equal(CancelLifecyclePlanner.CancelRequired, first.Reason);
        Assert.NotNull(first.Command);
        Assert.NotNull(first.OutboxRecord);
        Assert.Equal(first.Command, second.Command);
        Assert.Equal(first.OutboxRecord, second.OutboxRecord);
        Assert.Equal(CancelLifecyclePlanner.CancelOrderMessageType, first.OutboxRecord!.MessageType);
        Assert.Equal(snapshot.ClientOrderId, first.Command!.ClientOrderId);
        Assert.Equal(snapshot.FilledQuantity, first.Command.FilledQuantity);
    }

    [Fact]
    public void BeforeTtlDoesNotCreateCancelCommand()
    {
        var snapshot = Snapshot(OrderStatus.Acknowledged);

        var beforeExpiry = new CancelableExecutionSnapshot(
            snapshot.OrderId,
            snapshot.ClientOrderId,
            snapshot.Symbol,
            snapshot.Side,
            snapshot.Type,
            snapshot.OriginalQuantity,
            snapshot.FilledQuantity,
            snapshot.LimitPrice,
            snapshot.CorrelationId,
            snapshot.IdempotencyKey,
            snapshot.Status,
            snapshot.Attempt,
            snapshot.ExpiresAtUtc,
            snapshot.ExpiresAtUtc.AddSeconds(-1),
            snapshot.ExchangeOrderId);

        var result = CancelLifecyclePlanner.Plan(beforeExpiry);

        Assert.False(result.IsDispatchable);
        Assert.Equal(CancelLifecyclePlanner.TtlNotReached, result.Reason);
    }

    [Theory]
    [InlineData(OrderStatus.CancelPending, CancelLifecyclePlanner.AwaitingReconciliation)]
    [InlineData(OrderStatus.Unknown, CancelLifecyclePlanner.AwaitingReconciliation)]
    [InlineData(OrderStatus.Filled, CancelLifecyclePlanner.TerminalOrder)]
    [InlineData(OrderStatus.Cancelled, CancelLifecyclePlanner.TerminalOrder)]
    public void UnsafeOrTerminalStatesNeverCreateReplacementCancel(OrderStatus status, string reason)
    {
        var result = CancelLifecyclePlanner.Plan(Snapshot(status));

        Assert.False(result.IsDispatchable);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void SubmittedOrderWaitsForAcknowledgementBeforeCancellation()
    {
        var result = CancelLifecyclePlanner.Plan(Snapshot(OrderStatus.Submitted));

        Assert.False(result.IsDispatchable);
        Assert.Equal(CancelLifecyclePlanner.NotCancelable, result.Reason);
    }

    [Fact]
    public void CancelledPartialOrderReturnsOnlyResidualForReplan()
    {
        var command = Assert.IsType<CancelOrderCommand>(CancelLifecyclePlanner.Plan(Snapshot(OrderStatus.PartiallyFilled)).Command);

        var resolution = CancelLifecyclePlanner.Resolve(
            command,
            CancelVenueStatus.Canceled,
            new Quantity(0.006m));

        Assert.True(resolution.CanReplan);
        Assert.Equal(OrderStatus.Cancelled, resolution.ProjectedStatus);
        Assert.Equal(new Quantity(0.004m), resolution.ResidualQuantity);
        Assert.Equal(CancelLifecyclePlanner.ResidualReadyForReplan, resolution.Reason);
    }

    [Theory]
    [InlineData(CancelVenueStatus.New, OrderStatus.Acknowledged)]
    [InlineData(CancelVenueStatus.PartiallyFilled, OrderStatus.PartiallyFilled)]
    [InlineData(CancelVenueStatus.PendingCancel, OrderStatus.CancelPending)]
    [InlineData(CancelVenueStatus.Unknown, OrderStatus.Unknown)]
    public void OpenOrAmbiguousCancelOutcomeCannotReplan(CancelVenueStatus status, OrderStatus projected)
    {
        var command = Assert.IsType<CancelOrderCommand>(CancelLifecyclePlanner.Plan(Snapshot(OrderStatus.Acknowledged)).Command);

        var resolution = CancelLifecyclePlanner.Resolve(command, status, new Quantity(0m));

        Assert.False(resolution.CanReplan);
        Assert.Equal(CancelResolutionAction.AwaitReconciliation, resolution.Action);
        Assert.Equal(projected, resolution.ProjectedStatus);
    }

    [Fact]
    public void FilledOutcomeWithoutFullQuantityFailsClosedAsUnknown()
    {
        var command = Assert.IsType<CancelOrderCommand>(CancelLifecyclePlanner.Plan(Snapshot(OrderStatus.Acknowledged)).Command);

        var resolution = CancelLifecyclePlanner.Resolve(command, CancelVenueStatus.Filled, new Quantity(0.009m));

        Assert.False(resolution.CanReplan);
        Assert.Equal(OrderStatus.Unknown, resolution.ProjectedStatus);
        Assert.Equal(CancelResolutionAction.AwaitReconciliation, resolution.Action);
    }

    private static CancelableExecutionSnapshot Snapshot(OrderStatus status) => new(
        new OrderId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new ClientOrderId("ctp-client-1"),
        new Symbol("BTCUSDT"),
        OrderSide.Buy,
        OrderType.Limit,
        new Quantity(0.01m),
        new Quantity(status == OrderStatus.PartiallyFilled ? 0.006m : 0m),
        new Price(100m),
        new CorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new IdempotencyKey("execution:baseline:1"),
        status,
        1,
        ExpiredAt,
        ExpiredAt,
        "42");
}
