using CryptoTrading.Domain.Runtime;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Owns process lifecycle and mirrors the operational control plane into the
/// runtime state machine. It does not authorize resets and never makes Ready
/// reachable without the supervisor's independent readiness gates.
/// </summary>
internal sealed class RuntimeSupervisorHostedService(
    IRuntimeSupervisor supervisor,
    IAsyncOperationalControl operationalControl,
    ILogger<RuntimeSupervisorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await supervisor.BeginBootstrappingAsync("worker-started", stoppingToken).ConfigureAwait(false);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                await supervisor.ObserveOperationalControlAsync(operationalControl.Snapshot, stoppingToken).ConfigureAwait(false);
                RuntimeSupervisorLog.Heartbeat(logger, supervisor.Snapshot.State.ToString(), supervisor.Snapshot.Revision);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown is handled by StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await supervisor.StopAsync("worker-stopping", cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class RuntimeSupervisorLog
{
    [LoggerMessage(LogLevel.Debug, "Runtime supervisor state={State} revision={Revision}")]
    public static partial void Heartbeat(ILogger logger, string state, long revision);
}
