#pragma warning disable CA2007 // Npgsql async disposal/commands are intentionally awaited with ConfigureAwait(false) at each I/O boundary.
#pragma warning disable CA1849 // The small result sets use synchronous field access after an async read; no blocking I/O is performed.
#pragma warning disable CA2100 // SQL is either a trusted embedded migration or a constant query; values are always parameters.
#pragma warning disable CA1031 // The readiness boundary converts provider failures into an unhealthy result.
#pragma warning disable IDE0058 // Npgsql parameter registration intentionally ignores the returned parameter.

using System.Data;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

/// <summary>Connection settings for the local PostgreSQL persistence adapter.</summary>
public sealed record PostgresPersistenceOptions
{
    public PostgresPersistenceOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString.Trim();
    }

    public string ConnectionString { get; }
}

/// <summary>A leased outbox message ready for an exchange dispatcher.</summary>
public sealed record LeasedOutboxMessage(
    Guid MessageId,
    string MessageType,
    CorrelationId CorrelationId,
    IdempotencyKey? IdempotencyKey,
    string PayloadJson,
    DateTimeOffset AvailableAt,
    int Attempts,
    DateTimeOffset LockedUntil,
    string LeaseOwner);

/// <summary>Durable outbox operations required by a dispatcher.</summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<LeasedOutboxMessage>> ClaimOutboxAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<bool> MarkOutboxProcessedAsync(
        Guid messageId,
        string leaseOwner,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> FailOutboxAsync(
        Guid messageId,
        string leaseOwner,
        string failure,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PostgreSQL adapter for the durable journal, ledger projection and outbox.
/// Every mutating operation owns one transaction and uses parameterized SQL.
/// </summary>
public sealed class PostgresTradingStore : IOutboxStore, IAsyncDisposable
{
    private const string MigrationLockKey = "cryptotrading:persistence:migrations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource dataSource;

    public PostgresTradingStore(PostgresPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    /// <summary>Applies embedded up migrations exactly once, serialised by an advisory lock.</summary>
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var migration in PersistenceMigrations.ReadAll().Where(static migration => migration.Direction == "up"))
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended(@lock_name, 0));",
                [Parameter("lock_name", NpgsqlDbType.Text, MigrationLockKey)], cancellationToken).ConfigureAwait(false);

            var schemaMigrationsExists = await ExistsAsync(connection, transaction,
                "SELECT to_regclass('trading.schema_migrations') IS NOT NULL;", [], cancellationToken).ConfigureAwait(false);
            var alreadyApplied = schemaMigrationsExists && await ExistsAsync(connection, transaction,
                "SELECT EXISTS (SELECT 1 FROM trading.schema_migrations WHERE version = @version);",
                [Parameter("version", NpgsqlDbType.Varchar, migration.Version)], cancellationToken).ConfigureAwait(false);

            if (!alreadyApplied)
            {
                await ExecuteAsync(connection, transaction, migration.Sql, [], cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks the database connection without mutating schema or data.</summary>
    public async Task CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1;", connection);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a balanced journal entry and updates the rebuildable balance projection
    /// in one transaction. Replaying an identical ID or idempotency key is a no-op.
    /// </summary>
    public async Task<LedgerEntry> AppendJournalEntryAsync(
        LedgerEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var inserted = await InsertJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (!inserted)
        {
            var existing = await ReadExistingJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The journal uniqueness constraint reported a conflict, but no entry was found.");
            EnsureEquivalent(existing, entry);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        for (var index = 0; index < entry.Postings.Count; index++)
        {
            var posting = entry.Postings[index];
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO trading.ledger_postings (entry_id, posting_index, account_id, asset, amount)
                VALUES (@entry_id, @posting_index, @account_id, @asset, @amount);
                """,
                [
                    Parameter("entry_id", NpgsqlDbType.Uuid, entry.Id.Value),
                    Parameter("posting_index", NpgsqlDbType.Smallint, checked((short)index)),
                    Parameter("account_id", NpgsqlDbType.Varchar, posting.Account.Value),
                    Parameter("asset", NpgsqlDbType.Varchar, posting.Amount.Asset.Value),
                    Parameter("amount", NpgsqlDbType.Numeric, posting.Amount.Amount),
                ], cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO trading.ledger_balances (account_id, asset, balance, projection_version)
                VALUES (@account_id, @asset, @balance, 1)
                ON CONFLICT (account_id, asset) DO UPDATE SET
                    balance = trading.ledger_balances.balance + EXCLUDED.balance,
                    projection_version = trading.ledger_balances.projection_version + 1,
                    updated_at = transaction_timestamp();
                """,
                [
                    Parameter("account_id", NpgsqlDbType.Varchar, posting.Account.Value),
                    Parameter("asset", NpgsqlDbType.Varchar, posting.Amount.Asset.Value),
                    Parameter("balance", NpgsqlDbType.Numeric, posting.Amount.Amount),
                ], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<Money> GetBalanceAsync(
        AccountId account,
        Asset asset,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT balance FROM trading.ledger_balances WHERE account_id = @account_id AND asset = @asset;", connection);
        command.Parameters.Add(Parameter("account_id", NpgsqlDbType.Varchar, account.Value));
        command.Parameters.Add(Parameter("asset", NpgsqlDbType.Varchar, asset.Value));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return new(result is null or DBNull ? 0m : (decimal)result, asset);
    }

    /// <summary>Enqueues a command durably; duplicate identity is accepted only for identical payload.</summary>
    public async Task<OutboxRecord> EnqueueAsync(
        OutboxRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payload = JsonSerializer.Serialize(record.Command, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.outbox_messages
                (message_id, aggregate_type, aggregate_id, event_type, payload, correlation_id,
                 idempotency_key, occurred_at, available_at)
            VALUES (@message_id, 'order', @aggregate_id, @event_type, @payload, @correlation_id,
                    @idempotency_key, @occurred_at, @occurred_at)
            ON CONFLICT DO NOTHING
            RETURNING message_id;
            """,
            [
                Parameter("message_id", NpgsqlDbType.Uuid, record.MessageId),
                Parameter("aggregate_id", NpgsqlDbType.Varchar, record.Command.OrderId.ToString()),
                Parameter("event_type", NpgsqlDbType.Varchar, record.MessageType),
                Parameter("payload", NpgsqlDbType.Jsonb, payload),
                Parameter("correlation_id", NpgsqlDbType.Uuid, record.CorrelationId.Value),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, record.IdempotencyKey.Value),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, record.CreatedAtUtc.UtcDateTime),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existing = await ReadExistingOutboxIdentityAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The outbox uniqueness constraint reported a conflict, but no message was found.");
            if (!string.Equals(existing.EventType, record.MessageType, StringComparison.Ordinal)
                || !EquivalentJson(existing.PayloadJson, payload)
                || existing.CorrelationId != record.CorrelationId
                || !string.Equals(existing.IdempotencyKey, record.IdempotencyKey.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The outbox identity is already associated with different data.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>Claims available messages atomically using row locks and a time-bounded lease.</summary>
    public async Task<IReadOnlyList<LeasedOutboxMessage>> ClaimOutboxAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (batchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = nowUtc.ToUniversalTime();
        var lockedUntil = now.Add(leaseDuration);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH candidates AS
            (
                SELECT message_id
                FROM trading.outbox_messages
                WHERE processed_at IS NULL
                  AND available_at <= @now
                  AND (locked_until IS NULL OR locked_until <= @now)
                ORDER BY available_at, created_at, message_id
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            )
            UPDATE trading.outbox_messages AS messages
            SET locked_until = @locked_until,
                lease_owner = @lease_owner,
                attempts = messages.attempts + 1
            FROM candidates
            WHERE messages.message_id = candidates.message_id
            RETURNING messages.message_id, messages.event_type, messages.payload::text,
                      messages.correlation_id, messages.idempotency_key, messages.available_at,
                      messages.attempts, messages.locked_until, messages.lease_owner;
            """, connection, transaction);
        command.Parameters.Add(Parameter("now", NpgsqlDbType.TimestampTz, now.UtcDateTime));
        command.Parameters.Add(Parameter("locked_until", NpgsqlDbType.TimestampTz, lockedUntil.UtcDateTime));
        command.Parameters.Add(Parameter("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(Parameter("lease_owner", NpgsqlDbType.Varchar, leaseOwner));

        var messages = new List<LeasedOutboxMessage>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new(
                reader.GetGuid(0),
                reader.GetString(1),
                new CorrelationId(reader.GetGuid(3)),
                reader.IsDBNull(4) ? null : new IdempotencyKey(reader.GetString(4)),
                reader.GetString(2),
                reader.GetFieldValue<DateTime>(5).ToUniversalTime(),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTime>(7).ToUniversalTime(),
                reader.GetString(8)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return messages;
    }

    public async Task<bool> MarkOutboxProcessedAsync(
        Guid messageId,
        string leaseOwner,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE trading.outbox_messages
            SET processed_at = COALESCE(processed_at, @processed_at), locked_until = NULL, lease_owner = NULL
            WHERE message_id = @message_id AND lease_owner = @lease_owner AND processed_at IS NULL;
            """, connection);
        command.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, messageId));
        command.Parameters.Add(Parameter("lease_owner", NpgsqlDbType.Varchar, leaseOwner));
        command.Parameters.Add(Parameter("processed_at", NpgsqlDbType.TimestampTz, processedAtUtc.ToUniversalTime().UtcDateTime));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> FailOutboxAsync(
        Guid messageId,
        string leaseOwner,
        string failure,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        var boundedError = failure.Length <= 4000 ? failure : failure[..4000];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE trading.outbox_messages
            SET available_at = @available_at, locked_until = NULL, lease_owner = NULL, last_error = @last_error
            WHERE message_id = @message_id AND lease_owner = @lease_owner AND processed_at IS NULL;
            """, connection);
        command.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, messageId));
        command.Parameters.Add(Parameter("lease_owner", NpgsqlDbType.Varchar, leaseOwner));
        command.Parameters.Add(Parameter("available_at", NpgsqlDbType.TimestampTz, retryAtUtc.ToUniversalTime().UtcDateTime));
        command.Parameters.Add(Parameter("last_error", NpgsqlDbType.Text, boundedError));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask DisposeAsync() => await dataSource.DisposeAsync().ConfigureAwait(false);

    private static async Task<bool> InsertJournalEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LedgerEntry entry,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.journal_entries (entry_id, correlation_id, occurred_at, reason, idempotency_key)
            VALUES (@entry_id, @correlation_id, @occurred_at, @reason, @idempotency_key)
            ON CONFLICT DO NOTHING
            RETURNING entry_id;
            """,
            [
                Parameter("entry_id", NpgsqlDbType.Uuid, entry.Id.Value),
                Parameter("correlation_id", NpgsqlDbType.Uuid, entry.CorrelationId.Value),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, entry.OccurredAt.UtcDateTime),
                Parameter("reason", NpgsqlDbType.Varchar, entry.Reason),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, entry.IdempotencyKey?.Value),
            ], cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task<LedgerEntry?> ReadExistingJournalEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LedgerEntry candidate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT entry_id, correlation_id, occurred_at, reason, idempotency_key
            FROM trading.journal_entries
            WHERE entry_id = @entry_id OR (@idempotency_key IS NOT NULL AND idempotency_key = @idempotency_key)
            ORDER BY CASE WHEN entry_id = @entry_id THEN 0 ELSE 1 END
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("entry_id", NpgsqlDbType.Uuid, candidate.Id.Value));
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, candidate.IdempotencyKey?.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var entryId = new LedgerEntryId(reader.GetGuid(0));
        var correlationId = new CorrelationId(reader.GetGuid(1));
        var occurredAt = reader.GetFieldValue<DateTime>(2).ToUniversalTime();
        var reason = reader.GetString(3);
        IdempotencyKey? idempotencyKey = reader.IsDBNull(4) ? null : new IdempotencyKey(reader.GetString(4));
        await reader.DisposeAsync().ConfigureAwait(false);
        var postings = await ReadPostingsAsync(connection, transaction, entryId, cancellationToken).ConfigureAwait(false);
        return new(
            entryId,
            correlationId,
            occurredAt,
            reason,
            postings,
            idempotencyKey);
    }

    private static async Task<IReadOnlyList<LedgerPosting>> ReadPostingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LedgerEntryId entryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT account_id, asset, amount FROM trading.ledger_postings WHERE entry_id = @entry_id ORDER BY posting_index;",
            connection, transaction);
        command.Parameters.Add(Parameter("entry_id", NpgsqlDbType.Uuid, entryId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var postings = new List<LedgerPosting>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            postings.Add(new(
                new AccountId(reader.GetString(0)),
                new Asset(reader.GetString(1)),
                reader.GetDecimal(2)));
        }

        return postings;
    }

    private static async Task<(string EventType, string PayloadJson, CorrelationId CorrelationId, string? IdempotencyKey)?> ReadExistingOutboxIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_type, payload::text, correlation_id, idempotency_key
            FROM trading.outbox_messages
            WHERE message_id = @message_id OR idempotency_key = @idempotency_key
            ORDER BY CASE WHEN message_id = @message_id THEN 0 ELSE 1 END
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, record.MessageId));
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, record.IdempotencyKey.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), new CorrelationId(reader.GetGuid(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static void EnsureEquivalent(LedgerEntry existing, LedgerEntry candidate)
    {
        if (existing.Id != candidate.Id
            || existing.CorrelationId != candidate.CorrelationId
            // PostgreSQL timestamptz stores microseconds while DateTimeOffset
            // carries 100ns ticks; compare at the database precision.
            || existing.OccurredAt.ToUniversalTime().Ticks / 10 != candidate.OccurredAt.ToUniversalTime().Ticks / 10
            || !string.Equals(existing.Reason, candidate.Reason, StringComparison.Ordinal)
            || existing.IdempotencyKey != candidate.IdempotencyKey
            || !existing.Postings.SequenceEqual(candidate.Postings))
        {
            throw new InvalidOperationException("The journal identity is already associated with different data.");
        }
    }

    private static bool EquivalentJson(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters.ToArray());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        return (bool)(await ExecuteScalarAsync(connection, transaction, sql, parameters, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The existence query returned no value."));
    }
}

/// <summary>Readiness check backed by a real PostgreSQL round-trip.</summary>
public sealed class PostgresHealthCheck(
    PostgresTradingStore store,
    ILogger<PostgresHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await store.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("PostgreSQL accepted a connectivity probe.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PostgresHealthLog.ProbeFailed(logger, exception);
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}

internal static partial class PostgresHealthLog
{
    [LoggerMessage(LogLevel.Warning, "PostgreSQL readiness probe failed.")]
    public static partial void ProbeFailed(ILogger logger, Exception exception);
}

/// <summary>Registers the shared PostgreSQL store and a real readiness check.</summary>
public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresTradingStore(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton(_ => new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)));
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>(
            "postgres",
            HealthStatus.Unhealthy,
            tags: ["ready", "postgres"]);
        return services;
    }
}
