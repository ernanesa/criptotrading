using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Domain.Runtime;

/// <summary>Point-in-time runtime readiness evidence.</summary>
public sealed record RuntimeSupervisorSnapshot(
    RuntimeState State,
    long Revision,
    bool ReconciliationHealthy,
    bool AccountStreamConnected,
    bool WarmupComplete,
    OperationalMode OperationalMode,
    long OperationalRevision)
{
    public bool CanCreateNewExposure =>
        State == RuntimeState.Ready &&
        ReconciliationHealthy &&
        AccountStreamConnected &&
        WarmupComplete &&
        OperationalMode == OperationalMode.Normal;

    public bool BlocksNewExposure => !CanCreateNewExposure;
}

/// <summary>
/// Coordinates lifecycle evidence from independent hosted services. It never
/// resets operational SafeMode/KillSwitch and cannot reach Ready without a
/// fresh reconciliation, a connected account stream and completed warm-up.
/// </summary>
public interface IRuntimeSupervisor
{
    RuntimeSupervisorSnapshot Snapshot { get; }

    Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default);

    Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default);

    Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default);

    Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default);

    Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default);

    Task ObserveOperationalControlAsync(OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default);

    Task StopAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe supervisor implementation over the pure state machine.</summary>
public sealed class RuntimeSupervisor : IRuntimeSupervisor, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RuntimeStateMachine stateMachine;
    private readonly IRuntimeTransitionSink transitionSink;
    private readonly TimeProvider timeProvider;
    private RuntimeSupervisorSnapshot snapshot;

    public RuntimeSupervisor(
        IRuntimeTransitionSink? transitionSink = null,
        TimeProvider? timeProvider = null,
        long initialRevision = 0,
        RuntimeState initialState = RuntimeState.Stopped)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialRevision);
        this.transitionSink = transitionSink ?? new InMemoryRuntimeTransitionSink();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        stateMachine = new RuntimeStateMachine(initialState, initialRevision);
        snapshot = new(
            initialState,
            initialRevision,
            ReconciliationHealthy: false,
            AccountStreamConnected: false,
            WarmupComplete: false,
            OperationalMode.Normal,
            0);
    }

    public RuntimeSupervisorSnapshot Snapshot => Volatile.Read(ref snapshot);

    public Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            if (stateMachine.State == RuntimeState.Stopped)
            {
                await TransitionAsync(RuntimeState.Bootstrapping, reason, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var current = Snapshot;
            if (!healthy)
            {
                await TransitionToSafeModeAsync(reason, cancellationToken).ConfigureAwait(false);
                SetSnapshot(Snapshot with { ReconciliationHealthy = false });
                return;
            }

            SetSnapshot(current with { ReconciliationHealthy = true });
            if (stateMachine.State == RuntimeState.Bootstrapping)
            {
                await TransitionAsync(RuntimeState.Reconciling, "reconciliation-started", cancellationToken).ConfigureAwait(false);
            }

            if (stateMachine.State == RuntimeState.SafeMode)
            {
                await TransitionAsync(RuntimeState.Reconciling, "reconciliation-recovered", cancellationToken).ConfigureAwait(false);
            }

            await AdvanceReadinessAsync(reason, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            SetSnapshot(connected
                ? Snapshot with { AccountStreamConnected = true }
                : Snapshot with { AccountStreamConnected = false, ReconciliationHealthy = false });
            if (!connected)
            {
                await TransitionToSafeModeAsync(reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (stateMachine.State == RuntimeState.SafeMode && Snapshot.ReconciliationHealthy)
            {
                await TransitionAsync(RuntimeState.Reconciling, "account-stream-recovered", cancellationToken).ConfigureAwait(false);
            }

            await AdvanceReadinessAsync(reason, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            SetSnapshot(Snapshot with { WarmupComplete = complete });
            if (!complete)
            {
                if (stateMachine.State == RuntimeState.Ready)
                {
                    await TransitionAsync(RuntimeState.Degraded, reason, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await AdvanceReadinessAsync(reason, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => TransitionToSafeModeAsync(reason, cancellationToken), cancellationToken);

    public Task ObserveOperationalControlAsync(OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationalControl);
        return ExecuteAsync(async () =>
        {
            SetSnapshot(Snapshot with
            {
                OperationalMode = operationalControl.Mode,
                OperationalRevision = operationalControl.Revision
            });

            if (operationalControl.Mode == OperationalMode.KillSwitch)
            {
                if (stateMachine.State is not RuntimeState.KillSwitch and not RuntimeState.Stopping and not RuntimeState.Stopped)
                {
                    await TransitionAsync(RuntimeState.KillSwitch, operationalControl.Reason ?? "operational-kill-switch", cancellationToken).ConfigureAwait(false);
                }
            }
            else if (operationalControl.Mode == OperationalMode.SafeMode)
            {
                await TransitionToSafeModeAsync(operationalControl.Reason ?? "operational-safe-mode", cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task StopAsync(string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            if (stateMachine.State == RuntimeState.Stopped)
            {
                return;
            }

            if (stateMachine.State != RuntimeState.Stopping)
            {
                await TransitionAsync(RuntimeState.Stopping, reason, cancellationToken).ConfigureAwait(false);
            }

            await TransitionAsync(RuntimeState.Stopped, "runtime-stopped", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task AdvanceReadinessAsync(string reason, CancellationToken cancellationToken)
    {
        if (stateMachine.State == RuntimeState.Reconciling && Snapshot.ReconciliationHealthy)
        {
            await TransitionAsync(RuntimeState.WarmingUp, "reconciliation-healthy", cancellationToken).ConfigureAwait(false);
        }

        if (stateMachine.State == RuntimeState.WarmingUp &&
            Snapshot.ReconciliationHealthy &&
            Snapshot.AccountStreamConnected &&
            Snapshot.WarmupComplete &&
            Snapshot.OperationalMode == OperationalMode.Normal)
        {
            await TransitionAsync(RuntimeState.Ready, reason, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TransitionToSafeModeAsync(string reason, CancellationToken cancellationToken)
    {
        if (stateMachine.State is RuntimeState.SafeMode or RuntimeState.KillSwitch or RuntimeState.Stopping or RuntimeState.Stopped)
        {
            return;
        }

        await TransitionAsync(RuntimeState.SafeMode, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(RuntimeState target, string reason, CancellationToken cancellationToken)
    {
        var transition = stateMachine.TransitionTo(target, reason, timeProvider.GetUtcNow());
        await transitionSink.AppendAsync(transition, cancellationToken).ConfigureAwait(false);
        SetSnapshot(Snapshot with { State = stateMachine.State, Revision = stateMachine.Revision });
    }

    private async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void SetSnapshot(RuntimeSupervisorSnapshot value) => Volatile.Write(ref snapshot, value);

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => gate.Dispose();
}
