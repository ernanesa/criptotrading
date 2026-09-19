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
        if (!options.Value.ApplyMigrations)
        {
            return;
        }

        foreach (var store in stores)
        {
            await store.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
        }

        PersistenceMigrationLog.Applied(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class PersistenceMigrationLog
{
    [LoggerMessage(LogLevel.Information, "Trading persistence migrations applied.")]
    public static partial void Applied(ILogger logger);
}
