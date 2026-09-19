#pragma warning disable CA1812 // Hosted services are instantiated by the generic host through dependency injection.

using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

internal sealed class PersistenceMigrationHostedService(
    IEnumerable<PostgresTradingStore> stores,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<PersistenceMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.PersistenceEnabled)
        {
            return;
        }

        foreach (var store in stores)
        {
            await store.CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);
            if (options.Value.ApplyMigrations)
            {
                await store.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
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
    [LoggerMessage(LogLevel.Information, "Trading PostgreSQL connectivity verified. Stores={StoreCount}")]
    public static partial void Connected(ILogger logger, int storeCount);

    [LoggerMessage(LogLevel.Information, "Trading persistence migrations applied.")]
    public static partial void Applied(ILogger logger);
}
