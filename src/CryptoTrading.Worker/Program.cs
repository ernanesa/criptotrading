using CryptoTrading.Configuration;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;
using Microsoft.Extensions.Options;
using System.Text.Json;
using CryptoTrading.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
builder.Configuration.AddLocalDotEnv();
builder.Services.AddTradingConfiguration(builder.Configuration);
// The worker's default PAPER/NO_TRADE mode does not require Binance credentials.
// The authenticated account client is registered only when the explicit,
// read-only reconciliation gate is enabled.
builder.Services.AddOptions<WorkerRuntimeOptions>()
    .Configure(options =>
    {
        options.PersistenceEnabled = builder.Configuration.GetValue("WORKER_PERSISTENCE_ENABLED", false);
        options.ApplyMigrations = builder.Configuration.GetValue("WORKER_APPLY_MIGRATIONS", false);
        options.PostgresConnectionString = builder.Configuration["POSTGRES_CONNECTION_STRING"] ?? string.Empty;
        options.ExecutionMode = builder.Configuration["WORKER_EXECUTION_MODE"] ?? "PAPER";
        options.NoTrade = builder.Configuration.GetValue("WORKER_NO_TRADE", true);
        options.ShadowEnabled = builder.Configuration.GetValue("WORKER_SHADOW_ENABLED", false);
        options.ShadowReportDirectory = builder.Configuration["WORKER_SHADOW_REPORT_DIRECTORY"] ?? "artifacts/shadow";
        options.PollIntervalMilliseconds = builder.Configuration.GetValue("WORKER_POLL_INTERVAL_MS", 1_000);
        options.BatchSize = builder.Configuration.GetValue("WORKER_OUTBOX_BATCH_SIZE", 25);
        options.LeaseSeconds = builder.Configuration.GetValue("WORKER_OUTBOX_LEASE_SECONDS", 30);
        options.MaxOutboxAttempts = builder.Configuration.GetValue("WORKER_MAX_OUTBOX_ATTEMPTS", 5);
        options.ReconciliationEnabled = builder.Configuration.GetValue("WORKER_RECONCILIATION_ENABLED", false);
        options.RiskPolicyRequired = builder.Configuration.GetValue("WORKER_RISK_POLICY_REQUIRED", false);
        options.AccountStreamEnabled = builder.Configuration.GetValue("WORKER_ACCOUNT_STREAM_ENABLED", false);
        options.ReconciliationAccount = builder.Configuration["WORKER_RECONCILIATION_ACCOUNT"] ?? "exchange-wallet";
        options.ReconciliationSymbols = builder.Configuration["WORKER_RECONCILIATION_SYMBOLS"] ?? "BTCUSDT";
        options.ReconciliationMaxSnapshotAgeSeconds = builder.Configuration.GetValue("WORKER_RECONCILIATION_MAX_SNAPSHOT_AGE_SECONDS", 30);
        options.ReconciliationIntervalMinutes = builder.Configuration.GetValue("WORKER_RECONCILIATION_INTERVAL_MINUTES", 15);
        options.ReconciliationReportDirectory = builder.Configuration["WORKER_RECONCILIATION_REPORT_DIRECTORY"] ?? "artifacts/reconciliation";
        options.AlertFileDirectory = builder.Configuration["WORKER_ALERT_FILE_DIRECTORY"] ?? string.Empty;
        options.AlertWebhookUrl = builder.Configuration["WORKER_ALERT_WEBHOOK_URL"] ?? string.Empty;
        options.AlertWebhookAllowedHosts = builder.Configuration["WORKER_ALERT_WEBHOOK_ALLOWED_HOSTS"] ?? string.Empty;
        options.AlertWebhookTimeoutSeconds = builder.Configuration.GetValue("WORKER_ALERT_WEBHOOK_TIMEOUT_SECONDS", 5);
    })
    .Validate(static options =>
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (OptionsValidationException)
        {
            return false;
        }
    }, "Worker runtime configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton<IPaperExchange, PaperExchange>();
builder.Services.AddSingleton<CryptoTrading.Observability.ITradingAlertSink>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<WorkerRuntimeOptions>>().Value;
    CryptoTrading.Observability.ITradingAlertSink sink;
    if (!string.IsNullOrWhiteSpace(options.AlertWebhookUrl))
    {
        var endpoint = new Uri(options.AlertWebhookUrl.Trim(), UriKind.Absolute);
        var allowedHosts = options.AlertWebhookAllowedHosts
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        sink = new CryptoTrading.Observability.HttpWebhookTradingAlertSink(
            new HttpClient { Timeout = TimeSpan.FromSeconds(options.AlertWebhookTimeoutSeconds) },
            new CryptoTrading.Observability.TradingAlertWebhookOptions
            {
                Enabled = true,
                Endpoint = endpoint,
                AllowedHosts = allowedHosts,
                RequestTimeout = TimeSpan.FromSeconds(options.AlertWebhookTimeoutSeconds)
            });
    }
    else
    {
        sink = string.IsNullOrWhiteSpace(options.AlertFileDirectory)
            ? new CryptoTrading.Observability.InMemoryTradingAlertSink()
            : new CryptoTrading.Observability.JsonlTradingAlertSink(options.AlertFileDirectory);
    }
    return new CryptoTrading.Observability.RetryingTradingAlertSink(sink);
});
builder.Services.AddSingleton<CryptoTrading.Observability.TradingAlertDispatcher>();
builder.Services.AddSingleton<CryptoTrading.Shadow.ShadowReportWriter>(serviceProvider =>
    new CryptoTrading.Shadow.ShadowReportWriter(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerRuntimeOptions>>().Value.ShadowReportDirectory));
builder.Services.AddSingleton<IOutboxMessageExecutor, PaperOutboxMessageExecutor>();
if (builder.Configuration.GetValue("WORKER_PERSISTENCE_ENABLED", false))
{
    _ = builder.Services.AddPostgresTradingStore(
        builder.Configuration["POSTGRES_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is required when persistence is enabled."));
    _ = builder.Services.AddSingleton<IRiskPolicyStore>(serviceProvider =>
        serviceProvider.GetRequiredService<PostgresTradingStore>());
    _ = builder.Services.AddSingleton<IAccountStreamEventStore>(serviceProvider =>
        serviceProvider.GetRequiredService<PostgresTradingStore>());
    _ = builder.Services.AddSingleton<IOutboxStore>(serviceProvider =>
        serviceProvider.GetRequiredService<PostgresTradingStore>());
    _ = builder.Services.AddSingleton<OrderProjectionService>();
}

// Operational-control transitions are always audited. The default remains
// local/in-memory; PostgreSQL is selected only by the explicit persistence
// gate above. A database outage then fails the transition closed without
// changing the in-memory control snapshot.
OperationalControlRegistration.Add(
    builder.Services,
    builder.Configuration.GetValue("WORKER_PERSISTENCE_ENABLED", false));

// Hosted services are started in registration order. Migrations must complete
// before reconciliation or account-stream persistence can be used.
builder.Services.AddHostedService<PersistenceMigrationHostedService>();
if (builder.Configuration.GetValue("WORKER_RECONCILIATION_ENABLED", false))
{
    _ = builder.Services.AddSingleton<ReconciliationReportWriter>(serviceProvider =>
        new ReconciliationReportWriter(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerRuntimeOptions>>().Value.ReconciliationReportDirectory));
    _ = builder.Services.AddBinanceSpotAccountClientFromConfiguration(builder.Configuration);
    _ = builder.Services.AddSingleton<IReadOnlyAccountSnapshotProvider>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerRuntimeOptions>>().Value;
        var reconciliationOptions = new ReconciliationOptions(
            new AccountId(options.ReconciliationAccount),
            options.ReconciliationSymbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            maxSnapshotAge: TimeSpan.FromSeconds(options.ReconciliationMaxSnapshotAgeSeconds));
        return new BinanceReadOnlySnapshotProvider(
            serviceProvider.GetRequiredService<IBinanceSpotAccountClient>(), reconciliationOptions);
    });
    _ = builder.Services.AddSingleton<ILocalBalanceReader, PostgresLocalBalanceReader>();
    _ = builder.Services.AddSingleton<IKnownTradeReader, PostgresKnownTradeReader>();
    _ = builder.Services.AddSingleton<ILocalOpenOrderReader, PostgresLocalOpenOrderReader>();
    _ = builder.Services.AddSingleton<IReconciliationEventSink, InMemoryReconciliationEventSink>();
    _ = builder.Services.AddSingleton<AccountReconciliationService>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerRuntimeOptions>>().Value;
        var reconciliationOptions = new ReconciliationOptions(
            new AccountId(options.ReconciliationAccount),
            options.ReconciliationSymbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            maxSnapshotAge: TimeSpan.FromSeconds(options.ReconciliationMaxSnapshotAgeSeconds));
        return new AccountReconciliationService(
            serviceProvider.GetRequiredService<IReadOnlyAccountSnapshotProvider>(),
            serviceProvider.GetRequiredService<ILocalBalanceReader>(),
            serviceProvider.GetRequiredService<IKnownTradeReader>(),
            serviceProvider.GetRequiredService<IReconciliationEventSink>(),
            serviceProvider.GetRequiredService<IOperationalControl>(),
            reconciliationOptions,
            localOpenOrderReader: serviceProvider.GetRequiredService<ILocalOpenOrderReader>());
    });
    builder.Services.AddHostedService<ReconciliationStartupHostedService>();
    builder.Services.AddHostedService<PeriodicReconciliationHostedService>();
}
if (builder.Configuration.GetValue("WORKER_RISK_POLICY_REQUIRED", false))
{
    builder.Services.AddHostedService<RiskPolicyStartupHostedService>();
}
if (builder.Configuration.GetValue("WORKER_ACCOUNT_STREAM_ENABLED", false))
{
    _ = builder.Services.AddSingleton<IBinanceUserDataStreamClient>(serviceProvider => new BinanceUserDataStreamApiClient(
        new BinanceUserDataStreamApiOptions
        {
            Environment = string.Equals(builder.Configuration["TRADING_ENVIRONMENT"], "DEMO", StringComparison.OrdinalIgnoreCase)
                ? BinanceEnvironment.Demo
                : BinanceEnvironment.Testnet,
            ApiKey = builder.Configuration["BINANCE_API_KEY"] ?? string.Empty,
            ApiSecret = builder.Configuration["BINANCE_API_SECRET"] ?? string.Empty,
            WebSocketApiBaseAddress = Uri.TryCreate(builder.Configuration["BINANCE_USER_STREAM_WS_API_BASE_URL"], UriKind.Absolute, out var wsBaseAddress)
                ? wsBaseAddress
                : null,
            MaxReconnectAttempts = 10
        },
        clock: serviceProvider.GetRequiredService<IBinanceClockDriftEstimator>()));
}
if (builder.Configuration.GetValue("WORKER_ACCOUNT_STREAM_ENABLED", false))
{
    builder.Services.AddHostedService<AccountStreamHostedService>();
}
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<CryptoTrading.Worker.TradingWorker>();
builder.Services.AddHostedService<ShadowDrillHostedService>();

var host = builder.Build();
host.Run();
