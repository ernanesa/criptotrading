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
using CryptoTrading.Domain.Risk;
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

    Task<bool> DeadLetterOutboxAsync(
        Guid messageId,
        string leaseOwner,
        string failure,
        DateTimeOffset deadLetteredAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable, approval-gated registry for immutable risk-policy documents.</summary>
public interface IRiskPolicyStore
{
    Task SaveRiskPolicyAsync(RiskPolicyDocument document, CancellationToken cancellationToken = default);

    Task<RiskPolicyDocument> ApproveRiskPolicyAsync(
        string policyVersion,
        string approvalId,
        string approver,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default);

    Task<RiskPolicyDocument> ActivateRiskPolicyAsync(
        string policyVersion,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default);

    Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PostgreSQL adapter for the durable journal, ledger projection and outbox.
/// Every mutating operation owns one transaction and uses parameterized SQL.
/// </summary>
public sealed class PostgresTradingStore : IOutboxStore, IRiskPolicyStore, IAsyncDisposable
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

    public async Task SaveRiskPolicyAsync(
        RiskPolicyDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Lifecycle != RiskPolicyLifecycle.Draft)
        {
            throw new InvalidOperationException("Only draft risk policies can be initially registered.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.risk_policies
                (policy_version, max_order_notional, max_exposure_notional, max_daily_loss,
                 max_drawdown, max_market_data_age_ticks, withdrawals_enabled,
                 max_slippage_bps, max_volatility_bps, min_liquidity_notional,
                 effective_from, expires_at, document_hash, created_by)
            VALUES
                (@version, @max_order, @max_exposure, @max_daily_loss, @max_drawdown,
                 @max_age_ticks, @withdrawals, @max_slippage, @max_volatility,
                 @min_liquidity, @effective_from, @expires_at, @document_hash, @created_by)
            ON CONFLICT (policy_version) DO NOTHING
            RETURNING policy_version;
            """,
            RiskPolicyParameters(document), cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existing = await ReadRiskPolicyRowAsync(connection, transaction, document.Policy.Version, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Risk-policy uniqueness reported a conflict, but no document was found.");
            EnsureEquivalent(existing, document);
        }
        else
        {
            await InsertRiskPolicyEventAsync(connection, transaction, document.Policy.Version,
                RiskPolicyLifecycle.Draft, document.CreatedBy, null, document.EffectiveFromUtc, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RiskPolicyDocument> ApproveRiskPolicyAsync(
        string policyVersion,
        string approvalId,
        string approver,
        DateTimeOffset approvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var row = await ReadRiskPolicyRowAsync(connection, transaction, policyVersion.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Risk policy '{policyVersion}' is not registered.");
        var latest = await ReadLatestRiskPolicyEventAsync(connection, transaction, row.PolicyVersion, cancellationToken).ConfigureAwait(false);
        if (latest?.Lifecycle == RiskPolicyLifecycle.Approved)
        {
            if (string.Equals(latest.ApprovalId, approvalId, StringComparison.Ordinal) &&
                string.Equals(latest.Actor, approver, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BuildRiskPolicyDocument(row, latest, RiskPolicyLifecycle.Approved);
            }

            throw new InvalidOperationException($"Risk policy '{row.PolicyVersion}' is already approved.");
        }

        if (latest?.Lifecycle != RiskPolicyLifecycle.Draft)
        {
            throw new InvalidOperationException($"Risk policy '{row.PolicyVersion}' is not in Draft lifecycle.");
        }

        var draft = BuildRiskPolicyDocument(row, latest, RiskPolicyLifecycle.Draft);
        var approved = draft.Approve(approver, approvalId, approvedAtUtc);
        await InsertRiskPolicyEventAsync(connection, transaction, row.PolicyVersion,
            RiskPolicyLifecycle.Approved, approved.ApprovedBy!, approved.ApprovalId!, approved.ApprovedAtUtc!.Value, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return approved;
    }

    public async Task<RiskPolicyDocument> ActivateRiskPolicyAsync(
        string policyVersion,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var row = await ReadRiskPolicyRowAsync(connection, transaction, policyVersion.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Risk policy '{policyVersion}' is not registered.");
        var latest = await ReadLatestRiskPolicyEventAsync(connection, transaction, row.PolicyVersion, cancellationToken).ConfigureAwait(false);
        if (latest?.Lifecycle == RiskPolicyLifecycle.Active)
        {
            var approval = await ReadLatestApprovalEventAsync(connection, transaction, row.PolicyVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Active risk policy is missing approval evidence.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BuildRiskPolicyDocument(row, approval, RiskPolicyLifecycle.Active);
        }

        if (latest?.Lifecycle != RiskPolicyLifecycle.Approved)
        {
            throw new InvalidOperationException($"Risk policy '{row.PolicyVersion}' must be approved before activation.");
        }

        var approvedEvent = await ReadLatestApprovalEventAsync(connection, transaction, row.PolicyVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Approved risk policy is missing approval evidence.");
        var approved = BuildRiskPolicyDocument(row, approvedEvent, RiskPolicyLifecycle.Approved);
        var active = approved.Activate(activatedAtUtc);

        var previousActive = await ReadActivePolicyVersionAsync(connection, transaction, activatedAtUtc, cancellationToken).ConfigureAwait(false);
        if (previousActive is not null && !string.Equals(previousActive, row.PolicyVersion, StringComparison.Ordinal))
        {
            await InsertRiskPolicyEventAsync(connection, transaction, previousActive,
                RiskPolicyLifecycle.Superseded, "system", null, activatedAtUtc, cancellationToken).ConfigureAwait(false);
        }

        await InsertRiskPolicyEventAsync(connection, transaction, row.PolicyVersion,
            RiskPolicyLifecycle.Active, active.ApprovedBy!, active.ApprovalId!, activatedAtUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        if (atUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Risk-policy lookup timestamps must be UTC.", nameof(atUtc));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadActiveRiskPolicyRowAsync(connection, null, atUtc, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var approval = await ReadLatestApprovalEventAsync(connection, null, row.PolicyVersion, cancellationToken).ConfigureAwait(false);
        return approval is null ? null : BuildRiskPolicyDocument(row, approval, RiskPolicyLifecycle.Active);
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
        return await AppendJournalEntryCoreAsync(entry, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically appends a journal entry, updates its ledger projection and
    /// records the corresponding external trade ID for reconciliation.
    /// </summary>
    public async Task<LedgerEntry> AppendJournalEntryWithKnownTradeAsync(
        LedgerEntry entry,
        long tradeId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateTradeReference(tradeId, symbol);
        return await AppendJournalEntryCoreAsync(entry, tradeId, symbol, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LedgerEntry> AppendJournalEntryCoreAsync(
        LedgerEntry entry,
        long? knownTradeId,
        string? knownTradeSymbol,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var inserted = await InsertJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (!inserted)
        {
            var existing = await ReadExistingJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The journal uniqueness constraint reported a conflict, but no entry was found.");
            EnsureEquivalent(existing, entry);
            if (knownTradeId is { } existingTradeId)
            {
                await InsertKnownTradeAsync(connection, transaction, existingTradeId, knownTradeSymbol!, cancellationToken).ConfigureAwait(false);
            }

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

        if (knownTradeId is { } tradeId)
        {
            await InsertKnownTradeAsync(connection, transaction, tradeId, knownTradeSymbol!, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlySet<string>> GetAssetsAsync(
        AccountId account,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT asset FROM trading.ledger_balances WHERE account_id = @account_id ORDER BY asset;", connection);
        command.Parameters.Add(Parameter("account_id", NpgsqlDbType.Varchar, account.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(reader.GetString(0));
        }

        return assets;
    }

    /// <summary>
    /// Appends an exchange trade ID after its journal event has been committed.
    /// Re-registering the same ID is idempotent; existing rows are immutable.
    /// </summary>
    public async Task RecordKnownTradeAsync(
        long tradeId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ValidateTradeReference(tradeId, symbol);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.reconciliation_known_trades (trade_id, symbol)
            VALUES (@trade_id, @symbol)
            ON CONFLICT (trade_id) DO NOTHING;
            """, connection);
        command.Parameters.Add(Parameter("trade_id", NpgsqlDbType.Bigint, tradeId));
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, normalizedSymbol));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var existingCommand = new NpgsqlCommand(
            "SELECT symbol FROM trading.reconciliation_known_trades WHERE trade_id = @trade_id;", connection);
        existingCommand.Parameters.Add(Parameter("trade_id", NpgsqlDbType.Bigint, tradeId));
        var existingSymbol = (string?)await existingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(existingSymbol, normalizedSymbol, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Trade ID {tradeId} is already associated with symbol '{existingSymbol}', not '{normalizedSymbol}'.");
        }
    }

    /// <summary>Reads immutable trade IDs used by account reconciliation.</summary>
    public async Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT trade_id FROM trading.reconciliation_known_trades ORDER BY trade_id;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static void ValidateTradeReference(long tradeId, string symbol)
    {
        if (tradeId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tradeId), "Trade IDs cannot be negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Trade symbols must contain only ASCII letters and digits and be at most 20 characters.", nameof(symbol));
        }
    }

    private static async Task InsertKnownTradeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long tradeId,
        string symbol,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.reconciliation_known_trades (trade_id, symbol)
            VALUES (@trade_id, @symbol)
            ON CONFLICT (trade_id) DO NOTHING;
            """,
            [
                Parameter("trade_id", NpgsqlDbType.Bigint, tradeId),
                Parameter("symbol", NpgsqlDbType.Varchar, normalizedSymbol),
            ], cancellationToken).ConfigureAwait(false);

        var existingSymbol = await ExecuteScalarAsync(connection, transaction,
            "SELECT symbol FROM trading.reconciliation_known_trades WHERE trade_id = @trade_id;",
            [Parameter("trade_id", NpgsqlDbType.Bigint, tradeId)], cancellationToken).ConfigureAwait(false) as string;
        if (!string.Equals(existingSymbol, normalizedSymbol, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Trade ID {tradeId} is already associated with symbol '{existingSymbol}', not '{normalizedSymbol}'.");
        }
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
                  AND dead_lettered_at IS NULL
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

    public async Task<bool> DeadLetterOutboxAsync(
        Guid messageId,
        string leaseOwner,
        string failure,
        DateTimeOffset deadLetteredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        var boundedError = failure.Length <= 4000 ? failure : failure[..4000];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE trading.outbox_messages
            SET dead_lettered_at = @dead_lettered_at, locked_until = NULL, lease_owner = NULL, last_error = @last_error
            WHERE message_id = @message_id AND lease_owner = @lease_owner AND processed_at IS NULL AND dead_lettered_at IS NULL;
            """, connection);
        command.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, messageId));
        command.Parameters.Add(Parameter("lease_owner", NpgsqlDbType.Varchar, leaseOwner));
        command.Parameters.Add(Parameter("dead_lettered_at", NpgsqlDbType.TimestampTz, deadLetteredAtUtc.ToUniversalTime().UtcDateTime));
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

    private sealed record RiskPolicyRow(
        string PolicyVersion,
        decimal MaxOrderNotional,
        decimal MaxExposureNotional,
        decimal MaxDailyLoss,
        decimal MaxDrawdown,
        long MaxMarketDataAgeTicks,
        bool WithdrawalsEnabled,
        decimal? MaxSlippageBps,
        decimal? MaxVolatilityBps,
        decimal? MinLiquidityNotional,
        DateTimeOffset EffectiveFromUtc,
        DateTimeOffset ExpiresAtUtc,
        string DocumentHash,
        string CreatedBy);

    private sealed record RiskPolicyEventRow(
        Guid EventId,
        string PolicyVersion,
        RiskPolicyLifecycle Lifecycle,
        string Actor,
        string? ApprovalId,
        DateTimeOffset OccurredAtUtc);

    private static IReadOnlyList<NpgsqlParameter> RiskPolicyParameters(RiskPolicyDocument document) =>
    [
        Parameter("version", NpgsqlDbType.Varchar, document.Policy.Version),
        Parameter("max_order", NpgsqlDbType.Numeric, document.Policy.MaxOrderNotional),
        Parameter("max_exposure", NpgsqlDbType.Numeric, document.Policy.MaxExposureNotional),
        Parameter("max_daily_loss", NpgsqlDbType.Numeric, document.Policy.MaxDailyLoss),
        Parameter("max_drawdown", NpgsqlDbType.Numeric, document.Policy.MaxDrawdown),
        Parameter("max_age_ticks", NpgsqlDbType.Bigint, document.Policy.MaxMarketDataAge.Ticks),
        Parameter("withdrawals", NpgsqlDbType.Boolean, document.Policy.WithdrawalsEnabled),
        Parameter("max_slippage", NpgsqlDbType.Numeric, document.Policy.MaxSlippageBps),
        Parameter("max_volatility", NpgsqlDbType.Numeric, document.Policy.MaxVolatilityBps),
        Parameter("min_liquidity", NpgsqlDbType.Numeric, document.Policy.MinLiquidityNotional),
        Parameter("effective_from", NpgsqlDbType.TimestampTz, document.EffectiveFromUtc.UtcDateTime),
        Parameter("expires_at", NpgsqlDbType.TimestampTz, document.ExpiresAtUtc.UtcDateTime),
        Parameter("document_hash", NpgsqlDbType.Char, document.DocumentHash),
        Parameter("created_by", NpgsqlDbType.Varchar, document.CreatedBy),
    ];

    private static async Task InsertRiskPolicyEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string policyVersion,
        RiskPolicyLifecycle lifecycle,
        string actor,
        string? approvalId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.risk_policy_events
                (event_id, policy_version, lifecycle, actor, approval_id, occurred_at)
            VALUES (@event_id, @policy_version, @lifecycle, @actor, @approval_id, @occurred_at);
            """,
            [
                Parameter("event_id", NpgsqlDbType.Uuid, Guid.NewGuid()),
                Parameter("policy_version", NpgsqlDbType.Varchar, policyVersion),
                Parameter("lifecycle", NpgsqlDbType.Varchar, lifecycle.ToString()),
                Parameter("actor", NpgsqlDbType.Varchar, actor),
                Parameter("approval_id", NpgsqlDbType.Varchar, approvalId),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, occurredAtUtc.UtcDateTime),
            ], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RiskPolicyRow?> ReadRiskPolicyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT policy_version, max_order_notional, max_exposure_notional, max_daily_loss,
                   max_drawdown, max_market_data_age_ticks, withdrawals_enabled,
                   max_slippage_bps, max_volatility_bps, min_liquidity_notional,
                   effective_from, expires_at, document_hash, created_by
            FROM trading.risk_policies
            WHERE policy_version = @version;
            """, connection, transaction);
        command.Parameters.Add(Parameter("version", NpgsqlDbType.Varchar, version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRiskPolicyRow(reader) : null;
    }

    private static async Task<RiskPolicyEventRow?> ReadLatestRiskPolicyEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, policy_version, lifecycle, actor, approval_id, occurred_at
            FROM trading.risk_policy_events
            WHERE policy_version = @version
            ORDER BY occurred_at DESC, event_id DESC
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("version", NpgsqlDbType.Varchar, version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRiskPolicyEvent(reader) : null;
    }

    private static async Task<RiskPolicyEventRow?> ReadLatestApprovalEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, policy_version, lifecycle, actor, approval_id, occurred_at
            FROM trading.risk_policy_events
            WHERE policy_version = @version AND lifecycle = 'Approved'
            ORDER BY occurred_at DESC, event_id DESC
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("version", NpgsqlDbType.Varchar, version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRiskPolicyEvent(reader) : null;
    }

    private static async Task<RiskPolicyRow?> ReadActiveRiskPolicyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH latest AS
            (
                SELECT DISTINCT ON (policy_version) policy_version, lifecycle
                FROM trading.risk_policy_events
                ORDER BY policy_version, occurred_at DESC, event_id DESC
            )
            SELECT p.policy_version, p.max_order_notional, p.max_exposure_notional, p.max_daily_loss,
                   p.max_drawdown, p.max_market_data_age_ticks, p.withdrawals_enabled,
                   p.max_slippage_bps, p.max_volatility_bps, p.min_liquidity_notional,
                   p.effective_from, p.expires_at, p.document_hash, p.created_by
            FROM trading.risk_policies p
            INNER JOIN latest ON latest.policy_version = p.policy_version
            WHERE latest.lifecycle = 'Active'
              AND p.effective_from <= @at_utc
              AND p.expires_at > @at_utc
            ORDER BY p.effective_from DESC, p.policy_version
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("at_utc", NpgsqlDbType.TimestampTz, atUtc.UtcDateTime));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRiskPolicyRow(reader) : null;
    }

    private static async Task<string?> ReadActivePolicyVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH latest AS
            (
                SELECT DISTINCT ON (policy_version) policy_version, lifecycle
                FROM trading.risk_policy_events
                ORDER BY policy_version, occurred_at DESC, event_id DESC
            )
            SELECT p.policy_version
            FROM trading.risk_policies p
            INNER JOIN latest ON latest.policy_version = p.policy_version
            WHERE latest.lifecycle = 'Active'
              AND p.effective_from <= @at_utc
              AND p.expires_at > @at_utc
            ORDER BY p.effective_from DESC, p.policy_version
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("at_utc", NpgsqlDbType.TimestampTz, atUtc.UtcDateTime));
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RiskPolicyDocument BuildRiskPolicyDocument(
        RiskPolicyRow row,
        RiskPolicyEventRow eventRow,
        RiskPolicyLifecycle lifecycle)
    {
        var policy = new RiskPolicy(
            row.PolicyVersion,
            row.MaxOrderNotional,
            row.MaxExposureNotional,
            row.MaxDailyLoss,
            row.MaxDrawdown,
            TimeSpan.FromTicks(row.MaxMarketDataAgeTicks),
            row.WithdrawalsEnabled,
            row.MaxSlippageBps,
            row.MaxVolatilityBps,
            row.MinLiquidityNotional);
        var approval = lifecycle is RiskPolicyLifecycle.Approved or RiskPolicyLifecycle.Active ? eventRow : null;
        return new RiskPolicyDocument(
            policy,
            row.EffectiveFromUtc,
            row.ExpiresAtUtc,
            row.CreatedBy,
            row.DocumentHash,
            lifecycle,
            approval?.Actor,
            approval?.ApprovalId,
            approval?.OccurredAtUtc);
    }

    private static RiskPolicyRow ReadRiskPolicyRow(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetFieldValue<decimal>(1),
        reader.GetFieldValue<decimal>(2),
        reader.GetFieldValue<decimal>(3),
        reader.GetFieldValue<decimal>(4),
        reader.GetInt64(5),
        reader.GetBoolean(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<decimal>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<decimal>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<decimal>(9),
        new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
        new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
        reader.GetString(12).Trim(),
        reader.GetString(13));

    private static RiskPolicyEventRow ReadRiskPolicyEvent(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        Enum.Parse<RiskPolicyLifecycle>(reader.GetString(2), ignoreCase: false),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));

    private static void EnsureEquivalent(RiskPolicyRow existing, RiskPolicyDocument candidate)
    {
        var policy = candidate.Policy;
        if (!string.Equals(existing.DocumentHash, candidate.DocumentHash, StringComparison.OrdinalIgnoreCase) ||
            existing.MaxOrderNotional != policy.MaxOrderNotional ||
            existing.MaxExposureNotional != policy.MaxExposureNotional ||
            existing.MaxDailyLoss != policy.MaxDailyLoss ||
            existing.MaxDrawdown != policy.MaxDrawdown ||
            existing.MaxMarketDataAgeTicks != policy.MaxMarketDataAge.Ticks ||
            existing.WithdrawalsEnabled != policy.WithdrawalsEnabled ||
            existing.MaxSlippageBps != policy.MaxSlippageBps ||
            existing.MaxVolatilityBps != policy.MaxVolatilityBps ||
            existing.MinLiquidityNotional != policy.MinLiquidityNotional ||
            existing.EffectiveFromUtc.UtcTicks / 10 != candidate.EffectiveFromUtc.UtcTicks / 10 ||
            existing.ExpiresAtUtc.UtcTicks / 10 != candidate.ExpiresAtUtc.UtcTicks / 10 ||
            !string.Equals(existing.CreatedBy, candidate.CreatedBy, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The risk-policy version is already associated with different immutable content.");
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
