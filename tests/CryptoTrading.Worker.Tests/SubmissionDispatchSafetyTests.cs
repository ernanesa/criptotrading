using System.Text.Json;
using CryptoTrading.Configuration;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class SubmissionDispatchSafetyTests
{
    [Fact]
    public async Task DeliveryRetryWithoutFenceStillRunsSubmitPath()
    {
        var command = CreateCommand();
        var message = CreateMessage(command, attempts: 2);
        var client = new RecordingOrderClient(command);
        var store = new TestSubmissionStore();
        var executor = CreateExecutor(command, client, store, new TestSubmissionGuard(), new RecordingOrderProjectionStore());

        await executor.ExecuteAsync(message);

        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(0, client.QueryCalls);
        Assert.True(store.HasStarted(message.MessageId));
        Assert.Equal(TimeSpan.FromSeconds(12), store.MinimumLeaseRemaining);
    }

    [Fact]
    public async Task PreflightGuardFailureDoesNotProjectSubmittedOrCreateFence()
    {
        var command = CreateCommand();
        var message = CreateMessage(command, attempts: 1);
        var client = new RecordingOrderClient(command);
        var submissions = new TestSubmissionStore();
        var projection = new RecordingOrderProjectionStore();
        var guard = new TestSubmissionGuard { Reject = true };
        var executor = CreateExecutor(command, client, submissions, guard, projection);

        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => executor.ExecuteAsync(message));

        Assert.Equal(0, client.SubmitCalls);
        Assert.Empty(projection.OpenOrders);
        Assert.False(submissions.HasStarted(message.MessageId));
    }

    [Fact]
    public async Task FinalGuardFailureProjectsUnknownAndNeverSubmits()
    {
        var command = CreateCommand();
        var message = CreateMessage(command, attempts: 1);
        var client = new RecordingOrderClient(command);
        var projection = new RecordingOrderProjectionStore();
        var guard = new TestSubmissionGuard { RejectFinal = true };
        var executor = CreateExecutor(command, client, new TestSubmissionStore(), guard, projection);

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => executor.ExecuteAsync(message));

        Assert.Equal(0, client.SubmitCalls);
        Assert.Equal(0, client.QueryCalls);
        Assert.Equal("UNKNOWN", Assert.Single(projection.OpenOrders).Status);
    }

    [Fact]
    public async Task AcceptedSubmitWithProjectionFailureQueriesAndDoesNotResubmit()
    {
        var command = CreateCommand();
        var client = new RecordingOrderClient(command);
        var projection = new RecordingOrderProjectionStore(failOnUpsert: 2);
        var executor = CreateExecutor(command, client, new TestSubmissionStore(), new TestSubmissionGuard(), projection);

        await executor.ExecuteAsync(CreateMessage(command, attempts: 1));

        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(1, client.QueryCalls);
        Assert.Single(projection.OpenOrders);
    }

    private static TestnetOutboxMessageExecutor CreateExecutor(
        SubmitOrderCommand command,
        RecordingOrderClient client,
        TestSubmissionStore submissionStore,
        TestSubmissionGuard guard,
        RecordingOrderProjectionStore projectionStore) => new(
            client,
            new AccountClient(),
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true,
                ReconciliationEnabled = true,
                AccountStreamEnabled = true
            }),
            Options.Create(new TradingOptions { Binance = new BinanceOptions { AllowedSymbols = command.Symbol.Value } }),
            new OrderProjectionService(projectionStore),
            NullLogger<TestnetOutboxMessageExecutor>.Instance,
            submissionGuard: guard,
            submissionStore: submissionStore);

    private static SubmitOrderCommand CreateCommand()
    {
        var now = DateTimeOffset.UtcNow;
        return new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"), OrderSide.Buy,
            OrderType.Market, new Quantity(.01m), new Price(10_000m), 100m, CorrelationId.New(),
            IdempotencyKey.New(), "risk-v1", now, .001m, "commission-hash", "test:commission",
            now.AddSeconds(-1), now.AddMinutes(1));
    }

    private static LeasedOutboxMessage CreateMessage(SubmitOrderCommand command, int attempts) => new(
        Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, command.CorrelationId, command.IdempotencyKey,
        JsonSerializer.Serialize(command), command.CreatedAtUtc, attempts, DateTimeOffset.UtcNow.AddMinutes(1), "test-worker");

    private sealed class RecordingOrderClient(SubmitOrderCommand command) : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(Result());
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return Task.FromResult(Result());
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private BinanceSpotOrderResult Result() => new(42, command.Symbol.Value, command.ClientOrderId.Value, "NEW",
            command.ReferencePrice.Value, command.Quantity.Value, 0m, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private sealed class AccountClient : IBinanceSpotAccountClient
    {
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(
            new BinanceAccountSnapshot(0, 0, 0, 0, true, false, true, 0, "SPOT",
                [new BinanceBalance("BTC", "1", "0"), new BinanceBalance("USDT", "1000", "0")]));
        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingOrderProjectionStore(int failOnUpsert = 0) : ILocalOpenOrderStore
    {
        public List<LocalOpenOrderRecord> OpenOrders { get; } = [];
        private int upserts;
        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>(OpenOrders);
        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
        {
            upserts++;
            if (upserts == failOnUpsert) throw new IOException("simulated projection failure");
            OpenOrders.RemoveAll(item => item.ClientOrderId == order.ClientOrderId);
            OpenOrders.Add(order);
            return Task.CompletedTask;
        }
        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
        {
            OpenOrders.RemoveAll(item => item.ClientOrderId == clientOrderId);
            return Task.CompletedTask;
        }
    }
}
