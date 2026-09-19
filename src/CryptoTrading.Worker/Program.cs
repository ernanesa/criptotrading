using CryptoTrading.Configuration;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
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
// Private exchange configuration is validated only by a future explicitly enabled adapter.
builder.Services.AddOptions<WorkerRuntimeOptions>()
    .Configure(options =>
    {
        options.PersistenceEnabled = builder.Configuration.GetValue("WORKER_PERSISTENCE_ENABLED", false);
        options.ApplyMigrations = builder.Configuration.GetValue("WORKER_APPLY_MIGRATIONS", false);
        options.PostgresConnectionString = builder.Configuration["POSTGRES_CONNECTION_STRING"] ?? string.Empty;
        options.ExecutionMode = builder.Configuration["WORKER_EXECUTION_MODE"] ?? "PAPER";
        options.NoTrade = builder.Configuration.GetValue("WORKER_NO_TRADE", true);
        options.PollIntervalMilliseconds = builder.Configuration.GetValue("WORKER_POLL_INTERVAL_MS", 1_000);
        options.BatchSize = builder.Configuration.GetValue("WORKER_OUTBOX_BATCH_SIZE", 25);
        options.LeaseSeconds = builder.Configuration.GetValue("WORKER_OUTBOX_LEASE_SECONDS", 30);
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
builder.Services.AddSingleton<IOutboxMessageExecutor, PaperOutboxMessageExecutor>();
if (builder.Configuration.GetValue("WORKER_PERSISTENCE_ENABLED", false))
{
    _ = builder.Services.AddPostgresTradingStore(
        builder.Configuration["POSTGRES_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is required when persistence is enabled."));
    _ = builder.Services.AddSingleton<IOutboxStore>(serviceProvider =>
        serviceProvider.GetRequiredService<PostgresTradingStore>());
}
builder.Services.AddHostedService<PersistenceMigrationHostedService>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<CryptoTrading.Worker.TradingWorker>();

var host = builder.Build();
host.Run();
