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

    public int PollIntervalMilliseconds { get; set; } = 1_000;

    public int BatchSize { get; set; } = 25;

    public int LeaseSeconds { get; set; } = 30;

    public int MaxOutboxAttempts { get; set; } = 5;

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
    }
}
