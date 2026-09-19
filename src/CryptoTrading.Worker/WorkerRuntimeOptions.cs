#pragma warning disable CA1515 // Options are public to support host-level composition and contract tests.

using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>Explicit runtime gates for the local worker. Defaults are fail-closed.</summary>
public sealed class WorkerRuntimeOptions
{
    public const string SectionName = "Worker";

    public bool PersistenceEnabled { get; set; }

    public bool ApplyMigrations { get; set; }

    public string PostgresConnectionString { get; set; } = string.Empty;

    public string ExecutionMode { get; set; } = "PAPER";

    public bool NoTrade { get; set; } = true;

    /// <summary>Runs one deterministic local Shadow drill at worker startup when enabled.</summary>
    public bool ShadowEnabled { get; set; }

    public string ShadowReportDirectory { get; set; } = "artifacts/shadow";

    public int PollIntervalMilliseconds { get; set; } = 1_000;

    public int BatchSize { get; set; } = 25;

    public int LeaseSeconds { get; set; } = 30;

    public int MaxOutboxAttempts { get; set; } = 5;

    /// <summary>Runs one authenticated, read-only account reconciliation at startup.</summary>
    public bool ReconciliationEnabled { get; set; }

    /// <summary>Requires an approved, active RiskPolicy in PostgreSQL before startup.</summary>
    public bool RiskPolicyRequired { get; set; }

    /// <summary>Enables the authenticated account-event stream with durable local persistence.</summary>
    public bool AccountStreamEnabled { get; set; }

    public string ReconciliationAccount { get; set; } = "exchange-wallet";

    public string ReconciliationSymbols { get; set; } = "BTCUSDT";

    public int ReconciliationMaxSnapshotAgeSeconds { get; set; } = 30;

    /// <summary>Periodic read-only reconciliation interval after startup, in minutes.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 15;

    public string ReconciliationReportDirectory { get; set; } = "artifacts/reconciliation";

    /// <summary>Optional local JSONL alert directory; blank keeps the in-memory sink.</summary>
    public string AlertFileDirectory { get; set; } = string.Empty;

    public void Validate()
    {
        if (!string.Equals(ExecutionMode, "PAPER", StringComparison.OrdinalIgnoreCase))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE must be PAPER until a separately approved live adapter exists."]);
        }

        if (PollIntervalMilliseconds is < 100 or > 60_000)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_POLL_INTERVAL_MS must be between 100 and 60000."]);
        }

        if (BatchSize is < 1 or > 500)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_OUTBOX_BATCH_SIZE must be between 1 and 500."]);
        }

        if (LeaseSeconds is < 1 or > 3_600)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_OUTBOX_LEASE_SECONDS must be between 1 and 3600."]);
        }

        if (MaxOutboxAttempts is < 1 or > 100)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_MAX_OUTBOX_ATTEMPTS must be between 1 and 100."]);
        }

        if (ReconciliationEnabled && !NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_ENABLED requires WORKER_NO_TRADE=true."]);
        }

        if (ReconciliationEnabled && !PersistenceEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_ENABLED requires WORKER_PERSISTENCE_ENABLED=true."]);
        }

        if (RiskPolicyRequired && !PersistenceEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RISK_POLICY_REQUIRED requires WORKER_PERSISTENCE_ENABLED=true."]);
        }

        if (AccountStreamEnabled && !NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ACCOUNT_STREAM_ENABLED requires WORKER_NO_TRADE=true."]);
        }

        if (AccountStreamEnabled && !PersistenceEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ACCOUNT_STREAM_ENABLED requires WORKER_PERSISTENCE_ENABLED=true."]);
        }

        if (AccountStreamEnabled && !ApplyMigrations)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ACCOUNT_STREAM_ENABLED requires WORKER_APPLY_MIGRATIONS=true."]);
        }

        if (AccountStreamEnabled && !ReconciliationEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ACCOUNT_STREAM_ENABLED requires WORKER_RECONCILIATION_ENABLED=true for initial state recovery."]);
        }

        if (ReconciliationEnabled && string.IsNullOrWhiteSpace(ReconciliationAccount))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_ACCOUNT is required when reconciliation is enabled."]);
        }

        if (ReconciliationEnabled && string.IsNullOrWhiteSpace(ReconciliationSymbols))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_SYMBOLS is required when reconciliation is enabled."]);
        }

        if (ReconciliationMaxSnapshotAgeSeconds is < 1 or > 86_400)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_MAX_SNAPSHOT_AGE_SECONDS must be between 1 and 86400."]);
        }

        if (ReconciliationIntervalMinutes is < 1 or > 1_440)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_INTERVAL_MINUTES must be between 1 and 1440."]);
        }

        if (ReconciliationEnabled && string.IsNullOrWhiteSpace(ReconciliationReportDirectory))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_REPORT_DIRECTORY is required when reconciliation is enabled."]);
        }

        if (PersistenceEnabled && string.IsNullOrWhiteSpace(PostgresConnectionString))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["POSTGRES_CONNECTION_STRING is required when WORKER_PERSISTENCE_ENABLED=true."]);
        }

        if (ShadowEnabled && !NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_SHADOW_ENABLED requires WORKER_NO_TRADE=true."]);
        }

        if (ShadowEnabled && string.IsNullOrWhiteSpace(ShadowReportDirectory))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_SHADOW_REPORT_DIRECTORY is required when WORKER_SHADOW_ENABLED=true."]);
        }
    }
}
