#pragma warning disable CA1515 // Options are public to support host-level composition and contract tests.
#pragma warning disable CA1056 // Environment-bound configuration is kept as text until fail-closed validation at startup.

using Microsoft.Extensions.Options;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
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

    /// <summary>Autonomy decision mode: "Baseline" (A2), "RegimeAdaptive" (A3), "ContextualAllocation" (A4), "ContinuousLearning" (A5).</summary>
    public string AutonomyEngineMode { get; set; } = "RegimeAdaptive";

    public string AutonomySymbol { get; set; } = "BTCUSDT";

    /// <summary>Comma or whitespace-separated list of symbols scanned by the multi-asset autonomy engine.</summary>
    public string AutonomySymbols { get; set; } = string.Empty;

    public IReadOnlyList<string> ResolveAutonomySymbols()
    {
        var raw = !string.IsNullOrWhiteSpace(AutonomySymbols)
            ? AutonomySymbols
            : AutonomySymbol;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ["BTCUSDT"];
        }

        var list = raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count > 0 ? list : ["BTCUSDT"];
    }

    public string AutonomyInterval { get; set; } = "1m";

    public int AutonomyKlineLimit { get; set; } = 100;

    public decimal AutonomyEntryTargetNotional { get; set; } = 100m;

    /// <summary>Quote fee reserve in basis points applied to BUY sizing.</summary>
    public decimal AutonomyFeeReserveBps { get; set; } = FeeReservePolicy.DefaultReserveBps;

    /// <summary>Maximum permitted expected-versus-realized commission deviation.</summary>
    public decimal AutonomyFeeDriftToleranceBps { get; set; } = ExecutionFeeDriftEvaluator.DefaultToleranceBps;

    /// <summary>Maximum age of a non-future BNB conversion quote used for fill attribution.</summary>
    public int FeeAssetValuationMaxAgeSeconds { get; set; } = 30;

    public string ReconciliationAccount { get; set; } = "exchange-wallet";

    public string ReconciliationSymbols { get; set; } = "BTCUSDT";

    public int ReconciliationMaxSnapshotAgeSeconds { get; set; } = 30;

    /// <summary>Periodic read-only reconciliation interval after startup, in minutes.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 15;

    public string ReconciliationReportDirectory { get; set; } = "artifacts/reconciliation";

    /// <summary>Append-only local runtime lifecycle journal path.</summary>
    public string RuntimeAuditPath { get; set; } = "artifacts/runtime/runtime-transitions.jsonl";

    /// <summary>Optional local JSONL alert directory; blank keeps the in-memory sink.</summary>
    public string AlertFileDirectory { get; set; } = string.Empty;

    /// <summary>Optional HTTPS webhook URL; blank keeps the local sink.</summary>
    public string AlertWebhookUrl { get; set; } = string.Empty;

    /// <summary>Comma-separated concrete hosts allowed for the alert webhook.</summary>
    public string AlertWebhookAllowedHosts { get; set; } = string.Empty;

    public int AlertWebhookTimeoutSeconds { get; set; } = 5;

    /// <summary>Minimum free disk bytes required before latching to SafeMode (default 2 GB).</summary>
    public long MinFreeDiskBytes { get; set; } = 2_147_483_648L;

    /// <summary>Minimum free disk ratio (0.0 to 1.0) required before latching to SafeMode (default 10%).</summary>
    public decimal MinFreeDiskRatio { get; set; } = 0.10m;

    /// <summary>File path where the worker writes periodic JSON health/probe snapshots.</summary>
    public string ProbeFilePath { get; set; } = "/tmp/cryptotrading-worker-probe.json";

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

        if (FeeAssetValuationMaxAgeSeconds is < 1 or > 300)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_FEE_ASSET_VALUATION_MAX_AGE_SECONDS must be between 1 and 300."]);
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

            var symbols = ResolveAutonomySymbols();
            if (symbols.Count == 0)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_SYMBOLS must contain at least one valid symbol."]);
            }

            var allowedUniverse = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"
            };

            foreach (var symbol in symbols)
            {
                if (!allowedUniverse.Contains(symbol))
                {
                    throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                        [$"WORKER_AUTONOMY_SYMBOLS contains unauthorized symbol '{symbol}'. Allowed universe: BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT."]);
                }
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

            if (AutonomyFeeReserveBps is < 0m or > FeeReservePolicy.MaximumReserveBps)
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    [$"WORKER_AUTONOMY_FEE_RESERVE_BPS must be between 0 and {FeeReservePolicy.MaximumReserveBps}."]);
            }

            if (!string.Equals(AutonomyEngineMode, "Baseline", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(AutonomyEngineMode, "RegimeAdaptive", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(AutonomyEngineMode, "ContextualAllocation", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(AutonomyEngineMode, "ContinuousLearning", StringComparison.OrdinalIgnoreCase))
            {
                throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                    ["WORKER_AUTONOMY_ENGINE_MODE must be one of Baseline, RegimeAdaptive, ContextualAllocation or ContinuousLearning."]);
            }
        }

        if (ReconciliationEnabled && string.IsNullOrWhiteSpace(ReconciliationAccount))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RECONCILIATION_ACCOUNT is required when reconciliation is enabled."]);
        }

        if (AutonomyFeeDriftToleranceBps is < 0m or > 10_000m)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_AUTONOMY_FEE_DRIFT_TOLERANCE_BPS must be between 0 and 10000."]);
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

        if (string.IsNullOrWhiteSpace(RuntimeAuditPath))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_RUNTIME_AUDIT_PATH cannot be empty."]);
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

        if (MinFreeDiskBytes <= 0)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_MIN_FREE_DISK_BYTES must be greater than zero."]);
        }

        if (MinFreeDiskRatio is <= 0m or >= 1m)
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_MIN_FREE_DISK_RATIO must be strictly between 0 and 1."]);
        }

        if (string.IsNullOrWhiteSpace(ProbeFilePath))
        {
            throw new OptionsValidationException(nameof(WorkerRuntimeOptions), typeof(WorkerRuntimeOptions),
                ["WORKER_PROBE_FILE_PATH cannot be empty."]);
        }
    }
}
