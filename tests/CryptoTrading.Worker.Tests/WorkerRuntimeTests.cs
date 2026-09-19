using System.Text.Json;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
using CryptoTrading.Worker;
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
}
