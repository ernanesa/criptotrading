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
                var isTransient = IsTransientStop(previous);
                var prefix = isTransient ? "RESTORED_TRANSIENT_STOP" : "RESTORED_DURABLE_STOP";
                var restore = new OperationalControlCommand("worker-bootstrap",
                    $"{prefix}:{previous.EventId:N}:{previous.Reason}", IdempotencyKey.New(), DateTimeOffset.UtcNow);
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

    internal static bool IsTransientStop(OperationalControlAuditEvent previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        if (previous.To != OperationalMode.SafeMode) return false;
        if (string.Equals(previous.Actor, "operator", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(previous.Reason)) return false;

        var reason = previous.Reason;

        // Fail-closed allowlist: only specific, well-known transient causes qualify
        // 1. Account stream transient transport disconnects
        if (string.Equals(previous.Actor, "account-stream", StringComparison.OrdinalIgnoreCase))
        {
            return reason.Contains("ACCOUNT_STREAM_EVENTSTREAMTERMINATED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_SERVERSHUTDOWN", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_CONNECTIONLOST", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_DISCONNECTED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_SOCKET_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_ENDED", StringComparison.OrdinalIgnoreCase);
        }

        // 2. Periodic reconciliation transient failures
        if (string.Equals(previous.Actor, "account-reconciliation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(previous.Actor, "periodic-reconciliation", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(reason, "PERIODIC_RECONCILIATION_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("CLOCK_SYNC_TRANSIENT_DRIFT", StringComparison.OrdinalIgnoreCase) ||
                   reason.StartsWith("Account reconciliation stale snapshot", StringComparison.OrdinalIgnoreCase);
        }

        // 3. Autonomy cycle transient failures
        if (string.Equals(previous.Actor, "autonomy-cycle", StringComparison.OrdinalIgnoreCase))
        {
            return reason.Contains("severity=Transient", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("CLOCK_SYNC_TRANSIENT_DRIFT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("Binance kline quality gate rejected the REST snapshot", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("Binance REST kline snapshot has a gap after the durable autonomy checkpoint", StringComparison.OrdinalIgnoreCase);
        }

        // 4. Restored transient stop chain
        if (string.Equals(previous.Actor, "worker-bootstrap", StringComparison.OrdinalIgnoreCase) &&
            reason.StartsWith("RESTORED_TRANSIENT_STOP", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fail-closed default: everything else is durable
        return false;
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
