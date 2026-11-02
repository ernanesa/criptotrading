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
    ReconciliationOperationalControlResetAuthorizer resetAuthorizer,
    ILogger<PeriodicReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ReconciliationEnabled)
        {
            return;
        }

        var isFirstTick = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!isFirstTick)
                {
                    var isRecoverable = operationalControl.Snapshot.Mode == OperationalMode.SafeMode &&
                                        ReconciliationOperationalControlResetAuthorizer.IsAutoRecoverable(operationalControl.Snapshot) &&
                                        !resetAuthorizer.IsCircuitBreakerTripped(DateTimeOffset.UtcNow);

                    var targetWait = isRecoverable
                        ? TimeSpan.FromSeconds(Math.Clamp(options.Value.SafeModeRecoveryIntervalSeconds, 5, 300))
                        : TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 1, 1440));

                    var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!stoppingToken.IsCancellationRequested && System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp) < targetWait)
                    {
                        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp);
                        var remaining = targetWait - elapsed;
                        var slice = remaining > TimeSpan.FromSeconds(5)
                            ? TimeSpan.FromSeconds(5)
                            : (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                        if (slice > TimeSpan.Zero)
                        {
                            await Task.Delay(slice, stoppingToken).ConfigureAwait(false);
                        }

                        var activeRecoverable = operationalControl.Snapshot.Mode == OperationalMode.SafeMode &&
                                                ReconciliationOperationalControlResetAuthorizer.IsAutoRecoverable(operationalControl.Snapshot) &&
                                                !resetAuthorizer.IsCircuitBreakerTripped(DateTimeOffset.UtcNow);

                        if (activeRecoverable)
                        {
                            var recoveryWait = TimeSpan.FromSeconds(Math.Clamp(options.Value.SafeModeRecoveryIntervalSeconds, 5, 300));
                            if (targetWait > recoveryWait)
                            {
                                targetWait = recoveryWait;
                            }
                        }
                        else if (targetWait < TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 1, 1440)))
                        {
                            targetWait = TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 1, 1440));
                        }
                    }
                }
                isFirstTick = false;

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

                    if (ReconciliationOperationalControlResetAuthorizer.IsAutoRecoverable(operationalControl.Snapshot))
                    {
                        var resetCommand = new OperationalControlCommand(
                            "account-reconciliation",
                            $"Reconciliation auto-recovery: balance and orders healthy (correlation {result.CorrelationId}).",
                            new IdempotencyKey($"reconciliation-auto-recovery:{result.CorrelationId}"),
                            DateTimeOffset.UtcNow);
                        var authorization = new OperationalControlResetAuthorization(
                            "account-reconciliation",
                            $"reconciliation-auto-recovery:{result.CorrelationId}");
                        try
                        {
                            _ = await operationalControl.ResetAsync(resetCommand, authorization, stoppingToken).ConfigureAwait(false);
                            PeriodicReconciliationLog.AutoRecovered(logger, result.CorrelationId.ToString());
                            await runtimeSupervisor.ObserveOperationalControlAsync(operationalControl.Snapshot, stoppingToken).ConfigureAwait(false);
                        }
                        catch (OperationalControlResetDeniedException)
                        {
                            PeriodicReconciliationLog.AutoRecoveryFlappingBreakerTripped(logger, operationalControl.Snapshot.Reason ?? "Unknown");
                        }
                    }
                    else if (operationalControl.Snapshot.Mode == OperationalMode.SafeMode)
                    {
                        PeriodicReconciliationLog.AutoRecoveryHeld(logger, operationalControl.Snapshot.ChangedBy ?? "Unknown", operationalControl.Snapshot.Reason ?? "Unknown");
                    }
                }
                else
                {
                    PeriodicReconciliationLog.Blocked(logger, result.CorrelationId.ToString(), reportPath);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Periodic reconciliation converts unexpected tick failures into SafeMode and retries next tick instead of crashing host.
            catch (Exception exception)
            {
                using var errorScope = logger.BeginScope(new Dictionary<string, object?> { ["error_stack"] = exception.StackTrace });
                try
                {
                    _ = await operationalControl.EnterSafeModeAsync(new OperationalControlCommand(
                        "account-reconciliation",
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
            }
#pragma warning restore CA1031
        }
    }
}

internal static partial class PeriodicReconciliationLog
{
    [LoggerMessage(LogLevel.Information, "Periodic reconciliation healthy. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Healthy(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Information, "Periodic reconciliation auto-recovered SafeMode to Normal. CorrelationId={CorrelationId}")]
    public static partial void AutoRecovered(ILogger logger, string correlationId);

    [LoggerMessage(LogLevel.Information, "Periodic reconciliation evaluated SafeMode; auto-recovery held for operator intervention. ChangedBy={ChangedBy} Reason={Reason}")]
    public static partial void AutoRecoveryHeld(ILogger logger, string changedBy, string reason);

    [LoggerMessage(LogLevel.Warning, "Periodic reconciliation auto-recovery flapping circuit breaker tripped. SafeMode held for operator intervention. Reason={Reason}")]
    public static partial void AutoRecoveryFlappingBreakerTripped(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Warning, "Periodic reconciliation blocked new entries. CorrelationId={CorrelationId} ReportPath={ReportPath}")]
    public static partial void Blocked(ILogger logger, string correlationId, string reportPath);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation failed closed. ExceptionType={ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Periodic reconciliation SafeMode control failed. ExceptionType={ExceptionType}")]
    public static partial void ControlFailure(ILogger logger, string exceptionType);
}
