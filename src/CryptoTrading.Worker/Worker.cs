using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Observability;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The host creates this BackgroundService through dependency injection.")]
internal sealed partial class TradingWorker(
    IOptions<WorkerRuntimeOptions> options,
    DiskSpaceSafetyGuard diskGuard,
    IOperationalControl operationalControl,
    IRuntimeSupervisor runtimeSupervisor,
    ReconciliationOperationalControlResetAuthorizer resetAuthorizer,
    ILogger<TradingWorker> logger,
    IRuntimeSupervisorInitializer? runtimeInitializer = null) : BackgroundService
{
    private readonly WorkerRuntimeOptions runtimeOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runtimeInitializer is not null)
        {
            await runtimeInitializer.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            WorkerLog.Heartbeat(logger, now);
            TradingTelemetry.RecordWorkerHeartbeat();

            // F15: Check disk space safety guard on each cycle
            var diskResult = diskGuard.CheckDiskSpace();

            // F11: Evaluate snapshot and update probe file
            var supervisorSnapshot = runtimeSupervisor.Snapshot;
            var opSnapshot = operationalControl.Snapshot;
            var runtimeStateStr = supervisorSnapshot.State.ToString();
            var noTrade = runtimeOptions.NoTrade || opSnapshot.BlocksNewEntries || runtimeOptions.ExecutionMode != "TESTNET";
            var financialReady = supervisorSnapshot.CanCreateNewExposure &&
                                 diskResult.IsHealthy &&
                                 !noTrade &&
                                 opSnapshot.Mode == OperationalMode.Normal;

            string? noTradeReason = null;
            var autoRecoveryEligible = false;
            if (noTrade)
            {
                if (opSnapshot.BlocksNewEntries)
                {
                    noTradeReason = opSnapshot.Reason ?? opSnapshot.Mode.ToString();
                    autoRecoveryEligible = ReconciliationOperationalControlResetAuthorizer.IsAutoRecoverable(opSnapshot) &&
                                           !resetAuthorizer.IsCircuitBreakerTripped(now);
                }
                else if (runtimeOptions.NoTrade)
                {
                    noTradeReason = "CONFIG_NO_TRADE";
                }
                else if (runtimeOptions.ExecutionMode != "TESTNET")
                {
                    noTradeReason = $"EXECUTION_MODE_{runtimeOptions.ExecutionMode}";
                }
                else if (!diskResult.IsHealthy)
                {
                    noTradeReason = diskResult.FailureReason ?? "DISK_SPACE_UNHEALTHY";
                }
                else
                {
                    noTradeReason = "UNKNOWN_RESTRICTION";
                }
            }

            var probeSnapshot = new WorkerProbeSnapshot(
                ProcessAlive: true,
                UpdatedAtUtc: now,
                RuntimeState: opSnapshot.BlocksNewEntries ? opSnapshot.Mode.ToString() : runtimeStateStr,
                ExecutionMode: runtimeOptions.ExecutionMode,
                NoTrade: noTrade,
                PersistenceHealthy: true,
                DiskSpaceHealthy: diskResult.IsHealthy,
                FinancialReady: financialReady,
                NoTradeReason: noTradeReason,
                AutoRecoveryEligible: autoRecoveryEligible);

            try
            {
                await WorkerHealthProbe.WriteProbeSnapshotAsync(
                    runtimeOptions.ProbeFilePath,
                    probeSnapshot,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WorkerLog.ProbeWriteFailed(logger, runtimeOptions.ProbeFilePath, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "Trading worker heartbeat at {Timestamp}")]
    public static partial void Heartbeat(ILogger logger, DateTimeOffset timestamp);

    [LoggerMessage(LogLevel.Warning, "Failed to write worker probe snapshot to {ProbeFilePath}")]
    public static partial void ProbeWriteFailed(ILogger logger, string? probeFilePath, Exception exception);
}
