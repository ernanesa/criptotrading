using System.Text.Json;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class WorkerRuntimeTests
{
    [Fact]
    public void DefaultsArePaperNoTradeAndDoNotRequirePersistence()
    {
        var options = new WorkerRuntimeOptions();

        options.Validate();

        Assert.Equal("PAPER", options.ExecutionMode);
        Assert.True(options.NoTrade);
        Assert.False(options.PersistenceEnabled);
        Assert.False(options.ReconciliationEnabled);
    }

    [Fact]
    public void MaxOutboxAttemptsIsBounded()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions { MaxOutboxAttempts = 0 }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions { MaxOutboxAttempts = 101 }.Validate());
    }

    [Fact]
    public void ShadowDrillRequiresNoTradeMode()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ShadowEnabled = true,
            NoTrade = false,
        }.Validate());
    }

    [Fact]
    public void ReconciliationRequiresNoTradeAndPersistence()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationEnabled = true,
            NoTrade = false,
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost",
        }.Validate());

        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationEnabled = true,
            NoTrade = true,
            PersistenceEnabled = false,
        }.Validate());
    }

    [Fact]
    public void ReconciliationSnapshotAgeIsBounded()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationMaxSnapshotAgeSeconds = 0,
        }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationMaxSnapshotAgeSeconds = 86_401,
        }.Validate());
    }

    [Fact]
    public async Task NoTradeExecutorDoesNotParseOrExecutePayload()
    {
        var executor = new PaperOutboxMessageExecutor(
            new PaperExchange(),
            Options.Create(new WorkerRuntimeOptions()),
            NullLogger<PaperOutboxMessageExecutor>.Instance);
        var message = new LeasedOutboxMessage(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            CorrelationId.New(),
            IdempotencyKey.New(),
            "not-json-and-never-executed",
            DateTimeOffset.UtcNow,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test-worker");

        await executor.ExecuteAsync(message);
    }

    [Fact]
    public async Task PaperExecutorUsesCommandIdentityAndSubmitsWhenTradeGateIsExplicitlyDisabled()
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(
            OrderId.New(),
            ClientOrderId.New(),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(0.01m),
            new Price(10_000m),
            100m,
            correlation,
            idempotency,
            "risk-v1",
            DateTimeOffset.UtcNow);
        var message = new LeasedOutboxMessage(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlation,
            idempotency,
            JsonSerializer.Serialize(command),
            command.CreatedAtUtc,
            1,
            command.CreatedAtUtc.AddMinutes(1),
            "test-worker");
        var exchange = new PaperExchange();
        var options = Options.Create(new WorkerRuntimeOptions { NoTrade = false });
        var executor = new PaperOutboxMessageExecutor(exchange, options, NullLogger<PaperOutboxMessageExecutor>.Instance);

        await executor.ExecuteAsync(message);

        var order = Assert.Single(exchange.OpenOrders);
        Assert.Equal(command.ClientOrderId, order.ClientOrderId);
    }

    [Fact]
    public async Task PaperExecutorRecoversAmbiguousTimeoutByClientOrderIdWithoutSubmittingAgain()
    {
        var command = NewCommand();
        var exchange = new SubmitThenTimeoutPaperExchange();
        var message = MessageFor(command);
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await executor.ExecuteAsync(message);

        Assert.Equal(1, exchange.SubmitCalls);
        var order = Assert.Single(exchange.OpenOrders);
        Assert.Equal(command.ClientOrderId, order.ClientOrderId);
    }

    [Fact]
    public async Task PaperExecutorPreservesAmbiguousTimeoutWhenClientOrderIsAbsent()
    {
        var command = NewCommand();
        var exchange = new TimeoutWithoutOrderPaperExchange();
        var message = MessageFor(command);
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(message));

        Assert.Contains(command.ClientOrderId.Value, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exchange.SubmitCalls);
    }

    private static SubmitOrderCommand NewCommand() => new(
        OrderId.New(),
        ClientOrderId.New(),
        new Symbol("BTCUSDT"),
        OrderSide.Buy,
        OrderType.Market,
        new Quantity(0.01m),
        new Price(10_000m),
        100m,
        CorrelationId.New(),
        IdempotencyKey.New(),
        "risk-v1",
        DateTimeOffset.UtcNow);

    private static LeasedOutboxMessage MessageFor(SubmitOrderCommand command) => new(
        Guid.NewGuid(),
        OrderExecutionPipeline.SubmitOrderMessageType,
        command.CorrelationId,
        command.IdempotencyKey,
        JsonSerializer.Serialize(command),
        command.CreatedAtUtc,
        1,
        command.CreatedAtUtc.AddMinutes(1),
        "test-worker");

    private sealed class SubmitThenTimeoutPaperExchange : IPaperExchange
    {
        private readonly PaperExchange inner = new();

        public int SubmitCalls { get; private set; }

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            var result = inner.Submit(request, idempotencyKey);
            throw new TimeoutException("response lost after exchange acceptance");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => inner.GetOrder(clientOrderId);

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => inner.Cancel(clientOrderId, idempotencyKey);

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => inner.Apply(tick);

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => inner.Replay(ticks);

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => inner.OpenOrders;

        public IReadOnlyCollection<PaperFill> Fills => inner.Fills;
    }

    private sealed class TimeoutWithoutOrderPaperExchange : IPaperExchange
    {
        public int SubmitCalls { get; private set; }

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            throw new TimeoutException("response lost before exchange acceptance");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => throw new KeyNotFoundException();

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => throw new NotSupportedException();

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => [];

        public IReadOnlyCollection<PaperFill> Fills => [];
    }
}
