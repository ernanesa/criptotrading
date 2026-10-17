#pragma warning disable CA2007 // Npgsql async disposal/commands are intentionally awaited with ConfigureAwait(false) at each I/O boundary.
#pragma warning disable CA1849 // The small result sets use synchronous field access after an async read; no blocking I/O is performed.
#pragma warning disable CA2100 // SQL is either a trusted embedded migration or a constant query; values are always parameters.
#pragma warning disable CA1031 // The readiness boundary converts provider failures into an unhealthy result.
#pragma warning disable IDE0058 // Npgsql parameter registration intentionally ignores the returned parameter.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

/// <summary>
/// Immutable exchange trade identity persisted for reconciliation.
/// </summary>
public readonly record struct KnownReconciliationTrade(string Symbol, long TradeId);

/// <summary>Versioned, redacted append-only reconciliation event payload.</summary>
public sealed record ReconciliationEventRecord(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    short SchemaVersion,
    string PayloadJson,
    string PayloadSha256);

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

public sealed record LocalOpenOrderRecord(
    string Symbol,
    long? OrderId,
    string ClientOrderId,
    string Status,
    DateTimeOffset UpdatedAtUtc,
    decimal? OriginalQuantity = null,
    decimal? CumulativeExecutedQuantity = null,
    DateTimeOffset? ExchangeUpdatedAtUtc = null);

/// <summary>Immutable linkage from a deterministic plan attempt to its outbox command.</summary>
public sealed record ExecutionSubmissionContext(
    string ClientOrderId,
    OrderId ApplicationOrderId,
    CorrelationId CorrelationId,
    Guid OutboxMessageId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity AuthorizedQuantity,
    Price ReferencePrice,
    decimal AuthorizedNotional,
    int ExecutionAttempt,
    DateTimeOffset PlannedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ExecutionFillAttributionReport(
    string ExecutionId,
    DateTimeOffset OccurredAtUtc,
    decimal Quantity,
    decimal Price,
    decimal? CumulativeFillRatio,
    decimal? ImplementationShortfallQuote,
    long? DecisionToFillLatencyMilliseconds,
    string QualityStatus,
    string QualityReason,
    string FeeStatus,
    string FeeReason);

/// <summary>One durable OMS transition, returned in deterministic lifecycle order.</summary>
public sealed record OrderLifecycleAttributionReport(
    Guid EventId,
    OrderStatus From,
    OrderStatus To,
    string IdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string? ExchangeOrderId);

/// <summary>Deterministic reconstruction from durable plan, outbox, and fill evidence.</summary>
public sealed record ExecutionAttributionReport(
    ExecutionSubmissionContext Context,
    int OutboxAttempts,
    DateTimeOffset? OutboxProcessedAtUtc,
    string? OutboxLastError,
    IReadOnlyList<OrderLifecycleAttributionReport> Lifecycle,
    IReadOnlyList<ExecutionFillAttributionReport> Fills);

public sealed record OrderLifecycleEvent(
    Guid EventId,
    string ClientOrderId,
    OrderTransition Transition)
{
    /// <summary>Creates a replay-stable journal identity from the immutable transition.</summary>
    public static OrderLifecycleEvent Create(string clientOrderId, OrderTransition transition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(transition);
        var normalizedClientOrderId = clientOrderId.Trim();
        var canonical = string.Join('|',
            normalizedClientOrderId,
            transition.From,
            transition.To,
            transition.IdempotencyKey.Value,
            transition.OccurredAtUtc.UtcDateTime.Ticks,
            transition.ExchangeOrderId ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new OrderLifecycleEvent(new Guid(hash.AsSpan(0, 16)), normalizedClientOrderId, transition);
    }
}

/// <summary>Immutable, non-dispatchable residual discovered after a terminal cancel.</summary>
public sealed record ExecutionReplanCandidate(
    Guid CandidateId,
    OrderId OriginOrderId,
    ClientOrderId OriginClientOrderId,
    Guid CancelMessageId,
    Guid TerminalLifecycleEventId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity ResidualQuantity,
    int NextAttempt,
    CorrelationId CorrelationId,
    DateTimeOffset CreatedAtUtc);

/// <summary>Atomic terminal cancel outcome: journal, open-order deactivation, and optional replan candidate.</summary>
public sealed record TerminalCancellationResolution(
    CancelOrderCommand Command,
    CancelResolution Resolution,
    Guid CancelMessageId,
    OrderLifecycleEvent LifecycleEvent);

public sealed record RuntimeStateCheckpoint(RuntimeState State, long Revision);

/// <summary>
/// Immutable decision evidence committed before an autonomous cycle can advance
/// its watermark or make an outbox message visible to the dispatcher.
/// </summary>
public sealed record AutonomyDecisionCommit(
    string DecisionId,
    string Environment,
    string AccountScope,
    string CheckpointScope,
    Symbol Symbol,
    string StrategyId,
    string StrategyVersion,
    DateTimeOffset CloseTimeUtc,
    DateTimeOffset AvailableAtUtc,
    string FeatureSchemaVersion,
    string FeatureConfigurationFingerprint,
    string FeatureFingerprint,
    string Signal,
    string StrategyReason,
    RiskDecision? RiskDecision,
    string Outcome,
    string? NoTradeReason,
    string TraceJson,
    OutboxRecord? OutboxRecord)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(CheckpointScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(StrategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StrategyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(FeatureSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(FeatureConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(FeatureFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(Signal);
        ArgumentException.ThrowIfNullOrWhiteSpace(StrategyReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(Outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(TraceJson);
        if (CloseTimeUtc.Offset != TimeSpan.Zero || AvailableAtUtc.Offset != TimeSpan.Zero || AvailableAtUtc < CloseTimeUtc)
        {
            throw new ArgumentException("Decision close and availability times must be ordered UTC timestamps.");
        }

        if (string.Equals(Outcome, "DISPATCHABLE", StringComparison.Ordinal) != (OutboxRecord is not null))
        {
            throw new ArgumentException("Only a dispatchable decision can contain an outbox record.");
        }

        if (OutboxRecord is not null && RiskDecision is null)
        {
            throw new ArgumentException("A dispatchable decision requires durable risk evidence.");
        }
    }
}

/// <summary>
/// Atomic persistence boundary for decision evidence, optional execution
/// outbox, fee/submission context, and the autonomy checkpoint.
/// </summary>
public interface IAutonomyDecisionCommitStore
{
    Task<DateTimeOffset?> GetLastProcessedCloseAsync(string scope, CancellationToken cancellationToken = default);

    Task CommitAsync(AutonomyDecisionCommit commit, CancellationToken cancellationToken = default);

    Task<AutonomyDecisionCommit?> ReadDecisionCommitAsync(string decisionId, CancellationToken cancellationToken = default);
}

public interface IExpectedExecutionFeeStore
{
    Task RecordExpectedExecutionFeeAsync(
        SubmitOrderCommand command,
        CancellationToken cancellationToken = default);

    Task<ExecutionFeeEstimate?> ReadExpectedExecutionFeeAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);
}

public interface IExecutionSubmissionContextStore
{
    Task<ExecutionSubmissionContext?> ReadExecutionSubmissionContextAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable source and outbox boundary for expired passive-order cancellations.</summary>
public interface IOrderCancellationStore
{
    Task<IReadOnlyList<CancelableExecutionSnapshot>> ReadExpiredCancellableExecutionsAsync(
        DateTimeOffset observedAtUtc,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<CancelOutboxRecord> EnqueueCancellationAsync(
        CancelOutboxRecord record,
        CancellationToken cancellationToken = default);

    Task<ExecutionReplanCandidate?> ResolveTerminalCancellationAsync(
        TerminalCancellationResolution resolution,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionReplanCandidate>> ReadReplanCandidatesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable append-only runtime lifecycle and monotonic checkpoint boundary.</summary>
public interface IRuntimeStateStore
{
    Task<RuntimeStateCheckpoint> ReadRuntimeStateAsync(CancellationToken cancellationToken = default);
    Task AppendRuntimeTransitionAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default);
}

public interface IExecutionAttributionReportStore
{
    Task<ExecutionAttributionReport?> ReadExecutionAttributionReportAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);
}

public interface IOrderLifecycleStore
{
    Task<bool> AppendOrderLifecycleEventAsync(OrderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderLifecycleEvent>> ReadOrderLifecycleEventsAsync(string clientOrderId, CancellationToken cancellationToken = default);
}

/// <summary>Durable outbox operations required by a dispatcher.</summary>
public interface IOutboxStore
{
    Task<OutboxRecord> EnqueueAsync(
        OutboxRecord record,
        CancellationToken cancellationToken = default);

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

/// <summary>Durable watermark for a deterministic autonomy decision stream.</summary>
public interface IAutonomyCheckpointStore
{
    Task<DateTimeOffset?> GetLastProcessedCloseAsync(
        string scope,
        CancellationToken cancellationToken = default);

    Task SetLastProcessedCloseAsync(
        string scope,
        DateTimeOffset closeTimeUtc,
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

/// <summary>Append-only account-stream evidence required for reconnect recovery.</summary>
public interface IAccountStreamEventStore
{
    Task<bool> AppendAccountStreamEventAsync(
        AccountStreamEventRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountStreamEventRecord>> ReadAccountStreamEventsAsync(
        DateTimeOffset sinceUtc,
        int limit = 1_000,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable idempotent storage for reconciliation audit events.</summary>
public interface IReconciliationEventStore
{
    Task<bool> AppendReconciliationEventAsync(
        ReconciliationEventRecord record,
        CancellationToken cancellationToken = default);
}

public interface ILocalOpenOrderStore
{
    Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default);

    Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default);

    Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default);
}

/// <summary>Atomically applies ordered account-stream updates without reviving terminal orders.</summary>
public interface IAccountStreamOrderProjectionStore : ILocalOpenOrderStore
{
    Task<bool> ApplyAccountStreamExecutionReportAsync(
        LocalOpenOrderRecord order,
        bool isTerminal,
        CancellationToken cancellationToken = default);

    Task<bool> ApplyAuthoritativeOpenOrderSnapshotAsync(
        LocalOpenOrderRecord order,
        CancellationToken cancellationToken = default);
}

/// <summary>Atomic execution-fact, journal, position and PnL projection boundary.</summary>
public interface IExecutionAccountingStore
{
    Task<ExecutionAccountingResult> RecordExecutionAsync(
        ExecutionAccountingCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable, point-in-time accounting inputs consumed by autonomous risk evaluation.</summary>
public interface IAutonomyRiskContextStore
{
    Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(
        Symbol symbol,
        Asset quoteAsset,
        DateTimeOffset dayStartUtc,
        CancellationToken cancellationToken = default);

    Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(
        string scope,
        Symbol symbol,
        Asset quoteAsset,
        DateTimeOffset dayStartUtc,
        decimal? currentEquityNotional,
        DateTimeOffset valuationAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed record AutonomyRiskContextSnapshot(
    decimal RealizedDailyLossNotional,
    decimal PositionCostNotional,
    decimal PositionQuantity,
    bool HasPositionBasis,
    DateTimeOffset AsOfUtc,
    decimal? CurrentEquityNotional = null,
    decimal? HighWaterMarkNotional = null,
    decimal? DrawdownNotional = null,
    bool HasHighWaterMark = false,
    bool RiskContextComplete = false,
    string? RiskContextUnavailableReason = null);

/// <summary>Durable append-only store for KillSwitch/SafeMode transitions.</summary>
public interface IOperationalControlAuditStore
{
    Task<bool> AppendOperationalControlAuditEventAsync(
        OperationalControlAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationalControlAuditEvent>> ReadOperationalControlAuditEventsAsync(
        DateTimeOffset? sinceUtc = null,
        int limit = 1_000,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable append-only store for preregistered research evidence.</summary>
public interface IExperimentGovernanceStore
{
    Task<bool> AppendExperimentPreregistrationAsync(
        ExperimentPreregistration preregistration,
        CancellationToken cancellationToken = default);

    Task<bool> AppendExperimentTrialAsync(
        ExperimentId experimentId,
        ExperimentTrial trial,
        CancellationToken cancellationToken = default);

    Task<bool> AppendHoldoutAccessAuditAsync(
        HoldoutAccessAudit audit,
        CancellationToken cancellationToken = default);

    Task<bool> AppendHoldoutContaminationAsync(
        ExperimentId experimentId,
        string actor,
        string reason,
        string replacementHoldoutVersion,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<ExperimentGovernanceSnapshot?> ReadExperimentGovernanceSnapshotAsync(
        ExperimentId experimentId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read model reconstructed from immutable research evidence after restart.</summary>
public sealed record ExperimentGovernanceSnapshot(
    ExperimentPreregistration Preregistration,
    IReadOnlyList<ExperimentTrial> Trials,
    int HoldoutAccessCount,
    bool HoldoutContaminated,
    string? ReplacementHoldoutVersion,
    string? ContaminationReason);

/// <summary>
/// PostgreSQL adapter for the durable journal, ledger projection and outbox.
/// Every mutating operation owns one transaction and uses parameterized SQL.
/// </summary>
public sealed partial class PostgresTradingStore : IOutboxStore, IExpectedExecutionFeeStore, IExecutionSubmissionContextStore, IOrderCancellationStore, IRuntimeStateStore, IExecutionAttributionReportStore, IOrderLifecycleStore, IAutonomyCheckpointStore, IAutonomyDecisionCommitStore, IRiskPolicyStore, IAccountStreamEventStore, IReconciliationEventStore, ILocalOpenOrderStore, IAccountStreamOrderProjectionStore, IExecutionAccountingStore, IAutonomyRiskContextStore, IOperationalControlAuditStore, IExperimentGovernanceStore, ICapitalReservationStore, IAsyncDisposable
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
        var migrations = PersistenceMigrations.ReadAll();
        PersistenceMigrations.ValidateInventory(migrations);
        foreach (var migration in migrations.Where(static migration => migration.Direction == "up"))
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "SELECT pg_advisory_xact_lock(hashtextextended(@lock_name, 0));",
                [Parameter("lock_name", NpgsqlDbType.Text, MigrationLockKey)], cancellationToken).ConfigureAwait(false);

            var schemaMigrationsExists = await ExistsAsync(connection, transaction,
                "SELECT to_regclass('trading.schema_migrations') IS NOT NULL;", [], cancellationToken).ConfigureAwait(false);
            if (schemaMigrationsExists)
            {
                var appliedVersions = new List<string>();
                await using var appliedCommand = new NpgsqlCommand(
                    "SELECT version FROM trading.schema_migrations ORDER BY version;", connection, transaction);
                await using var appliedReader = await appliedCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await appliedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    appliedVersions.Add(appliedReader.GetString(0));
                }

                PersistenceMigrations.ValidateAppliedVersions(appliedVersions, migrations);
            }
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
        await using var command = new NpgsqlCommand("SELECT version FROM trading.schema_migrations;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = applied.Add(reader.GetString(0));
        }
        var migrations = PersistenceMigrations.ReadAll();
        PersistenceMigrations.ValidateAppliedVersions(applied, migrations);
        if (migrations.Any(migration => migration.Direction == "up" && !applied.Contains(migration.Version)))
        {
            throw new InvalidOperationException("PostgreSQL schema is behind the running release.");
        }
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
                 effective_from, expires_at, document_hash, created_by, budget_policy_json)
            VALUES
                (@version, @max_order, @max_exposure, @max_daily_loss, @max_drawdown,
                 @max_age_ticks, @withdrawals, @max_slippage, @max_volatility,
                 @min_liquidity, @effective_from, @expires_at, @document_hash, @created_by, @budget_policy_json)
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

    public async Task<bool> AppendAccountStreamEventAsync(
        AccountStreamEventRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.account_stream_events
                (event_id, event_type, dedupe_key, event_time_ms, received_at, symbol, payload, payload_sha256,
                 session_id, session_generation, subscription_id)
            VALUES (@event_id, @event_type, @dedupe_key, @event_time_ms, @received_at, @symbol, @payload, @payload_sha256,
                    @session_id, @session_generation, @subscription_id)
            ON CONFLICT DO NOTHING
            RETURNING event_id;
            """,
            [
                Parameter("event_id", NpgsqlDbType.Uuid, record.EventId),
                Parameter("event_type", NpgsqlDbType.Varchar, record.EventType),
                Parameter("dedupe_key", NpgsqlDbType.Varchar, record.DedupeKey),
                Parameter("event_time_ms", NpgsqlDbType.Bigint, record.EventTimeMilliseconds),
                Parameter("received_at", NpgsqlDbType.TimestampTz, record.ReceivedAtUtc.UtcDateTime),
                Parameter("symbol", NpgsqlDbType.Varchar, record.Symbol),
                Parameter("payload", NpgsqlDbType.Jsonb, record.PayloadJson),
                Parameter("payload_sha256", NpgsqlDbType.Char, record.PayloadSha256),
                Parameter("session_id", NpgsqlDbType.Uuid, record.SessionId),
                Parameter("session_generation", NpgsqlDbType.Bigint, record.SessionGeneration),
                Parameter("subscription_id", NpgsqlDbType.Bigint, record.SubscriptionId),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existing = await ReadAccountStreamEventAsync(connection, transaction, record.EventId, record.EventType, record.DedupeKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Account stream uniqueness reported a conflict, but no event was found.");
            EnsureEquivalent(existing, record);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<AccountStreamEventRecord>> ReadAccountStreamEventsAsync(
        DateTimeOffset sinceUtc,
        int limit = 1_000,
        CancellationToken cancellationToken = default)
    {
        if (sinceUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Account stream lookup timestamps must be UTC.", nameof(sinceUtc));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, event_type, dedupe_key, event_time_ms, received_at, symbol, payload, payload_sha256,
                   session_id, session_generation, subscription_id
            FROM trading.account_stream_events
            WHERE received_at >= @since_utc
            ORDER BY received_at, event_id
            LIMIT @limit;
            """, connection);
        command.Parameters.Add(Parameter("since_utc", NpgsqlDbType.TimestampTz, sinceUtc.UtcDateTime));
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<AccountStreamEventRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadAccountStreamEvent(reader);
            if (!string.Equals(record.PayloadSha256, reader.GetString(7).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Account stream event {record.EventId} has an invalid payload hash.");
            }

            result.Add(record);
        }

        return result;
    }

    /// <summary>
    /// Appends one immutable operational-control transition. A repeated event ID or
    /// idempotency key is accepted only when every field and its canonical hash match.
    /// </summary>
    public async Task<bool> AppendOperationalControlAuditEventAsync(
        OperationalControlAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationalControlAuditEvent(auditEvent);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.operational_control_audit_events
                (event_id, schema_version, event_type, from_mode, to_mode, actor, reason,
                 idempotency_key, occurred_at, occurred_at_ticks, approval_id, payload_sha256)
            VALUES (@event_id, @schema_version, @event_type, @from_mode, @to_mode, @actor, @reason,
                    @idempotency_key, @occurred_at, @occurred_at_ticks, @approval_id, @payload_sha256)
            ON CONFLICT DO NOTHING
            RETURNING event_id;
            """,
            [
                Parameter("event_id", NpgsqlDbType.Uuid, auditEvent.EventId),
                Parameter("schema_version", NpgsqlDbType.Integer, auditEvent.SchemaVersion),
                Parameter("event_type", NpgsqlDbType.Varchar, auditEvent.EventType),
                Parameter("from_mode", NpgsqlDbType.Varchar, auditEvent.From.ToString()),
                Parameter("to_mode", NpgsqlDbType.Varchar, auditEvent.To.ToString()),
                Parameter("actor", NpgsqlDbType.Varchar, auditEvent.Actor),
                Parameter("reason", NpgsqlDbType.Varchar, auditEvent.Reason),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, auditEvent.IdempotencyKey.Value),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, auditEvent.OccurredAtUtc.UtcDateTime),
                Parameter("occurred_at_ticks", NpgsqlDbType.Bigint, auditEvent.OccurredAtUtc.UtcTicks),
                Parameter("approval_id", NpgsqlDbType.Varchar, auditEvent.ApprovalId),
                Parameter("payload_sha256", NpgsqlDbType.Char, auditEvent.PayloadSha256),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existing = await ReadOperationalControlAuditEventAsync(
                connection,
                transaction,
                auditEvent.EventId,
                auditEvent.IdempotencyKey.Value,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Operational-control audit uniqueness reported a conflict, but no event was found.");
            EnsureEquivalent(existing, auditEvent);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<OperationalControlAuditEvent>> ReadOperationalControlAuditEventsAsync(
        DateTimeOffset? sinceUtc = null,
        int limit = 1_000,
        CancellationToken cancellationToken = default)
    {
        if (sinceUtc.HasValue && sinceUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Operational-control audit lookup timestamps must be UTC.", nameof(sinceUtc));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, schema_version, event_type, from_mode, to_mode, actor, reason,
                   idempotency_key, occurred_at, occurred_at_ticks, approval_id, payload_sha256
            FROM trading.operational_control_audit_events
            WHERE (@since_utc IS NULL OR occurred_at >= @since_utc)
            ORDER BY occurred_at, event_id
            LIMIT @limit;
            """, connection);
        command.Parameters.Add(Parameter(
            "since_utc",
            NpgsqlDbType.TimestampTz,
            sinceUtc?.UtcDateTime));
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<OperationalControlAuditEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var auditEvent = ReadOperationalControlAuditEvent(reader);
            if (!string.Equals(auditEvent.PayloadSha256, reader.GetString(11).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Operational-control audit event {auditEvent.EventId} has an invalid payload hash.");
            }

            result.Add(auditEvent);
        }

        return result;
    }

    public async Task<bool> AppendExperimentPreregistrationAsync(
        ExperimentPreregistration preregistration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preregistration);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.experiment_preregistrations
                (experiment_id, experiment_family_id, hypothesis, search_space, planned_variants, trial_budget,
                 primary_metric, rejection_rule, benchmark, preregistered_at, preregistered_by, idempotency_key,
                 strategy_version, code_commit, dataset_version, dataset_hash, holdout_version,
                 metric_definition_version, environment_fingerprint, dependency_lock_hash, seed,
                 period_start, period_end, preregistration_sha256)
            VALUES
                (@experiment_id, @experiment_family_id, @hypothesis, @search_space, @planned_variants, @trial_budget,
                 @primary_metric, @rejection_rule, @benchmark, @preregistered_at, @preregistered_by, @idempotency_key,
                 @strategy_version, @code_commit, @dataset_version, @dataset_hash, @holdout_version,
                 @metric_definition_version, @environment_fingerprint, @dependency_lock_hash, @seed,
                 @period_start, @period_end, @preregistration_sha256)
            ON CONFLICT DO NOTHING
            RETURNING experiment_id;
            """,
            [
                Parameter("experiment_id", NpgsqlDbType.Uuid, preregistration.ExperimentId.Value),
                Parameter("experiment_family_id", NpgsqlDbType.Varchar, preregistration.ExperimentFamilyId),
                Parameter("hypothesis", NpgsqlDbType.Varchar, preregistration.Hypothesis),
                Parameter("search_space", NpgsqlDbType.Varchar, preregistration.SearchSpace),
                Parameter("planned_variants", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(preregistration.PlannedVariants)),
                Parameter("trial_budget", NpgsqlDbType.Integer, preregistration.TrialBudget),
                Parameter("primary_metric", NpgsqlDbType.Varchar, preregistration.PrimaryMetric),
                Parameter("rejection_rule", NpgsqlDbType.Varchar, preregistration.RejectionRule),
                Parameter("benchmark", NpgsqlDbType.Varchar, preregistration.Benchmark),
                Parameter("preregistered_at", NpgsqlDbType.TimestampTz, preregistration.PreregisteredAtUtc.UtcDateTime),
                Parameter("preregistered_by", NpgsqlDbType.Varchar, preregistration.PreregisteredBy),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, preregistration.IdempotencyKey.Value),
                Parameter("strategy_version", NpgsqlDbType.Varchar, preregistration.StrategyVersion),
                Parameter("code_commit", NpgsqlDbType.Varchar, preregistration.CodeCommit),
                Parameter("dataset_version", NpgsqlDbType.Varchar, preregistration.DatasetVersion),
                Parameter("dataset_hash", NpgsqlDbType.Char, preregistration.DatasetHash),
                Parameter("holdout_version", NpgsqlDbType.Varchar, preregistration.HoldoutVersion),
                Parameter("metric_definition_version", NpgsqlDbType.Varchar, preregistration.MetricDefinitionVersion),
                Parameter("environment_fingerprint", NpgsqlDbType.Varchar, preregistration.EnvironmentFingerprint),
                Parameter("dependency_lock_hash", NpgsqlDbType.Char, preregistration.DependencyLockHash),
                Parameter("seed", NpgsqlDbType.Integer, preregistration.Seed),
                Parameter("period_start", NpgsqlDbType.TimestampTz, preregistration.PeriodStartUtc.UtcDateTime),
                Parameter("period_end", NpgsqlDbType.TimestampTz, preregistration.PeriodEndUtc.UtcDateTime),
                Parameter("preregistration_sha256", NpgsqlDbType.Char, preregistration.PreregistrationSha256),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existingHash = await ReadStringAsync(connection, transaction,
                "SELECT preregistration_sha256 FROM trading.experiment_preregistrations WHERE experiment_id = @experiment_id OR idempotency_key = @idempotency_key;",
                [Parameter("experiment_id", NpgsqlDbType.Uuid, preregistration.ExperimentId.Value), Parameter("idempotency_key", NpgsqlDbType.Varchar, preregistration.IdempotencyKey.Value)],
                cancellationToken).ConfigureAwait(false);
            EnsureHashEquivalent(existingHash, preregistration.PreregistrationSha256, "Experiment preregistration");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AppendExperimentTrialAsync(
        ExperimentId experimentId,
        ExperimentTrial trial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trial);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.experiment_trials
                (experiment_id, trial_number, variant_id, parameters_canonical, result_artifact_hash, decision,
                 recorded_at, recorded_by, idempotency_key, trial_sha256)
            VALUES (@experiment_id, @trial_number, @variant_id, @parameters_canonical, @result_artifact_hash, @decision,
                    @recorded_at, @recorded_by, @idempotency_key, @trial_sha256)
            ON CONFLICT DO NOTHING
            RETURNING trial_number;
            """,
            [
                Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value),
                Parameter("trial_number", NpgsqlDbType.Integer, trial.TrialNumber),
                Parameter("variant_id", NpgsqlDbType.Varchar, trial.VariantId),
                Parameter("parameters_canonical", NpgsqlDbType.Varchar, trial.ParametersCanonical),
                Parameter("result_artifact_hash", NpgsqlDbType.Char, trial.ResultArtifactHash),
                Parameter("decision", NpgsqlDbType.Varchar, trial.Decision.ToString()),
                Parameter("recorded_at", NpgsqlDbType.TimestampTz, trial.RecordedAtUtc.UtcDateTime),
                Parameter("recorded_by", NpgsqlDbType.Varchar, trial.RecordedBy),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, trial.IdempotencyKey.Value),
                Parameter("trial_sha256", NpgsqlDbType.Char, trial.TrialSha256),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existingHash = await ReadStringAsync(connection, transaction,
                "SELECT trial_sha256 FROM trading.experiment_trials WHERE (experiment_id = @experiment_id AND trial_number = @trial_number) OR idempotency_key = @idempotency_key;",
                [Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value), Parameter("trial_number", NpgsqlDbType.Integer, trial.TrialNumber), Parameter("idempotency_key", NpgsqlDbType.Varchar, trial.IdempotencyKey.Value)],
                cancellationToken).ConfigureAwait(false);
            EnsureHashEquivalent(existingHash, trial.TrialSha256, "Experiment trial");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AppendHoldoutAccessAuditAsync(
        HoldoutAccessAudit audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.holdout_access_audits
                (event_id, experiment_id, holdout_version, actor, purpose, occurred_at, idempotency_key,
                 approved_by_policy, approval_reference, outcome, contamination_reason, payload_sha256)
            VALUES (@event_id, @experiment_id, @holdout_version, @actor, @purpose, @occurred_at, @idempotency_key,
                    @approved_by_policy, @approval_reference, @outcome, @contamination_reason, @payload_sha256)
            ON CONFLICT DO NOTHING
            RETURNING event_id;
            """,
            [
                Parameter("event_id", NpgsqlDbType.Uuid, audit.EventId),
                Parameter("experiment_id", NpgsqlDbType.Uuid, audit.ExperimentId.Value),
                Parameter("holdout_version", NpgsqlDbType.Varchar, audit.HoldoutVersion),
                Parameter("actor", NpgsqlDbType.Varchar, audit.Actor),
                Parameter("purpose", NpgsqlDbType.Varchar, audit.Purpose),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, audit.OccurredAtUtc.UtcDateTime),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, audit.IdempotencyKey.Value),
                Parameter("approved_by_policy", NpgsqlDbType.Boolean, audit.ApprovedByPolicy),
                Parameter("approval_reference", NpgsqlDbType.Varchar, audit.ApprovalReference),
                Parameter("outcome", NpgsqlDbType.Varchar, audit.Outcome.ToString()),
                Parameter("contamination_reason", NpgsqlDbType.Varchar, audit.ContaminationReason),
                Parameter("payload_sha256", NpgsqlDbType.Char, audit.PayloadSha256),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existingHash = await ReadStringAsync(connection, transaction,
                "SELECT payload_sha256 FROM trading.holdout_access_audits WHERE event_id = @event_id OR idempotency_key = @idempotency_key;",
                [Parameter("event_id", NpgsqlDbType.Uuid, audit.EventId), Parameter("idempotency_key", NpgsqlDbType.Varchar, audit.IdempotencyKey.Value)],
                cancellationToken).ConfigureAwait(false);
            EnsureHashEquivalent(existingHash, audit.PayloadSha256, "Holdout access audit");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AppendHoldoutContaminationAsync(
        ExperimentId experimentId,
        string actor,
        string reason,
        string replacementHoldoutVersion,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Contamination timestamp must be UTC.", nameof(occurredAtUtc));
        }

        var canonical = string.Join('|', "holdout-contamination-v1", experimentId.Value, actor, reason,
            replacementHoldoutVersion, occurredAtUtc.ToString("O"), idempotencyKey.Value);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var eventId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.holdout_contamination_events
                (event_id, experiment_id, actor, reason, replacement_holdout_version, occurred_at, idempotency_key, payload_sha256)
            VALUES (@event_id, @experiment_id, @actor, @reason, @replacement_holdout_version, @occurred_at, @idempotency_key, @payload_sha256)
            ON CONFLICT DO NOTHING
            RETURNING event_id;
            """,
            [
                Parameter("event_id", NpgsqlDbType.Uuid, eventId),
                Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value),
                Parameter("actor", NpgsqlDbType.Varchar, actor),
                Parameter("reason", NpgsqlDbType.Varchar, reason),
                Parameter("replacement_holdout_version", NpgsqlDbType.Varchar, replacementHoldoutVersion),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, occurredAtUtc.UtcDateTime),
                Parameter("idempotency_key", NpgsqlDbType.Varchar, idempotencyKey.Value),
                Parameter("payload_sha256", NpgsqlDbType.Char, payloadHash),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existingHash = await ReadStringAsync(connection, transaction,
                "SELECT payload_sha256 FROM trading.holdout_contamination_events WHERE idempotency_key = @idempotency_key;",
                [Parameter("idempotency_key", NpgsqlDbType.Varchar, idempotencyKey.Value)],
                cancellationToken).ConfigureAwait(false);
            EnsureHashEquivalent(existingHash, payloadHash, "Holdout contamination event");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ExperimentGovernanceSnapshot?> ReadExperimentGovernanceSnapshotAsync(
        ExperimentId experimentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using var preregistrationCommand = new NpgsqlCommand(
            """
            SELECT experiment_family_id, hypothesis, search_space, planned_variants, trial_budget,
                   primary_metric, rejection_rule, benchmark, preregistered_at, preregistered_by,
                   idempotency_key, strategy_version, code_commit, dataset_version, dataset_hash,
                   holdout_version, metric_definition_version, environment_fingerprint, dependency_lock_hash,
                   seed, period_start, period_end, preregistration_sha256
            FROM trading.experiment_preregistrations
            WHERE experiment_id = @experiment_id;
            """, connection, transaction);
        preregistrationCommand.Parameters.Add(Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value));
        await using var preregistrationReader = await preregistrationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await preregistrationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var preregistration = new ExperimentPreregistration(
            experimentId,
            preregistrationReader.GetString(0),
            preregistrationReader.GetString(1),
            preregistrationReader.GetString(2),
            JsonSerializer.Deserialize<string[]>(preregistrationReader.GetString(3)) ?? throw new InvalidOperationException("Stored planned variants are invalid."),
            preregistrationReader.GetInt32(4),
            preregistrationReader.GetString(5),
            preregistrationReader.GetString(6),
            preregistrationReader.GetString(7),
            Utc(preregistrationReader.GetFieldValue<DateTime>(8)),
            preregistrationReader.GetString(9),
            new IdempotencyKey(preregistrationReader.GetString(10)),
            preregistrationReader.GetString(11),
            preregistrationReader.GetString(12),
            preregistrationReader.GetString(13),
            preregistrationReader.GetString(14).Trim(),
            preregistrationReader.GetString(15),
            preregistrationReader.GetString(16),
            preregistrationReader.GetString(17),
            preregistrationReader.GetString(18).Trim(),
            preregistrationReader.GetInt32(19),
            Utc(preregistrationReader.GetFieldValue<DateTime>(20)),
            Utc(preregistrationReader.GetFieldValue<DateTime>(21)));
        if (!string.Equals(preregistration.PreregistrationSha256, preregistrationReader.GetString(22).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Stored experiment preregistration hash is invalid.");
        }
        await preregistrationReader.CloseAsync().ConfigureAwait(false);

        var trials = new List<ExperimentTrial>();
        await using (var trialsCommand = new NpgsqlCommand(
            """
            SELECT trial_number, variant_id, parameters_canonical, result_artifact_hash, decision,
                   recorded_at, recorded_by, idempotency_key, trial_sha256
            FROM trading.experiment_trials
            WHERE experiment_id = @experiment_id
            ORDER BY trial_number;
            """, connection, transaction))
        {
            trialsCommand.Parameters.Add(Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value));
            await using var trialsReader = await trialsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await trialsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var trial = new ExperimentTrial(
                    trialsReader.GetInt32(0), trialsReader.GetString(1), trialsReader.GetString(2),
                    trialsReader.GetString(3).Trim(), Enum.Parse<ExperimentDecision>(trialsReader.GetString(4)),
                    Utc(trialsReader.GetFieldValue<DateTime>(5)), trialsReader.GetString(6), new IdempotencyKey(trialsReader.GetString(7)));
                if (!string.Equals(trial.TrialSha256, trialsReader.GetString(8).Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Stored experiment trial {trial.TrialNumber} has an invalid hash.");
                }
                trials.Add(trial);
            }
        }

        var accessCount = await ExecuteScalarAsync(connection, transaction,
            "SELECT count(*) FROM trading.holdout_access_audits WHERE experiment_id = @experiment_id;",
            [Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value)], cancellationToken).ConfigureAwait(false);
        string? replacement = null;
        string? reason = null;
        await using (var contaminationCommand = new NpgsqlCommand(
            """
            SELECT reason, replacement_holdout_version
            FROM trading.holdout_contamination_events
            WHERE experiment_id = @experiment_id
            ORDER BY occurred_at DESC, event_id DESC
            LIMIT 1;
            """, connection, transaction))
        {
            contaminationCommand.Parameters.Add(Parameter("experiment_id", NpgsqlDbType.Uuid, experimentId.Value));
            await using var contaminationReader = await contaminationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await contaminationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reason = contaminationReader.GetString(0);
                replacement = contaminationReader.GetString(1);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExperimentGovernanceSnapshot(
            preregistration,
            trials,
            Convert.ToInt32(accessCount, System.Globalization.CultureInfo.InvariantCulture),
            replacement is not null,
            replacement,
            reason);
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

    /// <summary>
    /// Atomically records an execution fact, balanced ledger postings, average-cost
    /// position state and known-trade evidence. Replays are immutable and idempotent.
    /// </summary>
    public async Task<ExecutionAccountingResult> RecordExecutionAsync(
        ExecutionAccountingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = ExecutionAccounting.CreateLedgerEntry(command);
        var feeComparison = ExecutionFeeDriftEvaluator.Compare(
            command.FeeEstimate,
            command.Commission,
            command.Quantity,
            command.Price,
            command.BaseAsset,
            command.QuoteAsset,
            command.OccurredAtUtc,
            command.FeeDriftToleranceBps,
            command.FeeAssetValuation);
        var qualityComparison = ExecutionQualityEvaluator.Compare(
            command.FeeEstimate,
            command.Side,
            command.Quantity,
            command.CumulativeExecutedQuantity,
            command.Price,
            command.OccurredAtUtc);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (var existingCommand = new NpgsqlCommand(
            "SELECT ledger_entry_id, realized_pnl_quote, average_cost_quote FROM trading.execution_fills WHERE execution_id = @execution_id;",
            connection, transaction))
        {
            existingCommand.Parameters.Add(Parameter("execution_id", NpgsqlDbType.Varchar, command.ExecutionId));
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingEntryId = new LedgerEntryId(reader.GetGuid(0));
                decimal? realized = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                decimal? existingAverageCost = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                await reader.DisposeAsync().ConfigureAwait(false);
                if (existingEntryId != entry.Id)
                {
                    throw new InvalidOperationException("Execution identity is already associated with a different ledger entry.");
                }

                var existingEntry = await ReadExistingJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Execution record exists without its journal entry.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(command.ExecutionId, existingEntry, realized, existingAverageCost, true, feeComparison, qualityComparison);
            }
        }

        decimal currentQuantity = 0m;
        decimal currentCost = 0m;
        await using (var positionCommand = new NpgsqlCommand(
            "SELECT base_asset, quote_asset, quantity, cost_quote FROM trading.position_cost_basis WHERE symbol = @symbol FOR UPDATE;",
            connection, transaction))
        {
            positionCommand.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value));
            await using var reader = await positionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), command.BaseAsset.Value, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1), command.QuoteAsset.Value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Position basis for {command.Symbol} has inconsistent assets.");
                }

                currentQuantity = reader.GetDecimal(2);
                currentCost = reader.GetDecimal(3);
            }
        }

        var commissionInBase = command.Commission.Asset == command.BaseAsset ? command.Commission.Amount : 0m;
        var requiredQuantity = command.Quantity.Value + commissionInBase;
        if (command.Side == OrderSide.Sell && currentQuantity < requiredQuantity)
        {
            var excess = requiredQuantity - currentQuantity;
            currentQuantity = requiredQuantity;
            currentCost += excess * command.Price.Value;
        }

        var update = ExecutionAccounting.ApplyAverageCost(command, currentQuantity, currentCost);
        decimal? averageCost = update.Quantity == 0m ? null : update.CostQuote / update.Quantity;
        var inserted = await InsertJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        if (!inserted)
        {
            var existing = await ReadExistingJournalEntryAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Journal uniqueness conflict did not return an entry.");
            throw new InvalidOperationException($"Ledger entry {entry.Id} already exists before execution fact {command.ExecutionId}.");
        }

        await InsertLedgerPostingsAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.execution_fills
                (execution_id, trade_id, order_id, symbol, base_asset, quote_asset, side,
                 quantity, price, commission, commission_asset, occurred_at, ledger_entry_id,
                 realized_pnl_quote, average_cost_quote, client_order_id, estimated_rate,
                 fee_snapshot_hash, fee_source, fee_observed_at, fee_valid_until,
                 expected_commission_quote, actual_commission_quote, fee_deviation_bps,
                 fee_comparison_status, fee_comparison_reason, commission_valuation_source, commission_valuation_at,
                 quality_reference_price, quality_authorized_quantity, quality_decision_at,
                 cumulative_fill_ratio, implementation_shortfall_quote, decision_to_fill_latency_ms,
                 execution_quality_status, execution_quality_reason)
            VALUES
                 (@execution_id, @trade_id, @order_id, @symbol, @base_asset, @quote_asset, @side,
                 @quantity, @price, @commission, @commission_asset, @occurred_at, @ledger_entry_id,
                 @realized_pnl_quote, @average_cost_quote, @client_order_id, @estimated_rate,
                 @fee_snapshot_hash, @fee_source, @fee_observed_at, @fee_valid_until,
                 @expected_commission_quote, @actual_commission_quote, @fee_deviation_bps,
                 @fee_comparison_status, @fee_comparison_reason, @commission_valuation_source, @commission_valuation_at,
                 @quality_reference_price, @quality_authorized_quantity, @quality_decision_at,
                 @cumulative_fill_ratio, @implementation_shortfall_quote, @decision_to_fill_latency_ms,
                 @execution_quality_status, @execution_quality_reason);
            """,
            [
                Parameter("execution_id", NpgsqlDbType.Varchar, command.ExecutionId),
                Parameter("trade_id", NpgsqlDbType.Bigint, command.TradeId),
                Parameter("order_id", NpgsqlDbType.Bigint, command.OrderId),
                Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value),
                Parameter("base_asset", NpgsqlDbType.Varchar, command.BaseAsset.Value),
                Parameter("quote_asset", NpgsqlDbType.Varchar, command.QuoteAsset.Value),
                Parameter("side", NpgsqlDbType.Varchar, command.Side == OrderSide.Buy ? "BUY" : "SELL"),
                Parameter("quantity", NpgsqlDbType.Numeric, command.Quantity.Value),
                Parameter("price", NpgsqlDbType.Numeric, command.Price.Value),
                Parameter("commission", NpgsqlDbType.Numeric, command.Commission.Amount),
                Parameter("commission_asset", NpgsqlDbType.Varchar, command.Commission.Asset.Value),
                Parameter("occurred_at", NpgsqlDbType.TimestampTz, command.OccurredAtUtc.UtcDateTime),
                Parameter("ledger_entry_id", NpgsqlDbType.Uuid, entry.Id.Value),
                Parameter("realized_pnl_quote", NpgsqlDbType.Numeric, update.RealizedPnlQuote),
                Parameter("average_cost_quote", NpgsqlDbType.Numeric, averageCost),
                Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId),
                Parameter("estimated_rate", NpgsqlDbType.Numeric, command.FeeEstimate?.Rate),
                Parameter("fee_snapshot_hash", NpgsqlDbType.Varchar, command.FeeEstimate?.SnapshotHash),
                Parameter("fee_source", NpgsqlDbType.Varchar, command.FeeEstimate?.Source),
                Parameter("fee_observed_at", NpgsqlDbType.TimestampTz, command.FeeEstimate?.ObservedAtUtc.UtcDateTime),
                Parameter("fee_valid_until", NpgsqlDbType.TimestampTz, command.FeeEstimate?.ValidUntilUtc.UtcDateTime),
                Parameter("expected_commission_quote", NpgsqlDbType.Numeric, feeComparison.ExpectedQuote),
                Parameter("actual_commission_quote", NpgsqlDbType.Numeric, feeComparison.ActualQuote),
                Parameter("fee_deviation_bps", NpgsqlDbType.Numeric, feeComparison.DeviationBps),
                Parameter("fee_comparison_status", NpgsqlDbType.Varchar, FeeComparisonStatus(feeComparison.Status)),
                Parameter("fee_comparison_reason", NpgsqlDbType.Varchar, feeComparison.Reason),
                Parameter("commission_valuation_source", NpgsqlDbType.Varchar, feeComparison.ValuationSource),
                Parameter("commission_valuation_at", NpgsqlDbType.TimestampTz, feeComparison.ValuationAtUtc?.UtcDateTime),
                Parameter("quality_reference_price", NpgsqlDbType.Numeric, command.FeeEstimate?.ReferencePrice),
                Parameter("quality_authorized_quantity", NpgsqlDbType.Numeric, command.FeeEstimate?.AuthorizedQuantity),
                Parameter("quality_decision_at", NpgsqlDbType.TimestampTz, command.FeeEstimate?.DecisionAtUtc?.UtcDateTime),
                Parameter("cumulative_fill_ratio", NpgsqlDbType.Numeric, qualityComparison.FillRatio),
                Parameter("implementation_shortfall_quote", NpgsqlDbType.Numeric, qualityComparison.ImplementationShortfallQuote),
                Parameter("decision_to_fill_latency_ms", NpgsqlDbType.Bigint, qualityComparison.DecisionToFillLatencyMilliseconds),
                Parameter("execution_quality_status", NpgsqlDbType.Varchar, qualityComparison.Status),
                Parameter("execution_quality_reason", NpgsqlDbType.Varchar, qualityComparison.Reason),
            ], cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.position_cost_basis (symbol, base_asset, quote_asset, quantity, cost_quote)
            VALUES (@symbol, @base_asset, @quote_asset, @quantity, @cost_quote)
            ON CONFLICT (symbol) DO UPDATE SET
                quantity = EXCLUDED.quantity,
                cost_quote = EXCLUDED.cost_quote,
                updated_at = transaction_timestamp();
            """,
            [
                Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value),
                Parameter("base_asset", NpgsqlDbType.Varchar, command.BaseAsset.Value),
                Parameter("quote_asset", NpgsqlDbType.Varchar, command.QuoteAsset.Value),
                Parameter("quantity", NpgsqlDbType.Numeric, update.Quantity),
                Parameter("cost_quote", NpgsqlDbType.Numeric, update.CostQuote),
            ], cancellationToken).ConfigureAwait(false);

        if (command.TradeId is { } tradeId)
        {
            await InsertKnownTradeAsync(connection, transaction, tradeId, command.Symbol.Value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(command.ExecutionId, entry, update.RealizedPnlQuote, averageCost, false, feeComparison, qualityComparison);
    }

    public async Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(
        Symbol symbol,
        Asset quoteAsset,
        DateTimeOffset dayStartUtc,
        CancellationToken cancellationToken = default)
    {
        if (dayStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Risk-context boundaries must be UTC.", nameof(dayStartUtc));
        }

        var dayEndUtc = dayStartUtc.AddDays(1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                COALESCE(SUM(CASE WHEN realized_pnl_quote < 0 THEN -realized_pnl_quote ELSE 0 END), 0),
                COALESCE((SELECT quantity FROM trading.position_cost_basis WHERE symbol = @symbol), 0),
                COALESCE((SELECT cost_quote FROM trading.position_cost_basis WHERE symbol = @symbol), 0),
                EXISTS (SELECT 1 FROM trading.position_cost_basis WHERE symbol = @symbol)
            FROM trading.execution_fills
            WHERE symbol = @symbol
              AND quote_asset = @quote_asset
              AND occurred_at >= @day_start
              AND occurred_at < @day_end;
            """, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, quoteAsset.Value));
        command.Parameters.Add(Parameter("day_start", NpgsqlDbType.TimestampTz, dayStartUtc.UtcDateTime));
        command.Parameters.Add(Parameter("day_end", NpgsqlDbType.TimestampTz, dayEndUtc.UtcDateTime));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL returned no autonomy risk-context row.");
        }

        return new(
            reader.GetDecimal(0),
            reader.GetDecimal(2),
            reader.GetDecimal(1),
            reader.GetBoolean(3),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Atomically reads accounting inputs and advances the restart-safe equity
    /// high-water-mark projection. The first observation is persisted but is
    /// intentionally incomplete, requiring one subsequent coherent valuation
    /// before autonomous entries can be authorized.
    /// </summary>
    public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(
        string scope,
        Symbol symbol,
        Asset quoteAsset,
        DateTimeOffset dayStartUtc,
        decimal? currentEquityNotional,
        DateTimeOffset valuationAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteWithRetryAsync(ct => RecordAutonomyRiskContextCoreAsync(scope, symbol, quoteAsset, dayStartUtc, currentEquityNotional, valuationAtUtc, ct), cancellationToken: cancellationToken);

    private async Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextCoreAsync(
        string scope,
        Symbol symbol,
        Asset quoteAsset,
        DateTimeOffset dayStartUtc,
        decimal? currentEquityNotional,
        DateTimeOffset valuationAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (dayStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Risk-context boundaries must be UTC.", nameof(dayStartUtc));
        }

        if (valuationAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Equity valuations must be UTC.", nameof(valuationAtUtc));
        }

        if (currentEquityNotional is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(currentEquityNotional), "Equity cannot be negative.");
        }

        var dayEndUtc = dayStartUtc.AddDays(1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        decimal realizedDailyLoss;
        decimal positionCost;
        decimal positionQuantity;
        bool hasPositionBasis;
        await using (var accountingCommand = new NpgsqlCommand(
            """
            SELECT
                COALESCE(SUM(CASE WHEN realized_pnl_quote < 0 THEN -realized_pnl_quote ELSE 0 END), 0),
                COALESCE((SELECT quantity FROM trading.position_cost_basis WHERE symbol = @symbol), 0),
                COALESCE((SELECT cost_quote FROM trading.position_cost_basis WHERE symbol = @symbol), 0),
                EXISTS (SELECT 1 FROM trading.position_cost_basis WHERE symbol = @symbol)
            FROM trading.execution_fills
            WHERE symbol = @symbol
              AND quote_asset = @quote_asset
              AND occurred_at >= @day_start
              AND occurred_at < @day_end;
            """, connection, transaction))
        {
            accountingCommand.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol.Value));
            accountingCommand.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, quoteAsset.Value));
            accountingCommand.Parameters.Add(Parameter("day_start", NpgsqlDbType.TimestampTz, dayStartUtc.UtcDateTime));
            accountingCommand.Parameters.Add(Parameter("day_end", NpgsqlDbType.TimestampTz, dayEndUtc.UtcDateTime));
            await using var reader = await accountingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL returned no autonomy risk-context row.");
            }

            realizedDailyLoss = reader.GetDecimal(0);
            positionQuantity = reader.GetDecimal(1);
            positionCost = reader.GetDecimal(2);
            hasPositionBasis = reader.GetBoolean(3);
        }

        EquityHighWaterMarkState? existingState = null;
        await using (var hwmCommand = new NpgsqlCommand(
            """
            SELECT equity_notional, high_water_mark_notional, drawdown_notional,
                   last_valuation_at, observation_count
            FROM trading.autonomy_equity_high_water_marks
            WHERE scope = @scope
            FOR UPDATE;
            """, connection, transaction))
        {
            hwmCommand.Parameters.Add(Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()));
            await using var reader = await hwmCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingState = new EquityHighWaterMarkState(
                    reader.GetDecimal(0),
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                    reader.GetInt64(4));
            }
        }

        EquityHighWaterMarkUpdate? update = null;
        if (currentEquityNotional is { } equity)
        {
            update = EquityHighWaterMarkCalculator.Apply(equity, valuationAtUtc, existingState);
            if (update.IsComplete || existingState is null)
            {
                if (existingState is null)
                {
                    await ExecuteAsync(connection, transaction,
                        """
                        INSERT INTO trading.autonomy_equity_high_water_marks
                            (scope, equity_notional, high_water_mark_notional, drawdown_notional,
                             last_valuation_at, observation_count)
                        VALUES
                            (@scope, @equity, @high_water_mark, @drawdown, @valuation_at, @observation_count);
                        """,
                        [
                            Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()),
                            Parameter("equity", NpgsqlDbType.Numeric, update.State.EquityNotional),
                            Parameter("high_water_mark", NpgsqlDbType.Numeric, update.State.HighWaterMarkNotional),
                            Parameter("drawdown", NpgsqlDbType.Numeric, update.State.DrawdownNotional),
                            Parameter("valuation_at", NpgsqlDbType.TimestampTz, update.State.LastValuationAtUtc.UtcDateTime),
                            Parameter("observation_count", NpgsqlDbType.Bigint, update.State.ObservationCount),
                        ], cancellationToken).ConfigureAwait(false);
                }
                else if (update.State != existingState)
                {
                    await ExecuteAsync(connection, transaction,
                        """
                        UPDATE trading.autonomy_equity_high_water_marks
                        SET equity_notional = @equity,
                            high_water_mark_notional = @high_water_mark,
                            drawdown_notional = @drawdown,
                            last_valuation_at = @valuation_at,
                            observation_count = @observation_count,
                            updated_at = transaction_timestamp()
                        WHERE scope = @scope;
                        """,
                        [
                            Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()),
                            Parameter("equity", NpgsqlDbType.Numeric, update.State.EquityNotional),
                            Parameter("high_water_mark", NpgsqlDbType.Numeric, update.State.HighWaterMarkNotional),
                            Parameter("drawdown", NpgsqlDbType.Numeric, update.State.DrawdownNotional),
                            Parameter("valuation_at", NpgsqlDbType.TimestampTz, update.State.LastValuationAtUtc.UtcDateTime),
                            Parameter("observation_count", NpgsqlDbType.Bigint, update.State.ObservationCount),
                        ], cancellationToken).ConfigureAwait(false);
                }

                if (existingState is null || update.State != existingState)
                {
                    await ExecuteAsync(connection, transaction,
                        """
                        INSERT INTO trading.autonomy_equity_observations
                            (scope, equity_notional, high_water_mark_notional, drawdown_notional, valuation_at)
                        VALUES
                            (@scope, @equity, @high_water_mark, @drawdown, @valuation_at);
                        """,
                        [
                            Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()),
                            Parameter("equity", NpgsqlDbType.Numeric, update.State.EquityNotional),
                            Parameter("high_water_mark", NpgsqlDbType.Numeric, update.State.HighWaterMarkNotional),
                            Parameter("drawdown", NpgsqlDbType.Numeric, update.State.DrawdownNotional),
                            Parameter("valuation_at", NpgsqlDbType.TimestampTz, update.State.LastValuationAtUtc.UtcDateTime),
                        ], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var state = update?.State ?? existingState;
        var complete = update?.IsComplete == true;
        var reason = update?.UnavailableReason
            ?? (state is null ? "EQUITY_VALUATION_UNAVAILABLE" : "EQUITY_VALUATION_UNAVAILABLE");
        return new(
            realizedDailyLoss,
            positionCost,
            positionQuantity,
            hasPositionBasis,
            DateTimeOffset.UtcNow,
            state?.EquityNotional,
            state?.HighWaterMarkNotional,
            state?.DrawdownNotional,
            state is not null,
            complete,
            complete ? null : reason);
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
            "INSERT INTO trading.reconciliation_known_trades (trade_id, symbol) " +
            "VALUES (@trade_id, @symbol) ON CONFLICT (symbol, trade_id) DO NOTHING;", connection);
        command.Parameters.Add(Parameter("trade_id", NpgsqlDbType.Bigint, tradeId));
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, normalizedSymbol));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>Reads immutable trade identities used by account reconciliation.</summary>
    public async Task<IReadOnlySet<KnownReconciliationTrade>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
        "SELECT symbol, trade_id FROM trading.reconciliation_known_trades ORDER BY symbol, trade_id;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var identities = new HashSet<KnownReconciliationTrade>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            identities.Add(new(reader.GetString(0), reader.GetInt64(1)));
        }

        return identities;
    }

    public async Task<bool> AppendReconciliationEventAsync(
        ReconciliationEventRecord record,
        CancellationToken cancellationToken = default)
    {
        ValidateReconciliationEvent(record);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.reconciliation_events
                (event_id, correlation_id, occurred_at, event_type, schema_version, payload, payload_sha256)
            VALUES
                (@event_id, @correlation_id, @occurred_at, @event_type, @schema_version, @payload, @payload_sha256)
            ON CONFLICT (event_id) DO NOTHING;
            """, connection);
        command.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, record.EventId));
        command.Parameters.Add(Parameter("correlation_id", NpgsqlDbType.Uuid, record.CorrelationId));
        command.Parameters.Add(Parameter("occurred_at", NpgsqlDbType.TimestampTz, record.OccurredAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("event_type", NpgsqlDbType.Varchar, record.EventType.Trim()));
        command.Parameters.Add(Parameter("schema_version", NpgsqlDbType.Smallint, record.SchemaVersion));
        command.Parameters.Add(Parameter("payload", NpgsqlDbType.Jsonb, record.PayloadJson));
        command.Parameters.Add(Parameter("payload_sha256", NpgsqlDbType.Char, record.PayloadSha256.ToUpperInvariant()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return true;
        }

        await using var existingCommand = new NpgsqlCommand(
            "SELECT correlation_id, occurred_at = @occurred_at, event_type, schema_version, payload_sha256 " +
            "FROM trading.reconciliation_events WHERE event_id = @event_id;", connection);
        existingCommand.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, record.EventId));
        existingCommand.Parameters.Add(Parameter("occurred_at", NpgsqlDbType.TimestampTz, record.OccurredAtUtc.UtcDateTime));
        await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetGuid(0) != record.CorrelationId ||
            !reader.GetBoolean(1) ||
            !string.Equals(reader.GetString(2), record.EventType.Trim(), StringComparison.Ordinal) ||
            reader.GetInt16(3) != record.SchemaVersion ||
            !string.Equals(reader.GetString(4).Trim(), record.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Reconciliation event identity conflicts with previously persisted evidence.");
        }

        return false;
    }

    public async Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT symbol, order_id, client_order_id, status, updated_at, original_quantity, cumulative_executed_quantity FROM trading.reconciliation_open_orders WHERE active = TRUE ORDER BY client_order_id;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LocalOpenOrderRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
        }

        return result;
    }

    public async Task<bool> ApplyAccountStreamExecutionReportAsync(
        LocalOpenOrderRecord order,
        bool isTerminal,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenOrder(order);
        ValidateAccountStreamStatus(order.Status, isTerminal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.reconciliation_open_orders
                (symbol, order_id, client_order_id, status, updated_at, original_quantity, cumulative_executed_quantity, active, exchange_updated_at)
            VALUES
                (@symbol, @order_id, @client_order_id, @status, @updated_at, @original_quantity, @cumulative_executed_quantity, @active, @exchange_updated_at)
            ON CONFLICT (client_order_id) DO UPDATE SET
                symbol = EXCLUDED.symbol,
                order_id = COALESCE(EXCLUDED.order_id, trading.reconciliation_open_orders.order_id),
                status = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at,
                original_quantity = COALESCE(EXCLUDED.original_quantity, trading.reconciliation_open_orders.original_quantity),
                cumulative_executed_quantity = COALESCE(EXCLUDED.cumulative_executed_quantity, trading.reconciliation_open_orders.cumulative_executed_quantity),
                active = EXCLUDED.active,
                exchange_updated_at = EXCLUDED.exchange_updated_at
            WHERE trading.reconciliation_open_orders.active = TRUE
              AND EXCLUDED.exchange_updated_at >= COALESCE(
                    trading.reconciliation_open_orders.exchange_updated_at,
                    trading.reconciliation_open_orders.updated_at)
              AND (
                    EXCLUDED.exchange_updated_at > COALESCE(
                        trading.reconciliation_open_orders.exchange_updated_at,
                        trading.reconciliation_open_orders.updated_at)
                    OR EXCLUDED.status = trading.reconciliation_open_orders.status
                    OR EXCLUDED.active = FALSE
                    OR (EXCLUDED.status IN ('PARTIALLY_FILLED', 'PENDING_CANCEL')
                        AND COALESCE(EXCLUDED.cumulative_executed_quantity, 0)
                            > COALESCE(trading.reconciliation_open_orders.cumulative_executed_quantity, 0))
              )
              AND trading.reconciliation_open_orders.symbol = EXCLUDED.symbol
              AND (trading.reconciliation_open_orders.order_id IS NULL OR EXCLUDED.order_id IS NULL
                   OR trading.reconciliation_open_orders.order_id = EXCLUDED.order_id)
              AND (trading.reconciliation_open_orders.original_quantity IS NULL OR EXCLUDED.original_quantity IS NULL
                   OR trading.reconciliation_open_orders.original_quantity = EXCLUDED.original_quantity)
              AND COALESCE(EXCLUDED.cumulative_executed_quantity, trading.reconciliation_open_orders.cumulative_executed_quantity, 0)
                  >= COALESCE(trading.reconciliation_open_orders.cumulative_executed_quantity, 0);
            """, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, order.Symbol.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("order_id", NpgsqlDbType.Bigint, order.OrderId.HasValue ? order.OrderId.Value : DBNull.Value));
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
        command.Parameters.Add(Parameter("status", NpgsqlDbType.Varchar, order.Status.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("updated_at", NpgsqlDbType.TimestampTz, order.UpdatedAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("original_quantity", NpgsqlDbType.Numeric, order.OriginalQuantity.HasValue ? order.OriginalQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("cumulative_executed_quantity", NpgsqlDbType.Numeric, order.CumulativeExecutedQuantity.HasValue ? order.CumulativeExecutedQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("active", NpgsqlDbType.Boolean, !isTerminal));
        command.Parameters.Add(Parameter("exchange_updated_at", NpgsqlDbType.TimestampTz, (order.ExchangeUpdatedAtUtc ?? order.UpdatedAtUtc).UtcDateTime));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        var mappedLifecycleStatus = order.Status.Trim().ToUpperInvariant() switch
        {
            "FILLED" => "Filled",
            "PARTIALLY_FILLED" => "PartiallyFilled",
            "CANCELED" or "CANCELLED" => "Cancelled",
            "REJECTED" => "Rejected",
            "EXPIRED" or "EXPIRED_IN_MATCH" => "Expired",
            "PENDING_CANCEL" => "CancelPending",
            "NEW" => "Acknowledged",
            _ => null
        };
        if (mappedLifecycleStatus is not null)
        {
            var exchangeOrderIdText = order.OrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var streamIdempotencyKey = $"stream:{order.ClientOrderId.Trim()}:{mappedLifecycleStatus}:{exchangeOrderIdText ?? "none"}";
            await using var lifecycleCommand = new NpgsqlCommand(
                """
                INSERT INTO trading.order_lifecycle_events
                    (event_id, client_order_id, from_status, to_status, idempotency_key, occurred_at, exchange_order_id)
                SELECT
                    @event_id,
                    @client_order_id,
                    latest.to_status,
                    @to_status,
                    @idempotency_key,
                    @occurred_at,
                    @exchange_order_id
                FROM (
                    SELECT to_status
                    FROM trading.order_lifecycle_events
                    WHERE client_order_id = @client_order_id
                    ORDER BY occurred_at DESC, event_id DESC
                    LIMIT 1
                ) AS latest
                WHERE latest.to_status <> @to_status
                  AND latest.to_status NOT IN ('Filled', 'Cancelled', 'Rejected', 'Expired')
                ON CONFLICT (idempotency_key) DO NOTHING;
                """,
                connection);
            lifecycleCommand.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, Guid.NewGuid()));
            lifecycleCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
            lifecycleCommand.Parameters.Add(Parameter("to_status", NpgsqlDbType.Varchar, mappedLifecycleStatus));
            lifecycleCommand.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, streamIdempotencyKey));
            lifecycleCommand.Parameters.Add(Parameter("occurred_at", NpgsqlDbType.TimestampTz, (order.ExchangeUpdatedAtUtc ?? order.UpdatedAtUtc).UtcDateTime));
            lifecycleCommand.Parameters.Add(Parameter("exchange_order_id", NpgsqlDbType.Varchar, exchangeOrderIdText));
            _ = await lifecycleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (isTerminal)
        {
            await using var releaseCommand = new NpgsqlCommand(
                """
                UPDATE trading.capital_reservations
                SET status = 'Released',
                    updated_at = transaction_timestamp(),
                    reason = 'TERMINAL_ORDER_RELEASE'
                WHERE client_order_id = @client_order_id
                  AND status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled');
                """,
                connection);
            releaseCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
            _ = await releaseCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<bool> ApplyAuthoritativeOpenOrderSnapshotAsync(
        LocalOpenOrderRecord order,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenOrder(order);
        if (order.ExchangeUpdatedAtUtc is null)
        {
            throw new ArgumentException("An authoritative exchange order update timestamp is required.", nameof(order));
        }

        if (order.Status.Trim().ToUpperInvariant() is not ("NEW" or "PARTIALLY_FILLED" or "PENDING_CANCEL"))
        {
            throw new ArgumentException("An authoritative open-order snapshot must contain an open status.", nameof(order));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE trading.reconciliation_open_orders SET
                symbol = @symbol,
                order_id = COALESCE(@order_id, order_id),
                status = @status,
                updated_at = @updated_at,
                original_quantity = COALESCE(@original_quantity, original_quantity),
                cumulative_executed_quantity = COALESCE(@cumulative_executed_quantity, cumulative_executed_quantity),
                exchange_updated_at = @exchange_updated_at
            WHERE client_order_id = @client_order_id
              AND active = TRUE
              AND symbol = @symbol
              AND (@order_id IS NULL OR order_id IS NULL OR order_id = @order_id)
              AND (original_quantity IS NULL OR @original_quantity IS NULL OR original_quantity = @original_quantity)
              AND COALESCE(@cumulative_executed_quantity, cumulative_executed_quantity, 0)
                  >= COALESCE(cumulative_executed_quantity, 0)
              AND @exchange_updated_at >= COALESCE(exchange_updated_at, updated_at);
            """, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, order.Symbol.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("order_id", NpgsqlDbType.Bigint, order.OrderId.HasValue ? order.OrderId.Value : DBNull.Value));
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
        command.Parameters.Add(Parameter("status", NpgsqlDbType.Varchar, order.Status.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("updated_at", NpgsqlDbType.TimestampTz, order.UpdatedAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("original_quantity", NpgsqlDbType.Numeric, order.OriginalQuantity.HasValue ? order.OriginalQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("cumulative_executed_quantity", NpgsqlDbType.Numeric, order.CumulativeExecutedQuantity.HasValue ? order.CumulativeExecutedQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("exchange_updated_at", NpgsqlDbType.TimestampTz, order.ExchangeUpdatedAtUtc.Value.UtcDateTime));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
    {
        ValidateOpenOrder(order);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.reconciliation_open_orders (symbol, order_id, client_order_id, status, updated_at, original_quantity, cumulative_executed_quantity, active, exchange_updated_at)
            VALUES (@symbol, @order_id, @client_order_id, @status, @updated_at, @original_quantity, @cumulative_executed_quantity, TRUE, @exchange_updated_at)
            ON CONFLICT (client_order_id) DO UPDATE SET
                symbol = EXCLUDED.symbol, order_id = EXCLUDED.order_id, status = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at, original_quantity = EXCLUDED.original_quantity,
                cumulative_executed_quantity = EXCLUDED.cumulative_executed_quantity,
                exchange_updated_at = COALESCE(EXCLUDED.exchange_updated_at, trading.reconciliation_open_orders.exchange_updated_at),
                active = TRUE
            WHERE trading.reconciliation_open_orders.active = TRUE
              AND EXCLUDED.updated_at >= trading.reconciliation_open_orders.updated_at;
            """, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, order.Symbol.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("order_id", NpgsqlDbType.Bigint, order.OrderId.HasValue ? order.OrderId.Value : DBNull.Value));
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
        command.Parameters.Add(Parameter("status", NpgsqlDbType.Varchar, order.Status.Trim().ToUpperInvariant()));
        command.Parameters.Add(Parameter("updated_at", NpgsqlDbType.TimestampTz, order.UpdatedAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("original_quantity", NpgsqlDbType.Numeric, order.OriginalQuantity.HasValue ? order.OriginalQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("cumulative_executed_quantity", NpgsqlDbType.Numeric, order.CumulativeExecutedQuantity.HasValue ? order.CumulativeExecutedQuantity.Value : DBNull.Value));
        command.Parameters.Add(Parameter("exchange_updated_at", NpgsqlDbType.TimestampTz, order.ExchangeUpdatedAtUtc.HasValue ? order.ExchangeUpdatedAtUtc.Value.UtcDateTime : DBNull.Value));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(order.Status.Trim(), "UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            await using var unknownCommand = new NpgsqlCommand(
                """
                UPDATE trading.capital_reservations
                SET status = 'LockedUnknown',
                    updated_at = transaction_timestamp(),
                    reason = 'ORDER_STATE_LOCKED_UNKNOWN'
                WHERE client_order_id = @client_order_id
                  AND status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled');
                """,
                connection);
            unknownCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, order.ClientOrderId.Trim()));
            _ = await unknownCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE trading.reconciliation_open_orders SET active = FALSE, updated_at = transaction_timestamp() WHERE client_order_id = @client_order_id;
            UPDATE trading.capital_reservations
            SET status = 'Released',
                updated_at = transaction_timestamp(),
                reason = 'TERMINAL_ORDER_RELEASE'
            WHERE client_order_id = @client_order_id
              AND status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled');
            """,
            connection);
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, clientOrderId.Trim()));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOpenOrder(LocalOpenOrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.Symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.ClientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.Status);
        if (order.OrderId is < 0 || order.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            (order.ExchangeUpdatedAtUtc.HasValue && order.ExchangeUpdatedAtUtc.Value.Offset != TimeSpan.Zero) ||
            order.OriginalQuantity is <= 0 || order.CumulativeExecutedQuantity is < 0 ||
            (order.OriginalQuantity is { } original && order.CumulativeExecutedQuantity is { } cumulative && cumulative > original))
        {
            throw new ArgumentException("Open order identity, quantities, or timestamp are invalid.", nameof(order));
        }
    }

    private static void ValidateAccountStreamStatus(string status, bool isTerminal)
    {
        var normalized = status.Trim().ToUpperInvariant();
        var terminalStatus = normalized is "FILLED" or "CANCELED" or "CANCELLED" or "REJECTED" or "EXPIRED" or "EXPIRED_IN_MATCH";
        if (normalized is not ("NEW" or "PARTIALLY_FILLED" or "PENDING_CANCEL" or
            "FILLED" or "CANCELED" or "CANCELLED" or "REJECTED" or "EXPIRED" or "EXPIRED_IN_MATCH") || terminalStatus != isTerminal)
        {
            throw new ArgumentException("Account-stream terminal classification does not match the exchange order status.", nameof(status));
        }
    }

    private static void ValidateReconciliationEvent(ReconciliationEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PayloadSha256);
        if (record.EventId == Guid.Empty || record.CorrelationId == Guid.Empty ||
            record.OccurredAtUtc.Offset != TimeSpan.Zero || record.SchemaVersion <= 0 ||
            record.PayloadSha256.Length != 64 ||
            record.PayloadSha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Reconciliation event identity, timestamp, version, or hash is invalid.", nameof(record));
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var payloadSchemaVersion) ||
            !payloadSchemaVersion.TryGetInt16(out var schemaVersion) || schemaVersion != record.SchemaVersion ||
            !root.TryGetProperty("eventType", out var payloadEventType) ||
            payloadEventType.ValueKind != JsonValueKind.String ||
            !string.Equals(payloadEventType.GetString(), record.EventType.Trim(), StringComparison.Ordinal) ||
            !root.TryGetProperty("eventId", out var payloadEventId) ||
            !payloadEventId.TryGetGuid(out var eventId) || eventId != record.EventId ||
            !root.TryGetProperty("correlationId", out var payloadCorrelationId) ||
            !payloadCorrelationId.TryGetGuid(out var correlationId) || correlationId != record.CorrelationId ||
            !root.TryGetProperty("occurredAtUtc", out var payloadOccurredAt) ||
            !payloadOccurredAt.TryGetDateTimeOffset(out var occurredAtUtc) || occurredAtUtc != record.OccurredAtUtc ||
            !root.TryGetProperty("data", out var eventData) || eventData.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Reconciliation event payload envelope does not match its indexed metadata.", nameof(record));
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.PayloadJson)));
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualHash), Convert.FromHexString(record.PayloadSha256)))
        {
            throw new ArgumentException("Reconciliation event payload hash does not match its content.", nameof(record));
        }
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
            "INSERT INTO trading.reconciliation_known_trades (trade_id, symbol) " +
            "VALUES (@trade_id, @symbol) ON CONFLICT (symbol, trade_id) DO NOTHING;",
            [
                Parameter("trade_id", NpgsqlDbType.Bigint, tradeId),
                Parameter("symbol", NpgsqlDbType.Varchar, normalizedSymbol),
            ], cancellationToken).ConfigureAwait(false);

    }

    public async Task<bool> AppendOrderLifecycleEventAsync(
        OrderLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.ClientOrderId);
        var transition = lifecycleEvent.Transition;
        if (transition.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lifecycle timestamps must be UTC.", nameof(lifecycleEvent));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.order_lifecycle_events
                (event_id, client_order_id, from_status, to_status, idempotency_key, occurred_at, exchange_order_id)
            VALUES (@event_id, @client_order_id, @from_status, @to_status, @idempotency_key, @occurred_at, @exchange_order_id)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING event_id;
            """, connection);
        command.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, lifecycleEvent.EventId));
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, lifecycleEvent.ClientOrderId.Trim()));
        command.Parameters.Add(Parameter("from_status", NpgsqlDbType.Varchar, transition.From.ToString()));
        command.Parameters.Add(Parameter("to_status", NpgsqlDbType.Varchar, transition.To.ToString()));
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, transition.IdempotencyKey.Value));
        command.Parameters.Add(Parameter("occurred_at", NpgsqlDbType.TimestampTz, transition.OccurredAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("exchange_order_id", NpgsqlDbType.Varchar, transition.ExchangeOrderId));
        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (inserted is not null and not DBNull)
        {
            return true;
        }

        await using var existingCommand = new NpgsqlCommand(
            """
            SELECT event_id, client_order_id, from_status, to_status, occurred_at, exchange_order_id
            FROM trading.order_lifecycle_events
            WHERE idempotency_key = @idempotency_key;
            """, connection);
        existingCommand.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, transition.IdempotencyKey.Value));
        await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The lifecycle journal uniqueness constraint reported a conflict, but no event was found.");
        }

        var equivalent = reader.GetGuid(0) == lifecycleEvent.EventId
            && string.Equals(reader.GetString(1), lifecycleEvent.ClientOrderId.Trim(), StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), transition.From.ToString(), StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), transition.To.ToString(), StringComparison.Ordinal)
            && MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(4), transition.OccurredAtUtc)
            && (reader.IsDBNull(5)
                ? string.IsNullOrWhiteSpace(transition.ExchangeOrderId)
                : string.Equals(reader.GetString(5), transition.ExchangeOrderId, StringComparison.Ordinal));
        if (!equivalent)
        {
            throw new InvalidOperationException("The lifecycle idempotency key is already associated with different event data.");
        }

        return false;
    }

    public async Task<IReadOnlyList<OrderLifecycleEvent>> ReadOrderLifecycleEventsAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, client_order_id, from_status, to_status, idempotency_key, occurred_at, exchange_order_id
            FROM trading.order_lifecycle_events
            WHERE client_order_id = @client_order_id
            ORDER BY occurred_at ASC;
            """, connection);
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, clientOrderId.Trim()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<OrderLifecycleEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var eventId = reader.GetGuid(0);
            var id = reader.GetString(1);
            var from = Enum.Parse<OrderStatus>(reader.GetString(2), ignoreCase: true);
            var to = Enum.Parse<OrderStatus>(reader.GetString(3), ignoreCase: true);
            var idempotencyKey = new IdempotencyKey(reader.GetString(4));
            var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(5), DateTimeKind.Utc));
            var exchangeOrderId = reader.IsDBNull(6) ? null : reader.GetString(6);
            events.Add(new OrderLifecycleEvent(eventId, id, new OrderTransition(from, to, idempotencyKey, occurredAt, exchangeOrderId)));
        }

        return events;
    }

    /// <summary>Enqueues a command durably; duplicate identity is accepted only for identical payload.</summary>
    public async Task<OutboxRecord> EnqueueAsync(
        OutboxRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await PersistOutboxAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>Writes one submit outbox record within an already-owned serializable transaction.</summary>
    private static async Task PersistOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(record.Command, JsonOptions);
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
            var existing = await ReadExistingOutboxIdentityAsync(connection, transaction, record.MessageId, record.IdempotencyKey.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The outbox uniqueness constraint reported a conflict, but no message was found.");
            if (!string.Equals(existing.EventType, record.MessageType, StringComparison.Ordinal)
                || !EquivalentJson(existing.PayloadJson, payload)
                || existing.CorrelationId != record.CorrelationId
                || !string.Equals(existing.IdempotencyKey, record.IdempotencyKey.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The outbox identity is already associated with different data.");
            }
        }

        await PersistExpectedExecutionFeeAsync(connection, transaction, record.Command, cancellationToken).ConfigureAwait(false);
        await PersistExecutionSubmissionContextAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionSubmissionContext?> ReadExecutionSubmissionContextAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT application_order_id, correlation_id, outbox_message_id, symbol, side, order_type,
                   authorized_quantity, reference_price, authorized_notional, execution_attempt, planned_at, expires_at
            FROM trading.execution_submission_context
            WHERE client_order_id = @client_order_id;
            """, connection);
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, clientOrderId.Trim()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            clientOrderId.Trim(), new OrderId(reader.GetGuid(0)), new CorrelationId(reader.GetGuid(1)), reader.GetGuid(2),
            new Symbol(reader.GetString(3)), reader.GetString(4) == "BUY" ? OrderSide.Buy : OrderSide.Sell,
            reader.GetString(5) == "LIMIT" ? OrderType.Limit : OrderType.Market,
            new Quantity(reader.GetDecimal(6)), new Price(reader.GetDecimal(7)), reader.GetDecimal(8), reader.GetInt32(9),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(10).ToUniversalTime()),
            reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetFieldValue<DateTime>(11).ToUniversalTime()));
    }

    public async Task<IReadOnlyList<CancelableExecutionSnapshot>> ReadExpiredCancellableExecutionsAsync(
        DateTimeOffset observedAtUtc,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Cancellation scan time must be UTC.", nameof(observedAtUtc));
        }

        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT context.application_order_id, context.client_order_id, context.symbol, context.side, context.order_type,
                   COALESCE(open_order.original_quantity, context.authorized_quantity),
                   COALESCE(open_order.cumulative_executed_quantity, 0), context.reference_price,
                   context.correlation_id, context.execution_attempt, context.expires_at, open_order.order_id, lifecycle.to_status,
                   submission.idempotency_key
            FROM trading.execution_submission_context AS context
            JOIN trading.outbox_messages AS submission ON submission.message_id = context.outbox_message_id
            JOIN trading.reconciliation_open_orders AS open_order ON open_order.client_order_id = context.client_order_id AND open_order.active = TRUE
            JOIN LATERAL (
                SELECT to_status FROM trading.order_lifecycle_events
                WHERE client_order_id = context.client_order_id
                ORDER BY occurred_at DESC, event_id DESC LIMIT 1
            ) AS lifecycle ON TRUE
            WHERE context.expires_at IS NOT NULL AND context.expires_at <= @observed_at
              AND lifecycle.to_status IN ('Acknowledged', 'PartiallyFilled')
              AND NOT EXISTS (
                  SELECT 1 FROM trading.outbox_messages AS cancellation
                  WHERE cancellation.aggregate_id = replace(context.application_order_id::text, '-', '')
                    AND cancellation.event_type = 'orders.cancel.v1')
            ORDER BY context.expires_at, context.client_order_id
            LIMIT @limit;
            """, connection);
        command.Parameters.Add(Parameter("observed_at", NpgsqlDbType.TimestampTz, observedAtUtc.UtcDateTime));
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CancelableExecutionSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = reader.GetString(12) switch
            {
                "Acknowledged" => OrderStatus.Acknowledged,
                "PartiallyFilled" => OrderStatus.PartiallyFilled,
                _ => throw new InvalidOperationException("Cancellation scan received a non-cancellable lifecycle status.")
            };
            result.Add(new CancelableExecutionSnapshot(
                new OrderId(reader.GetGuid(0)), new ClientOrderId(reader.GetString(1)), new Symbol(reader.GetString(2)),
                reader.GetString(3) == "BUY" ? OrderSide.Buy : OrderSide.Sell,
                reader.GetString(4) == "LIMIT" ? OrderType.Limit : OrderType.Market,
                new Quantity(reader.GetDecimal(5)), new Quantity(reader.GetDecimal(6)),
                reader.GetString(4) == "LIMIT" ? new Price(reader.GetDecimal(7)) : null,
                new CorrelationId(reader.GetGuid(8)), new IdempotencyKey(reader.GetString(13)), status,
                reader.GetInt32(9), new DateTimeOffset(reader.GetFieldValue<DateTime>(10).ToUniversalTime()), observedAtUtc,
                reader.IsDBNull(11) ? null : reader.GetInt64(11).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public async Task<CancelOutboxRecord> EnqueueCancellationAsync(
        CancelOutboxRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payload = JsonSerializer.Serialize(record.Command, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.outbox_messages
                (message_id, aggregate_type, aggregate_id, event_type, payload, correlation_id, idempotency_key, occurred_at, available_at)
            VALUES (@message_id, 'order', @aggregate_id, @event_type, @payload, @correlation_id, @idempotency_key, @occurred_at, @occurred_at)
            ON CONFLICT DO NOTHING RETURNING message_id;
            """,
            [Parameter("message_id", NpgsqlDbType.Uuid, record.MessageId), Parameter("aggregate_id", NpgsqlDbType.Varchar, record.Command.OrderId.ToString()),
             Parameter("event_type", NpgsqlDbType.Varchar, record.MessageType), Parameter("payload", NpgsqlDbType.Jsonb, payload),
             Parameter("correlation_id", NpgsqlDbType.Uuid, record.CorrelationId.Value), Parameter("idempotency_key", NpgsqlDbType.Varchar, record.IdempotencyKey.Value),
             Parameter("occurred_at", NpgsqlDbType.TimestampTz, record.CreatedAtUtc.UtcDateTime)], cancellationToken).ConfigureAwait(false);
        if (inserted is null or DBNull)
        {
            var existing = await ReadExistingOutboxIdentityAsync(connection, transaction, record.MessageId, record.IdempotencyKey.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The cancellation outbox uniqueness constraint reported a conflict, but no message was found.");
            if (!string.Equals(existing.EventType, record.MessageType, StringComparison.Ordinal)
                || !EquivalentJson(existing.PayloadJson, payload)
                || existing.CorrelationId != record.CorrelationId
                || !string.Equals(existing.IdempotencyKey, record.IdempotencyKey.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The cancellation outbox identity is already associated with different data.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<ExecutionReplanCandidate?> ResolveTerminalCancellationAsync(
        TerminalCancellationResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var command = resolution.Command;
        var outcome = resolution.Resolution;
        if (outcome.ProjectedStatus is not (OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired)
            || resolution.LifecycleEvent.Transition.To != outcome.ProjectedStatus
            || outcome.ExecutedQuantity.Value < 0m || outcome.ExecutedQuantity.Value > command.OriginalQuantity.Value
            || outcome.ResidualQuantity.Value != command.OriginalQuantity.Value - outcome.ExecutedQuantity.Value)
        {
            throw new ArgumentException("Terminal cancellation resolution is inconsistent.", nameof(resolution));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await InsertLifecycleEventAsync(connection, transaction, resolution.LifecycleEvent, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE trading.reconciliation_open_orders SET active = FALSE, updated_at = transaction_timestamp() WHERE client_order_id = @client_order_id;",
            [Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value)], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            """
            UPDATE trading.capital_reservations
            SET status = 'Released',
                reason = 'TERMINAL_ORDER_RELEASE',
                updated_at = transaction_timestamp()
            WHERE client_order_id = @client_order_id
              AND status IN ('Reserved', 'PartiallyConsumed', 'LockedUnknown');
            """,
            [Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value)], cancellationToken).ConfigureAwait(false);

        ExecutionReplanCandidate? candidate = null;
        if (outcome.ResidualQuantity.Value > 0m && outcome.Action == CancelResolutionAction.ReplanResidual)
        {
            candidate = CreateReplanCandidate(resolution);
            var inserted = await ExecuteScalarAsync(connection, transaction,
                """
                INSERT INTO trading.execution_replan_candidates
                    (candidate_id, origin_order_id, origin_client_order_id, cancel_message_id, terminal_lifecycle_event_id,
                     symbol, side, order_type, residual_quantity, next_attempt, correlation_id, created_at)
                VALUES (@candidate_id, @origin_order_id, @origin_client_order_id, @cancel_message_id, @terminal_lifecycle_event_id,
                        @symbol, @side, @order_type, @residual_quantity, @next_attempt, @correlation_id, @created_at)
                ON CONFLICT DO NOTHING RETURNING candidate_id;
                """,
                [Parameter("candidate_id", NpgsqlDbType.Uuid, candidate.CandidateId), Parameter("origin_order_id", NpgsqlDbType.Uuid, command.OrderId.Value),
                 Parameter("origin_client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value), Parameter("cancel_message_id", NpgsqlDbType.Uuid, resolution.CancelMessageId),
                 Parameter("terminal_lifecycle_event_id", NpgsqlDbType.Uuid, resolution.LifecycleEvent.EventId), Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value),
                 Parameter("side", NpgsqlDbType.Varchar, command.Side.ToString().ToUpperInvariant()), Parameter("order_type", NpgsqlDbType.Varchar, command.Type.ToString().ToUpperInvariant()),
                 Parameter("residual_quantity", NpgsqlDbType.Numeric, outcome.ResidualQuantity.Value), Parameter("next_attempt", NpgsqlDbType.Integer, command.Attempt + 1),
                 Parameter("correlation_id", NpgsqlDbType.Uuid, command.CorrelationId.Value), Parameter("created_at", NpgsqlDbType.TimestampTz, resolution.LifecycleEvent.Transition.OccurredAtUtc.UtcDateTime)], cancellationToken).ConfigureAwait(false);
            if (inserted is null or DBNull)
            {
                var existing = await ReadReplanCandidateAsync(connection, transaction, resolution.CancelMessageId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Replan candidate uniqueness conflict has no durable record.");
                if (existing.CandidateId != candidate.CandidateId || existing.OriginOrderId != candidate.OriginOrderId
                    || existing.OriginClientOrderId != candidate.OriginClientOrderId || existing.CancelMessageId != candidate.CancelMessageId
                    || existing.TerminalLifecycleEventId != candidate.TerminalLifecycleEventId || existing.Symbol != candidate.Symbol
                    || existing.Side != candidate.Side || existing.Type != candidate.Type || existing.ResidualQuantity != candidate.ResidualQuantity
                    || existing.NextAttempt != candidate.NextAttempt || existing.CorrelationId != candidate.CorrelationId
                    || existing.CreatedAtUtc.UtcTicks / 10 != candidate.CreatedAtUtc.UtcTicks / 10)
                {
                    throw new InvalidOperationException("Replan candidate identity is already associated with different data.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    public async Task<IReadOnlyList<ExecutionReplanCandidate>> ReadReplanCandidatesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT candidate_id, origin_order_id, origin_client_order_id, cancel_message_id, terminal_lifecycle_event_id, symbol, side, order_type, residual_quantity, next_attempt, correlation_id, created_at FROM trading.execution_replan_candidates ORDER BY created_at, candidate_id LIMIT @limit;", connection);
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<ExecutionReplanCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new(reader.GetGuid(0), new OrderId(reader.GetGuid(1)), new ClientOrderId(reader.GetString(2)), reader.GetGuid(3), reader.GetGuid(4), new Symbol(reader.GetString(5)),
                reader.GetString(6) == "BUY" ? OrderSide.Buy : OrderSide.Sell, reader.GetString(7) == "LIMIT" ? OrderType.Limit : OrderType.Market,
                new Quantity(reader.GetDecimal(8)), reader.GetInt32(9), new CorrelationId(reader.GetGuid(10)), new DateTimeOffset(reader.GetFieldValue<DateTime>(11).ToUniversalTime())));
        }
        return candidates;
    }

    public async Task<RuntimeStateCheckpoint> ReadRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT state, revision FROM trading.runtime_state_checkpoints WHERE scope = 'worker';", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new(RuntimeState.Stopped, 0);
        return new(ParseRuntimeState(reader.GetString(0)), reader.GetInt64(1));
    }

    public async Task AppendRuntimeTransitionAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Revision < 1 || transition.OccurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Runtime transition revision and timestamp are invalid.", nameof(transition));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var prior = await ReadRuntimeStateForUpdateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (prior.Revision != transition.Revision - 1 || prior.State != transition.From)
        {
            var replay = await ReadRuntimeTransitionAsync(connection, transaction, transition.Revision, cancellationToken).ConfigureAwait(false);
            if (RuntimeTransitionsEquivalent(replay, transition)) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return; }
            throw new InvalidOperationException("Runtime transition revision or predecessor state conflicts with durable checkpoint.");
        }
        await ExecuteAsync(connection, transaction,
            "INSERT INTO trading.runtime_state_transitions (revision, from_state, to_state, reason, occurred_at, correlation_id) VALUES (@revision, @from_state, @to_state, @reason, @occurred_at, @correlation_id);",
            [Parameter("revision", NpgsqlDbType.Bigint, transition.Revision), Parameter("from_state", NpgsqlDbType.Varchar, transition.From.ToString()), Parameter("to_state", NpgsqlDbType.Varchar, transition.To.ToString()), Parameter("reason", NpgsqlDbType.Varchar, transition.Reason), Parameter("occurred_at", NpgsqlDbType.TimestampTz, transition.OccurredAtUtc.UtcDateTime), Parameter("correlation_id", NpgsqlDbType.Varchar, transition.CorrelationId)], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO trading.runtime_state_checkpoints (scope, state, revision) VALUES ('worker', @state, @revision) ON CONFLICT (scope) DO UPDATE SET state = EXCLUDED.state, revision = EXCLUDED.revision, updated_at = transaction_timestamp();",
            [Parameter("state", NpgsqlDbType.Varchar, transition.To.ToString()), Parameter("revision", NpgsqlDbType.Bigint, transition.Revision)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionAttributionReport?> ReadExecutionAttributionReportAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        var context = await ReadExecutionSubmissionContextAsync(clientOrderId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var outboxCommand = new NpgsqlCommand(
            "SELECT attempts, processed_at, last_error FROM trading.outbox_messages WHERE message_id = @message_id;", connection);
        outboxCommand.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, context.OutboxMessageId));
        await using var outboxReader = await outboxCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await outboxReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Execution submission context references a missing outbox message.");
        }

        var attempts = outboxReader.GetInt32(0);
        DateTimeOffset? processedAt = outboxReader.IsDBNull(1) ? null : new DateTimeOffset(outboxReader.GetFieldValue<DateTime>(1).ToUniversalTime());
        var lastError = outboxReader.IsDBNull(2) ? null : outboxReader.GetString(2);
        await outboxReader.CloseAsync().ConfigureAwait(false);

        await using var fillsCommand = new NpgsqlCommand(
            """
            SELECT execution_id, occurred_at, quantity, price, cumulative_fill_ratio, implementation_shortfall_quote,
                   decision_to_fill_latency_ms, execution_quality_status, execution_quality_reason,
                   fee_comparison_status, fee_comparison_reason
            FROM trading.execution_fills
            WHERE client_order_id = @client_order_id
            ORDER BY occurred_at, execution_id;
            """, connection);
        fillsCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, context.ClientOrderId));
        await using var fillsReader = await fillsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var fills = new List<ExecutionFillAttributionReport>();
        while (await fillsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fills.Add(new(
                fillsReader.GetString(0), new DateTimeOffset(fillsReader.GetFieldValue<DateTime>(1).ToUniversalTime()),
                fillsReader.GetDecimal(2), fillsReader.GetDecimal(3),
                fillsReader.IsDBNull(4) ? null : fillsReader.GetDecimal(4),
                fillsReader.IsDBNull(5) ? null : fillsReader.GetDecimal(5),
                fillsReader.IsDBNull(6) ? null : fillsReader.GetInt64(6),
                fillsReader.GetString(7), fillsReader.GetString(8), fillsReader.GetString(9), fillsReader.GetString(10)));
        }
        await fillsReader.CloseAsync().ConfigureAwait(false);

        await using var lifecycleCommand = new NpgsqlCommand(
            """
            SELECT event_id, from_status, to_status, idempotency_key, occurred_at, exchange_order_id
            FROM trading.order_lifecycle_events
            WHERE client_order_id = @client_order_id
            ORDER BY occurred_at, event_id;
            """, connection);
        lifecycleCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, context.ClientOrderId));
        await using var lifecycleReader = await lifecycleCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = new List<OrderLifecycleAttributionReport>();
        while (await lifecycleReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lifecycle.Add(new(
                lifecycleReader.GetGuid(0),
                ParseOrderStatus(lifecycleReader.GetString(1)),
                ParseOrderStatus(lifecycleReader.GetString(2)),
                lifecycleReader.GetString(3),
                new DateTimeOffset(lifecycleReader.GetFieldValue<DateTime>(4).ToUniversalTime()),
                lifecycleReader.IsDBNull(5) ? null : lifecycleReader.GetString(5)));
        }

        return new(context, attempts, processedAt, lastError, lifecycle, fills);
    }

    public async Task RecordExpectedExecutionFeeAsync(
        SubmitOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await PersistExpectedExecutionFeeAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionFeeEstimate?> ReadExpectedExecutionFeeAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT estimated_rate, snapshot_hash, source, observed_at, valid_until, reference_price, authorized_quantity, decision_at FROM trading.execution_fee_expectations WHERE client_order_id = @client_order_id;",
            connection);
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, clientOrderId.Trim()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExecutionFeeEstimate(
            reader.GetDecimal(0),
            reader.GetString(1),
            reader.GetString(2),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(3).ToUniversalTime()),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(4).ToUniversalTime()),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(7).ToUniversalTime()));
    }

    private static async Task PersistExpectedExecutionFeeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SubmitOrderCommand command,
        CancellationToken cancellationToken)
    {
        if (command.EstimatedCommissionRate is not { } rate)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(command.CommissionSnapshotHash) ||
            string.IsNullOrWhiteSpace(command.CommissionSource) ||
            command.CommissionObservedAtUtc is not { } observedAtUtc ||
            command.CommissionValidUntilUtc is not { } validUntilUtc)
        {
            throw new InvalidOperationException("An estimated commission requires complete snapshot attribution.");
        }

        _ = new ExecutionFeeEstimate(rate, command.CommissionSnapshotHash, command.CommissionSource, observedAtUtc, validUntilUtc);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.execution_fee_expectations
                (client_order_id, order_id, symbol, side, estimated_rate, snapshot_hash, source, observed_at, valid_until,
                 reference_price, authorized_quantity, decision_at)
            VALUES
                (@client_order_id, @order_id, @symbol, @side, @estimated_rate, @snapshot_hash, @source, @observed_at, @valid_until,
                 @reference_price, @authorized_quantity, @decision_at)
            ON CONFLICT (client_order_id) DO NOTHING;
            """,
            [
                Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value),
                Parameter("order_id", NpgsqlDbType.Uuid, command.OrderId.Value),
                Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value),
                Parameter("side", NpgsqlDbType.Varchar, command.Side == OrderSide.Buy ? "BUY" : "SELL"),
                Parameter("estimated_rate", NpgsqlDbType.Numeric, rate),
                Parameter("snapshot_hash", NpgsqlDbType.Varchar, command.CommissionSnapshotHash),
                Parameter("source", NpgsqlDbType.Varchar, command.CommissionSource),
                Parameter("observed_at", NpgsqlDbType.TimestampTz, observedAtUtc.UtcDateTime),
                Parameter("valid_until", NpgsqlDbType.TimestampTz, validUntilUtc.UtcDateTime),
                Parameter("reference_price", NpgsqlDbType.Numeric, command.ReferencePrice.Value),
                Parameter("authorized_quantity", NpgsqlDbType.Numeric, command.Quantity.Value),
                Parameter("decision_at", NpgsqlDbType.TimestampTz, command.CreatedAtUtc.UtcDateTime),
            ], cancellationToken).ConfigureAwait(false);

        await using var identityCommand = new NpgsqlCommand(
            """
            SELECT order_id, symbol, side, estimated_rate, snapshot_hash, source, observed_at, valid_until
            FROM trading.execution_fee_expectations
            WHERE client_order_id = @client_order_id;
            """,
            connection,
            transaction);
        identityCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value));
        await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The execution-fee expectation conflict was reported without a durable record.");
        }

        var matches = reader.GetGuid(0) == command.OrderId.Value
            && string.Equals(reader.GetString(1), command.Symbol.Value, StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), command.Side == OrderSide.Buy ? "BUY" : "SELL", StringComparison.Ordinal)
            && reader.GetDecimal(3) == rate
            && string.Equals(reader.GetString(4), command.CommissionSnapshotHash, StringComparison.Ordinal)
            && string.Equals(reader.GetString(5), command.CommissionSource, StringComparison.Ordinal)
            && MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(6), observedAtUtc)
            && MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(7), validUntilUtc);
        if (!matches)
        {
            throw new InvalidOperationException("The client order identity is already associated with a different execution-fee expectation.");
        }
    }

    private static async Task PersistExecutionSubmissionContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var command = record.Command;
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.execution_submission_context
                (client_order_id, application_order_id, correlation_id, outbox_message_id, symbol, side, order_type,
                 authorized_quantity, reference_price, authorized_notional, execution_attempt, planned_at, expires_at)
            VALUES
                (@client_order_id, @application_order_id, @correlation_id, @outbox_message_id, @symbol, @side, @order_type,
                 @authorized_quantity, @reference_price, @authorized_notional, @execution_attempt, @planned_at, @expires_at)
            ON CONFLICT (client_order_id) DO NOTHING;
            """,
            [
                Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value),
                Parameter("application_order_id", NpgsqlDbType.Uuid, command.OrderId.Value),
                Parameter("correlation_id", NpgsqlDbType.Uuid, command.CorrelationId.Value),
                Parameter("outbox_message_id", NpgsqlDbType.Uuid, record.MessageId),
                Parameter("symbol", NpgsqlDbType.Varchar, command.Symbol.Value),
                Parameter("side", NpgsqlDbType.Varchar, command.Side == OrderSide.Buy ? "BUY" : "SELL"),
                Parameter("order_type", NpgsqlDbType.Varchar, command.Type == OrderType.Limit ? "LIMIT" : "MARKET"),
                Parameter("authorized_quantity", NpgsqlDbType.Numeric, command.Quantity.Value),
                Parameter("reference_price", NpgsqlDbType.Numeric, command.ReferencePrice.Value),
                Parameter("authorized_notional", NpgsqlDbType.Numeric, command.AuthorizedNotional),
                Parameter("execution_attempt", NpgsqlDbType.Integer, command.ExecutionAttempt),
                Parameter("planned_at", NpgsqlDbType.TimestampTz, command.CreatedAtUtc.UtcDateTime),
                Parameter("expires_at", NpgsqlDbType.TimestampTz, command.ExecutionExpiresAtUtc?.UtcDateTime),
            ], cancellationToken).ConfigureAwait(false);

        await using var identityCommand = new NpgsqlCommand(
            """
            SELECT application_order_id, correlation_id, outbox_message_id, symbol, side, order_type,
                   authorized_quantity, reference_price, authorized_notional, execution_attempt, planned_at, expires_at
            FROM trading.execution_submission_context
            WHERE client_order_id = @client_order_id;
            """, connection, transaction);
        identityCommand.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Varchar, command.ClientOrderId.Value));
        await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The execution submission context conflict was reported without a durable record.");
        }

        var matches = reader.GetGuid(0) == command.OrderId.Value
            && reader.GetGuid(1) == command.CorrelationId.Value
            && reader.GetGuid(2) == record.MessageId
            && string.Equals(reader.GetString(3), command.Symbol.Value, StringComparison.Ordinal)
            && string.Equals(reader.GetString(4), command.Side == OrderSide.Buy ? "BUY" : "SELL", StringComparison.Ordinal)
            && string.Equals(reader.GetString(5), command.Type == OrderType.Limit ? "LIMIT" : "MARKET", StringComparison.Ordinal)
            && reader.GetDecimal(6) == command.Quantity.Value
            && reader.GetDecimal(7) == command.ReferencePrice.Value
            && reader.GetDecimal(8) == command.AuthorizedNotional
            && reader.GetInt32(9) == command.ExecutionAttempt
            && MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(10), command.CreatedAtUtc)
            && (reader.IsDBNull(11)
                ? command.ExecutionExpiresAtUtc is null
                : command.ExecutionExpiresAtUtc is { } expiresAtUtc && MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(11), expiresAtUtc));
        if (!matches)
        {
            throw new InvalidOperationException("The client order identity is already associated with a different execution submission context.");
        }
    }

    private static bool MatchesPostgresTimestamp(DateTime storedUtc, DateTimeOffset expectedUtc) =>
        Math.Abs(storedUtc.ToUniversalTime().Ticks - expectedUtc.UtcDateTime.Ticks) < TimeSpan.TicksPerMicrosecond;

    private static OrderStatus ParseOrderStatus(string value) =>
        Enum.TryParse<OrderStatus>(value, ignoreCase: false, out var status) && Enum.IsDefined(status)
            ? status
            : throw new InvalidOperationException($"Persisted lifecycle contains an unsupported order status '{value}'.");

    /// <summary>
    /// Atomically records immutable decision evidence, the optional submit
    /// outbox plus its fee/context projections, and the processed-bar
    /// checkpoint. Nothing is dispatchable until this transaction commits.
    /// </summary>
    public async Task CommitAsync(
        AutonomyDecisionCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();
        var payload = JsonSerializer.Serialize(commit, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.autonomy_decisions
                (decision_id, environment, account_scope, checkpoint_scope, symbol, strategy_id, strategy_version,
                 close_time, available_at, feature_schema_version, feature_configuration_fingerprint, feature_fingerprint,
                 signal, strategy_reason, risk_status, risk_policy_version, risk_requested_notional, risk_authorized_notional,
                 risk_reason_codes, outcome, no_trade_reason, trace, outbox_message_id, payload)
            VALUES
                (@decision_id, @environment, @account_scope, @checkpoint_scope, @symbol, @strategy_id, @strategy_version,
                 @close_time, @available_at, @feature_schema_version, @feature_configuration_fingerprint, @feature_fingerprint,
                 @signal, @strategy_reason, @risk_status, @risk_policy_version, @risk_requested_notional, @risk_authorized_notional,
                 @risk_reason_codes, @outcome, @no_trade_reason, @trace, @outbox_message_id, @payload)
            ON CONFLICT DO NOTHING
            RETURNING decision_id;
            """,
            [
                Parameter("decision_id", NpgsqlDbType.Varchar, commit.DecisionId.Trim()),
                Parameter("environment", NpgsqlDbType.Varchar, commit.Environment.Trim()),
                Parameter("account_scope", NpgsqlDbType.Varchar, commit.AccountScope.Trim()),
                Parameter("checkpoint_scope", NpgsqlDbType.Varchar, commit.CheckpointScope.Trim()),
                Parameter("symbol", NpgsqlDbType.Varchar, commit.Symbol.Value),
                Parameter("strategy_id", NpgsqlDbType.Varchar, commit.StrategyId.Trim()),
                Parameter("strategy_version", NpgsqlDbType.Varchar, commit.StrategyVersion.Trim()),
                Parameter("close_time", NpgsqlDbType.TimestampTz, commit.CloseTimeUtc.UtcDateTime),
                Parameter("available_at", NpgsqlDbType.TimestampTz, commit.AvailableAtUtc.UtcDateTime),
                Parameter("feature_schema_version", NpgsqlDbType.Varchar, commit.FeatureSchemaVersion.Trim()),
                Parameter("feature_configuration_fingerprint", NpgsqlDbType.Varchar, commit.FeatureConfigurationFingerprint.Trim()),
                Parameter("feature_fingerprint", NpgsqlDbType.Varchar, commit.FeatureFingerprint.Trim()),
                Parameter("signal", NpgsqlDbType.Varchar, commit.Signal.Trim()),
                Parameter("strategy_reason", NpgsqlDbType.Varchar, commit.StrategyReason.Trim()),
                Parameter("risk_status", NpgsqlDbType.Varchar, commit.RiskDecision?.Status.ToString()),
                Parameter("risk_policy_version", NpgsqlDbType.Varchar, commit.RiskDecision?.PolicyVersion),
                Parameter("risk_requested_notional", NpgsqlDbType.Numeric, commit.RiskDecision?.RequestedTargetNotional),
                Parameter("risk_authorized_notional", NpgsqlDbType.Numeric, commit.RiskDecision?.AuthorizedTargetNotional),
                Parameter("risk_reason_codes", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(commit.RiskDecision?.ReasonCodes ?? [], JsonOptions)),
                Parameter("outcome", NpgsqlDbType.Varchar, commit.Outcome.Trim()),
                Parameter("no_trade_reason", NpgsqlDbType.Varchar, commit.NoTradeReason),
                Parameter("trace", NpgsqlDbType.Jsonb, commit.TraceJson),
                Parameter("outbox_message_id", NpgsqlDbType.Uuid, commit.OutboxRecord?.MessageId),
                Parameter("payload", NpgsqlDbType.Jsonb, payload),
            ], cancellationToken).ConfigureAwait(false);

        if (inserted is null or DBNull)
        {
            var existingPayload = await ExecuteScalarAsync(connection, transaction,
                "SELECT payload FROM trading.autonomy_decisions WHERE decision_id = @decision_id;",
                [Parameter("decision_id", NpgsqlDbType.Varchar, commit.DecisionId.Trim())], cancellationToken).ConfigureAwait(false);
            if (existingPayload is null or DBNull || !EquivalentJson((string)existingPayload, payload))
            {
                throw new InvalidOperationException("The autonomy decision identity is already associated with different evidence.");
            }
        }

        if (commit.OutboxRecord is not null)
        {
            await PersistOutboxAsync(connection, transaction, commit.OutboxRecord, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.autonomy_checkpoints (scope, last_processed_close)
            VALUES (@scope, @close_time)
            ON CONFLICT (scope) DO UPDATE
            SET last_processed_close = GREATEST(trading.autonomy_checkpoints.last_processed_close, EXCLUDED.last_processed_close),
                updated_at = transaction_timestamp();
            """,
            [
                Parameter("scope", NpgsqlDbType.Varchar, commit.CheckpointScope.Trim()),
                Parameter("close_time", NpgsqlDbType.TimestampTz, commit.CloseTimeUtc.UtcDateTime),
            ], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLastProcessedCloseAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT last_processed_close FROM trading.autonomy_checkpoints WHERE scope = @scope;",
            connection);
        command.Parameters.Add(Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime timestamp
            ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : null;
    }

    public async Task<AutonomyDecisionCommit?> ReadDecisionCommitAsync(
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT payload, symbol FROM trading.autonomy_decisions WHERE decision_id = @decision_id;",
            connection);
        command.Parameters.Add(Parameter("decision_id", NpgsqlDbType.Varchar, decisionId.Trim()));
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payload = reader.GetString(0);
        var symbolColumn = reader.GetString(1);
        var commit = JsonSerializer.Deserialize<AutonomyDecisionCommit>(payload, JsonOptions);
        if (commit is not null && string.IsNullOrWhiteSpace(commit.Symbol.Value) && !string.IsNullOrWhiteSpace(symbolColumn))
        {
            commit = commit with { Symbol = new Symbol(symbolColumn) };
        }

        return commit;
    }

    public async Task SetLastProcessedCloseAsync(
        string scope,
        DateTimeOffset closeTimeUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (closeTimeUtc.Offset != TimeSpan.Zero)
        {
            closeTimeUtc = closeTimeUtc.ToUniversalTime();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO trading.autonomy_checkpoints (scope, last_processed_close)
            VALUES (@scope, @close_time)
            ON CONFLICT (scope) DO UPDATE
            SET last_processed_close = GREATEST(trading.autonomy_checkpoints.last_processed_close, EXCLUDED.last_processed_close),
                updated_at = transaction_timestamp();
            """,
            connection);
        command.Parameters.Add(Parameter("scope", NpgsqlDbType.Varchar, scope.Trim()));
        command.Parameters.Add(Parameter("close_time", NpgsqlDbType.TimestampTz, closeTimeUtc.UtcDateTime));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task InsertLedgerPostingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LedgerEntry entry,
        CancellationToken cancellationToken)
    {
        foreach (var (posting, index) in entry.Postings.Select((posting, index) => (posting, index)))
        {
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
        Guid messageId,
        string idempotencyKey,
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
        command.Parameters.Add(Parameter("message_id", NpgsqlDbType.Uuid, messageId));
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), new CorrelationId(reader.GetGuid(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static ExecutionReplanCandidate CreateReplanCandidate(TerminalCancellationResolution resolution)
    {
        var command = resolution.Command;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"replan|{resolution.CancelMessageId:N}|{command.OrderId}|{command.Attempt + 1}"));
        return new(new Guid(hash[..16]), command.OrderId, command.ClientOrderId, resolution.CancelMessageId,
            resolution.LifecycleEvent.EventId, command.Symbol, command.Side, command.Type,
            resolution.Resolution.ResidualQuantity, command.Attempt + 1, command.CorrelationId,
            resolution.LifecycleEvent.Transition.OccurredAtUtc);
    }

    private static RuntimeState ParseRuntimeState(string value) => Enum.TryParse<RuntimeState>(value, out var state) && Enum.IsDefined(state)
        ? state : throw new InvalidOperationException("Persisted runtime state is unsupported.");

    private static bool RuntimeTransitionsEquivalent(RuntimeStateTransition? existing, RuntimeStateTransition candidate) =>
        existing is not null && existing.From == candidate.From && existing.To == candidate.To && existing.Revision == candidate.Revision
        && string.Equals(existing.Reason, candidate.Reason, StringComparison.Ordinal)
        && existing.OccurredAtUtc.UtcTicks / 10 == candidate.OccurredAtUtc.UtcTicks / 10
        && string.Equals(existing.CorrelationId, candidate.CorrelationId, StringComparison.Ordinal);

    private static async Task<RuntimeStateCheckpoint> ReadRuntimeStateForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT state, revision FROM trading.runtime_state_checkpoints WHERE scope = 'worker' FOR UPDATE;", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(ParseRuntimeState(reader.GetString(0)), reader.GetInt64(1)) : new(RuntimeState.Stopped, 0);
    }

    private static async Task<RuntimeStateTransition?> ReadRuntimeTransitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long revision, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT from_state, to_state, reason, occurred_at, correlation_id FROM trading.runtime_state_transitions WHERE revision = @revision;", connection, transaction);
        command.Parameters.Add(Parameter("revision", NpgsqlDbType.Bigint, revision));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(ParseRuntimeState(reader.GetString(0)), ParseRuntimeState(reader.GetString(1)), revision, reader.GetString(2), new DateTimeOffset(reader.GetFieldValue<DateTime>(3).ToUniversalTime()), reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
    }

    private static async Task InsertLifecycleEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, OrderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        var transition = lifecycleEvent.Transition;
        var inserted = await ExecuteScalarAsync(connection, transaction,
            """
            INSERT INTO trading.order_lifecycle_events
                (event_id, client_order_id, from_status, to_status, idempotency_key, occurred_at, exchange_order_id)
            VALUES (@event_id, @client_order_id, @from_status, @to_status, @idempotency_key, @occurred_at, @exchange_order_id)
            ON CONFLICT (idempotency_key) DO NOTHING RETURNING event_id;
            """,
            [Parameter("event_id", NpgsqlDbType.Uuid, lifecycleEvent.EventId), Parameter("client_order_id", NpgsqlDbType.Varchar, lifecycleEvent.ClientOrderId),
             Parameter("from_status", NpgsqlDbType.Varchar, transition.From.ToString()), Parameter("to_status", NpgsqlDbType.Varchar, transition.To.ToString()),
             Parameter("idempotency_key", NpgsqlDbType.Varchar, transition.IdempotencyKey.Value), Parameter("occurred_at", NpgsqlDbType.TimestampTz, transition.OccurredAtUtc.UtcDateTime),
             Parameter("exchange_order_id", NpgsqlDbType.Varchar, transition.ExchangeOrderId)], cancellationToken).ConfigureAwait(false);
        if (inserted is not null and not DBNull) return;
        await using var command = new NpgsqlCommand("SELECT event_id, client_order_id, from_status, to_status, occurred_at, exchange_order_id FROM trading.order_lifecycle_events WHERE idempotency_key = @idempotency_key;", connection, transaction);
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, transition.IdempotencyKey.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetGuid(0) != lifecycleEvent.EventId
            || !string.Equals(reader.GetString(1), lifecycleEvent.ClientOrderId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), transition.From.ToString(), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), transition.To.ToString(), StringComparison.Ordinal)
            || !MatchesPostgresTimestamp(reader.GetFieldValue<DateTime>(4), transition.OccurredAtUtc)
            || (reader.IsDBNull(5) ? !string.IsNullOrWhiteSpace(transition.ExchangeOrderId) : !string.Equals(reader.GetString(5), transition.ExchangeOrderId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The lifecycle idempotency key is already associated with different event data.");
        }
    }

    private static async Task<ExecutionReplanCandidate?> ReadReplanCandidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid cancelMessageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT candidate_id, origin_order_id, origin_client_order_id, terminal_lifecycle_event_id, symbol, side, order_type, residual_quantity, next_attempt, correlation_id, created_at FROM trading.execution_replan_candidates WHERE cancel_message_id = @cancel_message_id;", connection, transaction);
        command.Parameters.Add(Parameter("cancel_message_id", NpgsqlDbType.Uuid, cancelMessageId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetGuid(0), new OrderId(reader.GetGuid(1)), new ClientOrderId(reader.GetString(2)), cancelMessageId, reader.GetGuid(3), new Symbol(reader.GetString(4)),
                reader.GetString(5) == "BUY" ? OrderSide.Buy : OrderSide.Sell, reader.GetString(6) == "LIMIT" ? OrderType.Limit : OrderType.Market,
                new Quantity(reader.GetDecimal(7)), reader.GetInt32(8), new CorrelationId(reader.GetGuid(9)), new DateTimeOffset(reader.GetFieldValue<DateTime>(10).ToUniversalTime()))
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
        string CreatedBy,
        string? BudgetPolicyJson = null);

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
        Parameter("budget_policy_json", NpgsqlDbType.Text,
            document.Policy.BudgetPolicy is null
                ? (object)DBNull.Value
                : System.Text.Json.JsonSerializer.Serialize(document.Policy.BudgetPolicy)),
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
                   effective_from, expires_at, document_hash, created_by, budget_policy_json
            FROM trading.risk_policies
            WHERE policy_version = @version;
            """, connection, transaction);
        command.Parameters.Add(Parameter("version", NpgsqlDbType.Varchar, version));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRiskPolicyRow(reader) : null;
    }

    private static async Task<AccountStreamEventRecord?> ReadAccountStreamEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string eventType,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, event_type, dedupe_key, event_time_ms, received_at, symbol, payload, payload_sha256,
                   session_id, session_generation, subscription_id
            FROM trading.account_stream_events
            WHERE event_id = @event_id OR (event_type = @event_type AND dedupe_key = @dedupe_key)
            ORDER BY event_id
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(Parameter("event_type", NpgsqlDbType.Varchar, eventType));
        command.Parameters.Add(Parameter("dedupe_key", NpgsqlDbType.Varchar, dedupeKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAccountStreamEvent(reader) : null;
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
                   p.effective_from, p.expires_at, p.document_hash, p.created_by, p.budget_policy_json
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
        BudgetPolicy? budgetPolicy = null;
        if (!string.IsNullOrWhiteSpace(row.BudgetPolicyJson))
        {
            try
            {
                budgetPolicy = System.Text.Json.JsonSerializer.Deserialize<BudgetPolicy>(row.BudgetPolicyJson);
            }
            catch
            {
                budgetPolicy = null;
            }
        }

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
            row.MinLiquidityNotional,
            budgetPolicy);

        if (!string.Equals(
                RiskPolicyDocument.ComputeHash(policy, row.EffectiveFromUtc, row.ExpiresAtUtc),
                row.DocumentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            var policyWithDefaultBudget = new RiskPolicy(
                row.PolicyVersion,
                row.MaxOrderNotional,
                row.MaxExposureNotional,
                row.MaxDailyLoss,
                row.MaxDrawdown,
                TimeSpan.FromTicks(row.MaxMarketDataAgeTicks),
                row.WithdrawalsEnabled,
                row.MaxSlippageBps,
                row.MaxVolatilityBps,
                row.MinLiquidityNotional,
                BudgetPolicy.ApprovedMicroLiveDefault);
            if (string.Equals(
                    RiskPolicyDocument.ComputeHash(policyWithDefaultBudget, row.EffectiveFromUtc, row.ExpiresAtUtc),
                    row.DocumentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                policy = policyWithDefaultBudget;
            }
            else
            {
                var policyWithMultiAssetBudget = new RiskPolicy(
                    row.PolicyVersion,
                    row.MaxOrderNotional,
                    row.MaxExposureNotional,
                    row.MaxDailyLoss,
                    row.MaxDrawdown,
                    TimeSpan.FromTicks(row.MaxMarketDataAgeTicks),
                    row.WithdrawalsEnabled,
                    row.MaxSlippageBps,
                    row.MaxVolatilityBps,
                    row.MinLiquidityNotional,
                    new BudgetPolicy(allowedSymbol: "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT"));
                if (string.Equals(
                        RiskPolicyDocument.ComputeHash(policyWithMultiAssetBudget, row.EffectiveFromUtc, row.ExpiresAtUtc),
                        row.DocumentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    policy = policyWithMultiAssetBudget;
                }
            }
        }

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
        reader.GetString(13),
        reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null);

    private static RiskPolicyEventRow ReadRiskPolicyEvent(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        Enum.Parse<RiskPolicyLifecycle>(reader.GetString(2), ignoreCase: false),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));

    private static AccountStreamEventRecord ReadAccountStreamEvent(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
        reader.GetString(6),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(8) ? null : reader.GetGuid(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10));

    private static OperationalControlAuditEvent ReadOperationalControlAuditEvent(NpgsqlDataReader reader)
    {
        var auditEvent = new OperationalControlAuditEvent(
            reader.GetGuid(0),
            Enum.Parse<OperationalMode>(reader.GetString(3), ignoreCase: false),
            Enum.Parse<OperationalMode>(reader.GetString(4), ignoreCase: false),
            reader.GetString(5),
            reader.GetString(6),
            new IdempotencyKey(reader.GetString(7)),
            new DateTimeOffset(new DateTime(reader.GetInt64(9), DateTimeKind.Utc)),
            reader.IsDBNull(10) ? null : reader.GetString(10));

        if (reader.GetInt32(1) != auditEvent.SchemaVersion
            || !string.Equals(reader.GetString(2), auditEvent.EventType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operational-control audit event {auditEvent.EventId} has an unsupported schema or event type.");
        }

        return auditEvent;
    }

    private static async Task<OperationalControlAuditEvent?> ReadOperationalControlAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, schema_version, event_type, from_mode, to_mode, actor, reason,
                   idempotency_key, occurred_at, occurred_at_ticks, approval_id, payload_sha256
            FROM trading.operational_control_audit_events
            WHERE event_id = @event_id OR idempotency_key = @idempotency_key
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.Add(Parameter("event_id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(Parameter("idempotency_key", NpgsqlDbType.Varchar, idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOperationalControlAuditEvent(reader)
            : null;
    }

    private static void ValidateOperationalControlAuditEvent(OperationalControlAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (auditEvent.SchemaVersion != OperationalControlAuditEvent.CurrentSchemaVersion
            || !string.Equals(auditEvent.EventType, "OPERATIONAL_CONTROL_TRANSITION", StringComparison.Ordinal)
            || auditEvent.OccurredAtUtc.Offset != TimeSpan.Zero
            || !string.Equals(auditEvent.PayloadSha256, ComputeOperationalControlAuditHash(auditEvent), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Operational-control audit event schema, timestamp, type, or payload hash is invalid.");
        }
    }

    private static string ComputeOperationalControlAuditHash(OperationalControlAuditEvent auditEvent) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('|',
                auditEvent.EventType,
                auditEvent.SchemaVersion,
                auditEvent.EventId.ToString("D"),
                auditEvent.From,
                auditEvent.To,
                auditEvent.Actor,
                auditEvent.Reason,
                auditEvent.IdempotencyKey.ToString(),
                auditEvent.OccurredAtUtc.ToUniversalTime().ToString("O"),
                auditEvent.ApprovalId ?? string.Empty))));

    private static void EnsureEquivalent(
        OperationalControlAuditEvent existing,
        OperationalControlAuditEvent candidate)
    {
        if (existing.EventId != candidate.EventId
            || existing.SchemaVersion != candidate.SchemaVersion
            || !string.Equals(existing.EventType, candidate.EventType, StringComparison.Ordinal)
            || existing.From != candidate.From
            || existing.To != candidate.To
            || !string.Equals(existing.Actor, candidate.Actor, StringComparison.Ordinal)
            || !string.Equals(existing.Reason, candidate.Reason, StringComparison.Ordinal)
            || existing.IdempotencyKey != candidate.IdempotencyKey
            || existing.OccurredAtUtc.UtcTicks != candidate.OccurredAtUtc.UtcTicks
            || !string.Equals(existing.ApprovalId, candidate.ApprovalId, StringComparison.Ordinal)
            || !string.Equals(existing.PayloadSha256, candidate.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The operational-control audit identity is already associated with different data.");
        }
    }

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

    private static void EnsureEquivalent(AccountStreamEventRecord existing, AccountStreamEventRecord candidate)
    {
        if (existing.EventId != candidate.EventId ||
            !string.Equals(existing.EventType, candidate.EventType, StringComparison.Ordinal) ||
            !string.Equals(existing.DedupeKey, candidate.DedupeKey, StringComparison.Ordinal) ||
            existing.EventTimeMilliseconds != candidate.EventTimeMilliseconds ||
            existing.ReceivedAtUtc.UtcTicks / 10 != candidate.ReceivedAtUtc.UtcTicks / 10 ||
            !string.Equals(existing.Symbol, candidate.Symbol, StringComparison.Ordinal) ||
            existing.SessionId != candidate.SessionId ||
            existing.SessionGeneration != candidate.SessionGeneration ||
            existing.SubscriptionId != candidate.SubscriptionId ||
            !string.Equals(existing.PayloadSha256, candidate.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.PayloadJson, candidate.PayloadJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The account stream event identity is already associated with different data.");
        }
    }

    private static bool EquivalentJson(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static string FeeComparisonStatus(ExecutionFeeComparisonStatus status) => status switch
    {
        ExecutionFeeComparisonStatus.NotComparable => "NOT_COMPARABLE",
        ExecutionFeeComparisonStatus.WithinTolerance => "WITHIN_TOLERANCE",
        ExecutionFeeComparisonStatus.Drift => "DRIFT",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported fee-comparison status."),
    };

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<string?> ReadStringAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(connection, transaction, sql, parameters, cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void EnsureHashEquivalent(string? existingHash, string candidateHash, string entity)
    {
        if (existingHash is null || !string.Equals(existingHash.Trim(), candidateHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{entity} identity is already associated with different data.");
        }
    }

    private static bool IsTransientPostgresException(PostgresException pex) =>
        pex.SqlState is "40001" or "40P01" ||
        pex.SqlState.StartsWith("08", StringComparison.Ordinal) ||
        pex.SqlState.StartsWith("57P", StringComparison.Ordinal);

    private static async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException pex) when (IsTransientPostgresException(pex) && attempt <= maxRetries)
            {
                var delayMs = (attempt * 25) + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10, 30);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
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

    public Task<CapitalReservationResult> TryReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteWithRetryAsync(ct => TryReserveCapitalCoreAsync(request, ct), cancellationToken: cancellationToken);

    private async Task<CapitalReservationResult> TryReserveCapitalCoreAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (var fenceCheck = new NpgsqlCommand(
            "SELECT worker_instance_id, fencing_token FROM trading.capital_reservation_fences WHERE account_id = @account_id FOR UPDATE;",
            connection,
            transaction))
        {
            fenceCheck.Parameters.Add(Parameter("account_id", NpgsqlDbType.Text, request.AccountId));
            await using var fenceReader = await fenceCheck.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await fenceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var currentWorker = fenceReader.GetString(0);
                var currentToken = fenceReader.GetInt64(1);
                if (request.FenceToken < currentToken ||
                    (request.FenceToken == currentToken &&
                     !string.Equals(request.WorkerInstanceId, currentWorker, StringComparison.Ordinal)))
                {
                    await fenceReader.CloseAsync().ConfigureAwait(false);
                    var currentActive = await SumActiveReservedCapitalAsync(connection, transaction, request.AccountId, request.Asset, cancellationToken).ConfigureAwait(false);
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new CapitalReservationResult(false, null, currentActive, "STALE_WORKER_FENCING_TOKEN");
                }
            }
        }

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.capital_reservation_fences (account_id, worker_instance_id, fencing_token, updated_at)
            VALUES (@account_id, @worker_instance_id, @fencing_token, @updated_at)
            ON CONFLICT (account_id) DO UPDATE
            SET worker_instance_id = EXCLUDED.worker_instance_id,
                fencing_token = EXCLUDED.fencing_token,
                updated_at = EXCLUDED.updated_at;
            """,
            [
                Parameter("account_id", NpgsqlDbType.Text, request.AccountId),
                Parameter("worker_instance_id", NpgsqlDbType.Text, request.WorkerInstanceId),
                Parameter("fencing_token", NpgsqlDbType.Bigint, request.FenceToken),
                Parameter("updated_at", NpgsqlDbType.TimestampTz, request.OccurredAtUtc.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadCapitalReservationByClientOrderIdAsync(connection, transaction, request.ClientOrderId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.ReservationId, request.ReservationId, StringComparison.Ordinal) ||
                !string.Equals(existing.AccountId, request.AccountId, StringComparison.Ordinal) ||
                !string.Equals(existing.Symbol, request.Symbol, StringComparison.Ordinal) ||
                !string.Equals(existing.Asset, request.Asset, StringComparison.Ordinal) ||
                existing.ReservedAmount != request.RequestedAmount)
            {
                throw new InvalidOperationException("Capital reservation clientOrderId is already associated with different parameters.");
            }

            var currentActive = await SumActiveReservedCapitalAsync(connection, transaction, request.AccountId, request.Asset, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CapitalReservationResult(true, existing, currentActive, "IDEMPOTENT_REPLAY");
        }

        var activeReserved = await SumActiveReservedCapitalAsync(connection, transaction, request.AccountId, request.Asset, cancellationToken).ConfigureAwait(false);
        var hasLockedUnknown = await HasLockedUnknownReservationsAsync(connection, transaction, request.AccountId, request.Asset, cancellationToken).ConfigureAwait(false);
        if (hasLockedUnknown)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CapitalReservationResult(false, null, activeReserved, "LOCKED_UNKNOWN_CAPITAL_ACTIVE");
        }

        var projectedCommitted = request.OpenPositionsCommittedAmount
            + activeReserved
            + request.RequestedAmount
            + request.NewOrderFeeReserveAmount
            + request.RealizedLossesAmount;

        if (projectedCommitted > request.EffectiveBudgetCeilingAmount)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CapitalReservationResult(false, null, activeReserved, "BUDGET_CEILING_EXCEEDED");
        }

        if (request.OpenPositionsCount + 1 > request.MaxOpenPositions)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CapitalReservationResult(false, null, activeReserved, "MAX_OPEN_POSITIONS_EXCEEDED");
        }

        var record = new CapitalReservationRecord(
            request.ReservationId,
            request.AccountId,
            request.Symbol,
            request.ClientOrderId,
            request.Asset,
            request.RequestedAmount,
            0m,
            CapitalReservationState.Pending,
            request.WorkerInstanceId,
            request.FenceToken,
            request.OccurredAtUtc,
            request.OccurredAtUtc,
            "INITIAL_RESERVATION",
            request.Environment);

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.capital_reservations
                (reservation_id, account_id, symbol, client_order_id, asset, reserved_amount, consumed_amount,
                 status, worker_instance_id, fencing_token, created_at, updated_at, reason)
            VALUES
                (@reservation_id, @account_id, @symbol, @client_order_id, @asset, @reserved_amount, 0,
                 @status, @worker_instance_id, @fencing_token, @created_at, @updated_at, @reason);
            """,
            [
                Parameter("reservation_id", NpgsqlDbType.Text, record.ReservationId),
                Parameter("account_id", NpgsqlDbType.Text, record.AccountId),
                Parameter("symbol", NpgsqlDbType.Text, record.Symbol),
                Parameter("client_order_id", NpgsqlDbType.Text, record.ClientOrderId),
                Parameter("asset", NpgsqlDbType.Text, record.Asset),
                Parameter("reserved_amount", NpgsqlDbType.Numeric, record.ReservedAmount),
                Parameter("status", NpgsqlDbType.Text, record.Status.ToString()),
                Parameter("worker_instance_id", NpgsqlDbType.Text, record.WorkerInstanceId),
                Parameter("fencing_token", NpgsqlDbType.Bigint, record.FenceToken),
                Parameter("created_at", NpgsqlDbType.TimestampTz, record.CreatedAtUtc.UtcDateTime),
                Parameter("updated_at", NpgsqlDbType.TimestampTz, record.UpdatedAtUtc.UtcDateTime),
                Parameter("reason", NpgsqlDbType.Text, record.Reason),
            ],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CapitalReservationResult(true, record, activeReserved + request.RequestedAmount, "RESERVED");
    }

    public async Task<CapitalReservationRecord> ReserveCapitalAsync(
        CapitalReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await TryReserveCapitalAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Reservation is null)
        {
            throw new InvalidOperationException($"Failed to reserve capital: {result.ReasonCode}");
        }

        return result.Reservation;
    }

    public Task<CapitalReservationRecord> TransitionReservationAsync(
        string clientOrderId,
        CapitalReservationStatus targetStatus,
        decimal consumedAmount,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWithRetryAsync(ct => TransitionReservationCoreAsync(clientOrderId, targetStatus, consumedAmount, workerInstanceId, fencingToken, occurredAtUtc, reason, ct), cancellationToken: cancellationToken);

    private async Task<CapitalReservationRecord> TransitionReservationCoreAsync(
        string clientOrderId,
        CapitalReservationStatus targetStatus,
        decimal consumedAmount,
        string workerInstanceId,
        long fencingToken,
        DateTimeOffset occurredAtUtc,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerInstanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(consumedAmount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fencingToken, 0L);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition timestamp must be UTC.", nameof(occurredAtUtc));
        }

        var normalizedId = clientOrderId.Trim();
        var normalizedWorker = workerInstanceId.Trim();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var existing = await ReadCapitalReservationByClientOrderIdAsync(connection, transaction, normalizedId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Capital reservation '{normalizedId}' was not found.");

        await using (var fenceCheck = new NpgsqlCommand(
            "SELECT worker_instance_id, fencing_token FROM trading.capital_reservation_fences WHERE account_id = @account_id FOR UPDATE;",
            connection,
            transaction))
        {
            fenceCheck.Parameters.Add(Parameter("account_id", NpgsqlDbType.Text, existing.AccountId));
            await using var fenceReader = await fenceCheck.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await fenceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var currentWorker = fenceReader.GetString(0);
                var currentToken = fenceReader.GetInt64(1);
                if (fencingToken < currentToken ||
                    (fencingToken == currentToken && !string.Equals(normalizedWorker, currentWorker, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("STALE_WORKER_FENCING_TOKEN");
                }
            }
        }

        if (existing.State == CapitalReservationState.LockedUnknown)
        {
            throw new InvalidOperationException("LOCKED_UNKNOWN_REQUIRES_RECONCILIATION");
        }

        if (existing.State == CapitalReservationState.Released && targetStatus != CapitalReservationStatus.Released)
        {
            throw new InvalidOperationException("Cannot transition a released capital reservation.");
        }

        if (consumedAmount > existing.ReservedAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(consumedAmount), "Consumed amount cannot exceed reserved amount.");
        }

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO trading.capital_reservation_fences (account_id, worker_instance_id, fencing_token, updated_at)
            VALUES (@account_id, @worker_instance_id, @fencing_token, @updated_at)
            ON CONFLICT (account_id) DO UPDATE
            SET worker_instance_id = EXCLUDED.worker_instance_id,
                fencing_token = EXCLUDED.fencing_token,
                updated_at = EXCLUDED.updated_at;
            """,
            [
                Parameter("account_id", NpgsqlDbType.Text, existing.AccountId),
                Parameter("worker_instance_id", NpgsqlDbType.Text, normalizedWorker),
                Parameter("fencing_token", NpgsqlDbType.Bigint, fencingToken),
                Parameter("updated_at", NpgsqlDbType.TimestampTz, occurredAtUtc.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            """
            UPDATE trading.capital_reservations
            SET status = @status,
                consumed_amount = @consumed_amount,
                worker_instance_id = @worker_instance_id,
                fencing_token = @fencing_token,
                updated_at = @updated_at,
                reason = @reason
            WHERE client_order_id = @client_order_id;
            """,
            [
                Parameter("status", NpgsqlDbType.Text, targetStatus.ToString()),
                Parameter("consumed_amount", NpgsqlDbType.Numeric, consumedAmount),
                Parameter("worker_instance_id", NpgsqlDbType.Text, normalizedWorker),
                Parameter("fencing_token", NpgsqlDbType.Bigint, fencingToken),
                Parameter("updated_at", NpgsqlDbType.TimestampTz, occurredAtUtc.UtcDateTime),
                Parameter("reason", NpgsqlDbType.Text, reason),
                Parameter("client_order_id", NpgsqlDbType.Text, normalizedId),
            ],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var targetState = targetStatus switch
        {
            CapitalReservationStatus.Reserved => CapitalReservationState.Pending,
            CapitalReservationStatus.CommittedToExchange => CapitalReservationState.Active,
            CapitalReservationStatus.PartiallyFilled => CapitalReservationState.PartiallyFilled,
            CapitalReservationStatus.Released => CapitalReservationState.Released,
            CapitalReservationStatus.LockedUnknown => CapitalReservationState.LockedUnknown,
            _ => CapitalReservationState.Pending,
        };
        return existing with
        {
            State = targetState,
            ConsumedAmount = consumedAmount,
            WorkerInstanceId = normalizedWorker,
            FenceToken = fencingToken,
            UpdatedAtUtc = occurredAtUtc,
            Reason = reason,
        };
    }

    public Task<CapitalReservationRecord> TransitionReservationStateAsync(
        string clientOrderId,
        CapitalReservationState targetState,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var targetStatus = targetState switch
        {
            CapitalReservationState.Pending => CapitalReservationStatus.Reserved,
            CapitalReservationState.Active => CapitalReservationStatus.CommittedToExchange,
            CapitalReservationState.PartiallyFilled => CapitalReservationStatus.PartiallyFilled,
            CapitalReservationState.Released => CapitalReservationStatus.Released,
            CapitalReservationState.LockedUnknown => CapitalReservationStatus.LockedUnknown,
            _ => CapitalReservationStatus.Reserved,
        };

        return TransitionReservationAsync(
            clientOrderId,
            targetStatus,
            consumedAmount,
            workerInstanceId,
            fenceToken,
            occurredAtUtc,
            reason,
            cancellationToken);
    }

    public Task<CapitalReservationRecord> ReleaseReservationOnTerminalOrderAsync(
        string clientOrderId,
        decimal consumedAmount,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionReservationStateAsync(
            clientOrderId,
            CapitalReservationState.Released,
            consumedAmount,
            workerInstanceId,
            fenceToken,
            occurredAtUtc,
            reason ?? "TERMINAL_ORDER_RELEASE",
            cancellationToken);

    public Task<CapitalReservationRecord> MarkReservationLockedUnknownAsync(
        string clientOrderId,
        string workerInstanceId,
        long fenceToken,
        DateTimeOffset occurredAtUtc,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionReservationStateAsync(
            clientOrderId,
            CapitalReservationState.LockedUnknown,
            0m,
            workerInstanceId,
            fenceToken,
            occurredAtUtc,
            reason ?? "ORDER_STATE_LOCKED_UNKNOWN",
            cancellationToken);

    public async Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationStatus resolvedStatus,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliationCorrelationId);
        ArgumentOutOfRangeException.ThrowIfNegative(consumedAmount);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Reconciliation timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (resolvedStatus is CapitalReservationStatus.Reserved or CapitalReservationStatus.LockedUnknown)
        {
            throw new ArgumentException("Reconciliation must resolve LockedUnknown to CommittedToExchange, PartiallyFilled, or Released.", nameof(resolvedStatus));
        }

        var normalizedId = clientOrderId.Trim();
        var reason = $"RECONCILED:{reconciliationCorrelationId.Trim()}";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var existing = await ReadCapitalReservationByClientOrderIdAsync(connection, transaction, normalizedId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Capital reservation '{normalizedId}' was not found.");

        if (consumedAmount > existing.ReservedAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(consumedAmount), "Consumed amount cannot exceed reserved amount.");
        }

        await ExecuteAsync(connection, transaction,
            """
            UPDATE trading.capital_reservations
            SET status = @status,
                consumed_amount = @consumed_amount,
                updated_at = @updated_at,
                reason = @reason
            WHERE client_order_id = @client_order_id;
            """,
            [
                Parameter("status", NpgsqlDbType.Text, resolvedStatus.ToString()),
                Parameter("consumed_amount", NpgsqlDbType.Numeric, consumedAmount),
                Parameter("updated_at", NpgsqlDbType.TimestampTz, occurredAtUtc.UtcDateTime),
                Parameter("reason", NpgsqlDbType.Text, reason),
                Parameter("client_order_id", NpgsqlDbType.Text, normalizedId),
            ],
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var resolvedState = resolvedStatus switch
        {
            CapitalReservationStatus.Reserved => CapitalReservationState.Pending,
            CapitalReservationStatus.CommittedToExchange => CapitalReservationState.Active,
            CapitalReservationStatus.PartiallyFilled => CapitalReservationState.PartiallyFilled,
            CapitalReservationStatus.Released => CapitalReservationState.Released,
            CapitalReservationStatus.LockedUnknown => CapitalReservationState.LockedUnknown,
            _ => CapitalReservationState.Pending,
        };
        return existing with
        {
            State = resolvedState,
            ConsumedAmount = consumedAmount,
            UpdatedAtUtc = occurredAtUtc,
            Reason = reason,
        };
    }

    public Task<CapitalReservationRecord> ReconcileLockedUnknownAsync(
        string clientOrderId,
        CapitalReservationState resolvedState,
        decimal consumedAmount,
        string reconciliationCorrelationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var resolvedStatus = resolvedState switch
        {
            CapitalReservationState.Active => CapitalReservationStatus.CommittedToExchange,
            CapitalReservationState.PartiallyFilled => CapitalReservationStatus.PartiallyFilled,
            CapitalReservationState.Released => CapitalReservationStatus.Released,
            _ => throw new ArgumentException("Reconciliation must resolve LockedUnknown to Active, PartiallyFilled, or Released.", nameof(resolvedState)),
        };

        return ReconcileLockedUnknownAsync(
            clientOrderId,
            resolvedStatus,
            consumedAmount,
            reconciliationCorrelationId,
            occurredAtUtc,
            cancellationToken);
    }

    public Task<IReadOnlyList<CapitalReservationRecord>> GetActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        ReadActiveReservationsAsync(accountId, cancellationToken);

    public async Task<IReadOnlyList<CapitalReservationRecord>> ReadActiveReservationsAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT reservation_id, account_id, symbol, client_order_id, asset,
                   reserved_amount, consumed_amount, status, worker_instance_id,
                   fencing_token, created_at, updated_at, reason
            FROM trading.capital_reservations
            WHERE account_id = @account_id
              AND status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled', 'LockedUnknown')
            ORDER BY created_at, reservation_id;
            """,
            connection);
        command.Parameters.Add(Parameter("account_id", NpgsqlDbType.Text, accountId.Trim()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CapitalReservationRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(MapCapitalReservation(reader));
        }

        return result;
    }

    private static async Task<bool> HasLockedUnknownReservationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string accountId,
        string asset,
        CancellationToken cancellationToken)
    {
        return await ExistsAsync(connection, transaction,
            """
            SELECT EXISTS (
                SELECT 1
                FROM trading.capital_reservations
                WHERE account_id = @account_id
                  AND asset = @asset
                  AND status = 'LockedUnknown'
            );
            """,
            [
                Parameter("account_id", NpgsqlDbType.Text, accountId),
                Parameter("asset", NpgsqlDbType.Text, asset),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<decimal> SumActiveReservedCapitalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string accountId,
        string asset,
        CancellationToken cancellationToken)
    {
        var scalar = await ExecuteScalarAsync(connection, transaction,
            """
            SELECT COALESCE(SUM(reserved_amount - consumed_amount), 0)
            FROM trading.capital_reservations
            WHERE account_id = @account_id
              AND asset = @asset
              AND status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled', 'LockedUnknown');
            """,
            [
                Parameter("account_id", NpgsqlDbType.Text, accountId),
                Parameter("asset", NpgsqlDbType.Text, asset),
            ],
            cancellationToken).ConfigureAwait(false);

        return scalar is decimal sum ? sum : 0m;
    }

    private static async Task<CapitalReservationRecord?> ReadCapitalReservationByClientOrderIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT reservation_id, account_id, symbol, client_order_id, asset,
                   reserved_amount, consumed_amount, status, worker_instance_id,
                   fencing_token, created_at, updated_at, reason
            FROM trading.capital_reservations
            WHERE client_order_id = @client_order_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.Add(Parameter("client_order_id", NpgsqlDbType.Text, clientOrderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapCapitalReservation(reader)
            : null;
    }

    private static CapitalReservationRecord MapCapitalReservation(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            Enum.Parse<CapitalReservationStatus>(reader.GetString(7), ignoreCase: false),
            reader.GetString(8),
            reader.GetInt64(9),
            Utc(reader.GetFieldValue<DateTime>(10)),
            Utc(reader.GetFieldValue<DateTime>(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12));
}

/// <summary>
/// Operational-control audit adapter. Its async append is the preferred boundary
/// for hosted services; the synchronous append remains only for legacy callers
/// whose domain contract still exposes a synchronous sink.
/// </summary>
public sealed class PostgresOperationalControlAuditSink(PostgresTradingStore store) : IOperationalControlAuditSink, IAsyncOperationalControlAuditSink
{
    public async Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(store);
        _ = await store.AppendOperationalControlAuditEventAsync(auditEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compatibility path for legacy synchronous callers. Hosted Worker
    /// transitions use <see cref="IAsyncOperationalControlAuditSink"/> and do
    /// not block a thread on PostgreSQL I/O.
    /// </summary>
    public void Append(OperationalControlAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(store);
        _ = store.AppendOperationalControlAuditEventAsync(auditEvent).GetAwaiter().GetResult();
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
        services.AddSingleton<ILocalOpenOrderStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IAccountStreamOrderProjectionStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IAutonomyDecisionCommitStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IExpectedExecutionFeeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IExecutionSubmissionContextStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IOrderLifecycleStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IRiskPolicyStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IAutonomyCheckpointStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IRuntimeStateStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IMarketRegimeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IModelStrategyRegistryStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IModelStrategyRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IDriftObservationStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IStrategyRegimePerformanceStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IExecutionLearningRewardStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IPortfolioPerformanceStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IPortfolioNavSnapshotStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IOperationalControlAuditStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IOperationalControlCurrentStateStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IAutonomyRiskContextStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<IDurableSubmissionStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddSingleton<ICapitalReservationStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresTradingStore>());
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>(
            "postgres",
            HealthStatus.Unhealthy,
            tags: ["ready", "postgres"]);
        return services;
    }
}
