using CryptoTrading.Observability;

namespace CryptoTrading.Worker;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The host creates this BackgroundService through dependency injection.")]
internal sealed partial class TradingWorker(ILogger<TradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            WorkerLog.Heartbeat(logger, DateTimeOffset.UtcNow);
            TradingTelemetry.RecordWorkerHeartbeat();
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "Trading worker heartbeat at {Timestamp}")]
    public static partial void Heartbeat(ILogger logger, DateTimeOffset timestamp);
}
