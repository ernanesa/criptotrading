#pragma warning disable CA2007 // Integration assertions intentionally follow the test task context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;
using Xunit.Sdk;

namespace CryptoTrading.Persistence.IntegrationTests;

/// <summary>
/// Runs against the PostgreSQL instance explicitly supplied by the integration
/// script. It never reads Binance credentials and uses unique identities per run.
/// </summary>
public sealed class PostgresTradingStoreIntegrationTests
{
    [Fact]
    public async Task PersistsLedgerAndOutboxWithLeaseRetryAndIdempotency()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.CheckConnectivityAsync();
        await store.ApplyMigrationsAsync();

        var now = DateTimeOffset.UtcNow;
        var correlationId = CorrelationId.New();
        var ledgerId = LedgerEntryId.New();
        var ledgerKey = IdempotencyKey.New();
        var usdt = new Asset("USDT");
        var ledger = new LedgerEntry(
            ledgerId,
            correlationId,
            now,
            "integration-test-ledger",
            [
                new LedgerPosting(new AccountId("integration-cash"), usdt, 100m),
                new LedgerPosting(new AccountId("integration-clearing"), usdt, -100m),
            ],
            ledgerKey);

        Assert.Same(ledger, await store.AppendJournalEntryAsync(ledger));
        var replayed = await store.AppendJournalEntryAsync(ledger);
        Assert.Equal(ledgerId, replayed.Id);
        Assert.Equal(100m, (await store.GetBalanceAsync(new AccountId("integration-cash"), usdt)).Amount);

        var orderId = OrderId.New();
        var commandKey = IdempotencyKey.New();
        var command = new SubmitOrderCommand(
            orderId,
            ClientOrderId.New(),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(0.001m),
            new Price(50_000m),
            50m,
            correlationId,
            commandKey,
            "integration-test",
            now);
        var outbox = new OutboxRecord(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlationId,
            commandKey,
            "integration-test",
            command,
            OutboxRecordStatus.Pending,
            now);

        await store.EnqueueAsync(outbox);
        await store.EnqueueAsync(outbox);

        var firstOwner = "integration-owner-a";
        var firstLease = await store.ClaimOutboxAsync(firstOwner, now, TimeSpan.FromSeconds(5), 1);
        var leased = Assert.Single(firstLease);
        Assert.Equal(1, leased.Attempts);
        Assert.Equal(firstOwner, leased.LeaseOwner);

        Assert.True(await store.FailOutboxAsync(
            leased.MessageId,
            firstOwner,
            "simulated-transient-failure",
            now.AddSeconds(-1)));

        var secondOwner = "integration-owner-b";
        var retryLease = await store.ClaimOutboxAsync(secondOwner, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 1);
        var retried = Assert.Single(retryLease);
        Assert.Equal(leased.MessageId, retried.MessageId);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(secondOwner, retried.LeaseOwner);
        Assert.True(await store.MarkOutboxProcessedAsync(retried.MessageId, secondOwner, DateTimeOffset.UtcNow));
        Assert.Empty(await store.ClaimOutboxAsync("integration-owner-c", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 1));
    }
}
