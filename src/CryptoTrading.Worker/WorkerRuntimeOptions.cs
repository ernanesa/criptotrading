#pragma warning disable CA1515 // Options are public to support host-level composition and contract tests.
#pragma warning disable CA1056 // Environment-bound configuration is kept as text until fail-closed validation at startup.

using Microsoft.Extensions.Options;
using CryptoTrading.Observability;

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

    /// <summary>Explicit second factor for the isolated Binance Spot Testnet adapter.</summary>
    public bool TestnetExecutionAcknowledged { get; set; }

    /// <summary>Explicit opt-in that maps the Worker outbox to Testnet order execution.</summary>
    public bool TestnetOrderExecutionEnabled { get; set; }

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

    /// <summary>Enables the closed-bar baseline decision loop; disabled by default.</summary>
    public bool AutonomyEnabled { get; set; }

    public string AutonomySymbol { get; set; } = "BTCUSDT";

    public string AutonomyInterval { get; set; } = "1m";

    public int AutonomyKlineLimit { get; set; } = 100;

    public decimal AutonomyEntryTargetNotional { get; set; } = 100m;

    public string ReconciliationAccount { get; set; } = "exchange-wallet";

    public string ReconciliationSymbols { get; set; } = "BTCUSDT";

    public int ReconciliationMaxSnapshotAgeSeconds { get; set; } = 30;

    /// <summary>Periodic read-only reconciliation interval after startup, in minutes.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 15;

    public string ReconciliationReportDirectory { get; set; } = "artifacts/reconciliation";

    /// <summary>Optional local JSONL alert directory; blank keeps the in-memory sink.</summary>
    public string AlertFileDirectory { get; set; } = string.Empty;

    /// <summary>Optional HTTPS webhook URL; blank keeps the local sink.</summary>
    public string AlertWebhookUrl { get; set; } = string.Empty;

    /// <summary>Comma-separated concrete hosts allowed for the alert webhook.</summary>
    public string AlertWebhookAllowedHosts { get; set; } = string.Empty;

    public int AlertWebhookTimeoutSeconds { get; set; } = 5;

    public void Validate()
    {
        var isPaper = string.Equals(ExecutionMode, "PAPER", StringComparison.OrdinalIgnoreCase);
        var isTestnet = string.Equals(ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase);
        if (!isPaper && !isTestnet)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE must be PAPER or TESTNET; live/demo modes are not supported."]);
        }

        if (isPaper && (TestnetExecutionAcknowledged || TestnetOrderExecutionEnabled))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["Testnet execution flags require WORKER_EXECUTION_MODE=TESTNET."]);
        }

        if (isTestnet && NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_NO_TRADE=false."]);
        }

        if (isTestnet && !PersistenceEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_PERSISTENCE_ENABLED=true."]);
        }

        if (isTestnet && !ApplyMigrations)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_APPLY_MIGRATIONS=true."]);
        }

        if (isTestnet && !TestnetExecutionAcknowledged)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_TESTNET_EXECUTION_ACKNOWLEDGED=true is required for Testnet execution."]);
        }

        if (isTestnet && !TestnetOrderExecutionEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_TESTNET_ORDER_EXECUTION_ENABLED=true is required for Testnet execution."]);
        }

        if (isTestnet && !RiskPolicyRequired)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_RISK_POLICY_REQUIRED=true."]);
        }

        if (isTestnet && !ReconciliationEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_RECONCILIATION_ENABLED=true."]);
        }

        if (isTestnet && !AccountStreamEnabled)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_EXECUTION_MODE=TESTNET requires WORKER_ACCOUNT_STREAM_ENABLED=true."]);
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

        if (isPaper && ReconciliationEnabled && !NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["PAPER reconciliation requires WORKER_NO_TRADE=true."]);
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

        if (isPaper && AccountStreamEnabled && !NoTrade)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["PAPER account stream requires WORKER_NO_TRADE=true."]);
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

        if (AutonomyEnabled)
        {
            if (!PersistenceEnabled || !ApplyMigrations || !RiskPolicyRequired || !ReconciliationEnabled || !AccountStreamEnabled)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_ENABLED requires persistence, migrations, RiskPolicy, reconciliation and account stream."]);
            }

            if (string.IsNullOrWhiteSpace(AutonomySymbol) || !string.Equals(AutonomySymbol.Trim(), "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_SYMBOL is restricted to BTCUSDT for the A2 baseline."]);
            }

            if (AutonomyInterval.Trim() is not ("1m" or "5m" or "15m" or "1h"))
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_INTERVAL must be one of 1m, 5m, 15m or 1h."]);
            }

            if (AutonomyKlineLimit is < 50 or > 1_000)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_KLINE_LIMIT must be between 50 and 1000."]);
            }

            if (AutonomyEntryTargetNotional <= 0m)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_ENTRY_TARGET_NOTIONAL must be greater than zero."]);
            }
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

        if (AlertWebhookTimeoutSeconds is < 1 or > 120)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ALERT_WEBHOOK_TIMEOUT_SECONDS must be between 1 and 120."]);
        }

        if (string.IsNullOrWhiteSpace(AlertWebhookUrl) && !string.IsNullOrWhiteSpace(AlertWebhookAllowedHosts))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_ALERT_WEBHOOK_ALLOWED_HOSTS requires WORKER_ALERT_WEBHOOK_URL."]);
        }

        if (!string.IsNullOrWhiteSpace(AlertWebhookUrl))
        {
            if (string.IsNullOrWhiteSpace(AlertWebhookAllowedHosts))
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_ALERT_WEBHOOK_URL requires WORKER_ALERT_WEBHOOK_ALLOWED_HOSTS."]);
            }

            if (!Uri.TryCreate(AlertWebhookUrl.Trim(), UriKind.Absolute, out var endpoint))
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_ALERT_WEBHOOK_URL must be an absolute HTTPS URL."]);
            }

            var allowedHosts = AlertWebhookAllowedHosts
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                new TradingAlertWebhookOptions
                {
                    Enabled = true,
                    Endpoint = endpoint,
                    AllowedHosts = allowedHosts,
                    RequestTimeout = TimeSpan.FromSeconds(AlertWebhookTimeoutSeconds)
                }.Validate();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_ALERT_WEBHOOK_URL/ALLOWED_HOSTS configuration is unsafe or invalid."]);
            }
        }
    }
}
