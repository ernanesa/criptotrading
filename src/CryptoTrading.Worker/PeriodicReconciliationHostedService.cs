using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Reconciliation;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>Runs bounded, read-only reconciliation after startup at a fixed local cadence.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The host creates this hosted service through dependency injection.")]
internal sealed class PeriodicReconciliationHostedService(
    IOptions<WorkerRuntimeOptions> options,
    AccountReconciliationService reconciliation,
    ReconciliationReportWriter reportWriter,
    IOperationalControl operationalControl,
    ILogger<PeriodicReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ReconciliationEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.ReconciliationIntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var result = await reconciliation.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                var reportPath = await reportWriter.AppendAsync(new ReconciliationRunReport(
                    Guid.NewGuid().ToString("N"),
                    result.ObservedAtUtc,
                    result.IsHealthy,
                    result.Events.Count,
                    result.MismatchCount,
                    result.UnknownTradeIds.Count,
                    result.OpenOrderCount), stoppingToken).ConfigureAwait(false);

                if (result.IsHealthy)
                {
                    PeriodicReconciliationLog.Healthy(logger, result.CorrelationId.ToString(), reportPath);
                }
                else
                {
                    PeriodicReconciliationLog.Blocked(logger, result.CorrelationId.ToString(), reportPath);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            try
            {
                _ = operationalControl.EnterSafeMode(new OperationalControlCommand(
                    "periodic-reconciliation",
                    "PERIODIC_RECONCILIATION_FAILURE",
                    IdempotencyKey.New(),
                    DateTimeOffset.UtcNow));
            }
            catch (InvalidOperationException controlException)
            {
                PeriodicReconciliationLog.ControlFailure(logger, controlException.GetType().Name);
            }
            catch (ArgumentException controlException)
            {
                PeriodicReconciliationLog.ControlFailure(logger, controlException.GetType().Name);
            }

            PeriodicReconciliationLog.Failed(logger, exception.GetType().Name);
            throw;
        }
    }
}

internal static partial class PeriodicReconciliationLog
{
    [LoggerMessage(LogLevel.Information, "Periodic reconciliation healthy. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Healthy(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Warning, "Periodic reconciliation blocked new entries. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Blocked(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation failed closed. ExceptionType={ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation SafeMode control failed. ExceptionType={ExceptionType}")]
    public static partial void ControlFailure(ILogger logger, string exceptionType);
}
