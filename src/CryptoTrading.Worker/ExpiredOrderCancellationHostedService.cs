#pragma warning disable CA1031 // Scan failures are logged and isolated from the dispatcher; no exchange call is issued here.

using CryptoTrading.Domain.Execution;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>Finds expired, durably acknowledged passive orders and enqueues deterministic cancel commands.</summary>
internal sealed class ExpiredOrderCancellationHostedService(
    IOptions<WorkerRuntimeOptions> options,
    IOrderCancellationStore cancellations,
    ILogger<ExpiredOrderCancellationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = options.Value;
        runtime.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var snapshots = await cancellations.ReadExpiredCancellableExecutionsAsync(now, runtime.BatchSize, stoppingToken).ConfigureAwait(false);
                foreach (var snapshot in snapshots)
                {
                    var planned = CancelLifecyclePlanner.Plan(snapshot);
                    if (planned.OutboxRecord is not null)
                    {
                        _ = await cancellations.EnqueueCancellationAsync(planned.OutboxRecord, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ExpiredCancellationLog.Failed(logger, exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(runtime.PollIntervalMilliseconds), stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class ExpiredCancellationLog
{
    [LoggerMessage(LogLevel.Error, "Expired-order cancellation scan failed closed. ErrorType={ErrorType}")]
    public static partial void Failed(ILogger logger, string errorType);
}
