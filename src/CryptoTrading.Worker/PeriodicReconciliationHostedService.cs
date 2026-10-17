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
    CryptoTrading.Observability.TradingAlertDispatcher alertDispatcher,
    IAsyncOperationalControl operationalControl,
    CryptoTrading.Domain.Runtime.IRuntimeSupervisor runtimeSupervisor,
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
                if (!result.IsHealthy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    result = await reconciliation.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                var reportPath = await reportWriter.AppendAsync(new ReconciliationRunReport(
                    Guid.NewGuid().ToString("N"),
                    result.ObservedAtUtc,
                    result.IsHealthy,
                    result.Events.Count,
                    result.MismatchCount,
            result.UnknownTrades?.Count ?? result.UnknownTradeIds.Count,
                    result.OpenOrderCount), stoppingToken).ConfigureAwait(false);
                _ = await ReconciliationAlertPublisher.PublishAsync(result, alertDispatcher, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await runtimeSupervisor.MarkReconciliationAsync(
                    result.IsHealthy,
                    result.IsHealthy ? "periodic-reconciliation-healthy" : "periodic-reconciliation-mismatch",
                    stoppingToken).ConfigureAwait(false);

                if (result.IsHealthy)
                {
                    PeriodicReconciliationLog.Healthy(logger, result.CorrelationId.ToString(), reportPath);

                    if (operationalControl.Snapshot.Mode == OperationalMode.SafeMode &&
                        string.Equals(operationalControl.Snapshot.ChangedBy, "account-reconciliation", StringComparison.Ordinal))
                    {
                        var resetCommand = new OperationalControlCommand(
                            "account-reconciliation",
                            $"Reconciliation auto-recovery: balance and orders healthy (correlation {result.CorrelationId}).",
                            new IdempotencyKey($"reconciliation-auto-recovery:{result.CorrelationId}"),
                            DateTimeOffset.UtcNow);
                        var authorization = new OperationalControlResetAuthorization(
                            "account-reconciliation",
                            $"reconciliation-auto-recovery:{result.CorrelationId}");
                        _ = await operationalControl.ResetAsync(resetCommand, authorization, stoppingToken).ConfigureAwait(false);
                        PeriodicReconciliationLog.AutoRecovered(logger, result.CorrelationId.ToString());
                    }
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
            using var errorScope = logger.BeginScope(new Dictionary<string, object?> { ["error_stack"] = exception.StackTrace });
            try
            {
                _ = await operationalControl.EnterSafeModeAsync(new OperationalControlCommand(
                    "periodic-reconciliation",
                    "PERIODIC_RECONCILIATION_FAILURE",
                    IdempotencyKey.New(),
                    DateTimeOffset.UtcNow), stoppingToken).ConfigureAwait(false);
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
            throw new InvalidOperationException("Periodic reconciliation failed closed; inspect the correlated structured failure event.");
        }
    }
}

internal static partial class PeriodicReconciliationLog
{
    [LoggerMessage(LogLevel.Information, "Periodic reconciliation healthy. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Healthy(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Information, "Periodic reconciliation auto-recovered SafeMode to Normal. CorrelationId={CorrelationId}")]
    public static partial void AutoRecovered(ILogger logger, string correlationId);

    [LoggerMessage(LogLevel.Warning, "Periodic reconciliation blocked new entries. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Blocked(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation failed closed. ExceptionType={ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation SafeMode control failed. ExceptionType={ExceptionType}")]
    public static partial void ControlFailure(ILogger logger, string exceptionType);
}
