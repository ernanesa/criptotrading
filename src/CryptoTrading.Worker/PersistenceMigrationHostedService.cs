#pragma warning disable CA1812 // Hosted services are instantiated by the generic host through dependency injection.

using CryptoTrading.Persistence;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Primitives;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

internal sealed class PersistenceMigrationHostedService(
    IEnumerable<PostgresTradingStore> stores,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<PersistenceMigrationHostedService> logger,
    IAsyncOperationalControl operationalControl) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.PersistenceEnabled)
        {
            return;
        }

        foreach (var store in stores)
        {
            if (options.Value.ApplyMigrations)
            {
                await store.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
            }
            await store.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);
            var previous = await store.ReadLatestOperationalControlAuditEventAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                var restore = new OperationalControlCommand("worker-bootstrap",
                    $"RESTORED_DURABLE_STOP:{previous.EventId:N}", IdempotencyKey.New(), DateTimeOffset.UtcNow);
                if (previous.To == OperationalMode.KillSwitch)
                {
                    _ = await operationalControl.ActivateKillSwitchAsync(restore, cancellationToken).ConfigureAwait(false);
                    PersistenceMigrationLog.RestoredStop(logger, previous.To, previous.EventId);
                }
                else if (previous.To == OperationalMode.SafeMode)
                {
                    _ = await operationalControl.EnterSafeModeAsync(restore, cancellationToken).ConfigureAwait(false);
                    PersistenceMigrationLog.RestoredStop(logger, previous.To, previous.EventId);
                }
            }
        }

        PersistenceMigrationLog.Connected(logger, stores.Count());
        if (options.Value.ApplyMigrations)
        {
            PersistenceMigrationLog.Applied(logger);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class PersistenceMigrationLog
{
    [LoggerMessage(LogLevel.Warning, "Durable operational stop restored before runtime startup. Mode={Mode} PreviousEventId={PreviousEventId}")]
    public static partial void RestoredStop(ILogger logger, OperationalMode mode, Guid previousEventId);
    [LoggerMessage(LogLevel.Information, "Trading PostgreSQL connectivity verified. Stores={StoreCount}")]
    public static partial void Connected(ILogger logger, int storeCount);

    [LoggerMessage(LogLevel.Information, "Trading persistence migrations applied.")]
    public static partial void Applied(ILogger logger);
}
