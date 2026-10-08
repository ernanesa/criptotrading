#pragma warning disable CA2007 // Integration assertions intentionally follow the test task context.

using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Primitives;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace CryptoTrading.Persistence.IntegrationTests;

public sealed class DispatchSafetyIntegrationTests
{
    [Fact]
    public async Task IndependentStoresCanApplyMigrationsConcurrently()
    {
        var connectionString = GetConnectionString();
        await using var first = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await using var second = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));

        await Task.WhenAll(first.ApplyMigrationsAsync(), second.ApplyMigrationsAsync());
        await Task.WhenAll(first.CheckConnectivityAsync(), second.CheckConnectivityAsync());
    }

    [Fact]
    public async Task SubmissionFenceExtendsLiveLeaseButRejectsExpiredOrStaleOwner()
    {
        var (store, connectionString) = await CreateStoreAsync();
        await using (store)
        {
            var tooShort = await InsertLeasedMessageAsync(connectionString, "lease-short", DateTimeOffset.UtcNow.AddSeconds(2));
            Assert.True(await store.TryBeginSubmissionAsync(tooShort, TimeSpan.FromSeconds(12)));
            Assert.True(await store.ValidateOutboxLeaseAsync(tooShort, TimeSpan.FromSeconds(10)));
            await SetLeaseExpiryAsync(connectionString, tooShort.MessageId, TimeSpan.FromSeconds(5));
            Assert.False(await store.ValidateOutboxLeaseAsync(tooShort, TimeSpan.FromSeconds(10)));

            var expired = await InsertLeasedMessageAsync(connectionString, "lease-expired", DateTimeOffset.UtcNow.AddSeconds(-1));
            Assert.False(await store.TryBeginSubmissionAsync(expired, TimeSpan.FromSeconds(12)));

            var staleOwner = tooShort with { LeaseOwner = "different-owner", LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1) };
            Assert.False(await store.TryBeginSubmissionAsync(staleOwner, TimeSpan.FromSeconds(12)));
        }
    }

    [Fact]
    public async Task ConcurrentOwnersCanCreateOnlyOneSubmissionFence()
    {
        var (store, connectionString) = await CreateStoreAsync();
        await using (store)
        {
            var message = await InsertLeasedMessageAsync(connectionString, "lease-race", DateTimeOffset.UtcNow.AddMinutes(1));
            var outcomes = await Task.WhenAll(
                store.TryBeginSubmissionAsync(message, TimeSpan.FromSeconds(12)),
                store.TryBeginSubmissionAsync(message, TimeSpan.FromSeconds(12)));

            Assert.Single(outcomes, item => item);
            Assert.Single(outcomes, item => !item);
            Assert.True(await store.HasSubmissionStartedAsync(message));
        }
    }

    private static async Task<(PostgresTradingStore Store, string ConnectionString)> CreateStoreAsync()
    {
        var connectionString = GetConnectionString();
        var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync();
        return (store, connectionString);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        return connectionString;
    }

    private static async Task<LeasedOutboxMessage> InsertLeasedMessageAsync(string connectionString, string suffix, DateTimeOffset lockedUntil)
    {
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var owner = "dispatch-safety-" + suffix;
        const string payload = "{\"probe\":\"dispatch-safety\"}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.outbox_messages
                (message_id, aggregate_type, aggregate_id, event_type, payload, correlation_id,
                 idempotency_key, occurred_at, available_at, attempts, locked_until, lease_owner)
            VALUES (@message_id, 'test', @aggregate_id, 'test.dispatch', @payload::jsonb,
                    @correlation_id, @idempotency_key, clock_timestamp(), @available_at, 1,
                    @locked_until, @lease_owner);
            """, connection);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("aggregate_id", messageId.ToString("N"));
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        command.Parameters.AddWithValue("idempotency_key", "dispatch-safety-" + suffix);
        command.Parameters.AddWithValue("available_at", new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("locked_until", lockedUntil);
        command.Parameters.AddWithValue("lease_owner", owner);
        await command.ExecuteNonQueryAsync();
        return new LeasedOutboxMessage(messageId, OrderExecutionPipeline.SubmitOrderMessageType,
            new CorrelationId(correlationId), new IdempotencyKey("dispatch-safety-" + suffix), payload,
            DateTimeOffset.UtcNow, 1, lockedUntil, owner);
    }

    private static async Task SetLeaseExpiryAsync(string connectionString, Guid messageId, TimeSpan remaining)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE trading.outbox_messages SET locked_until = clock_timestamp() + @remaining WHERE message_id = @message_id;",
            connection);
        command.Parameters.AddWithValue("remaining", remaining);
        command.Parameters.AddWithValue("message_id", messageId);
        await command.ExecuteNonQueryAsync();
    }
}
