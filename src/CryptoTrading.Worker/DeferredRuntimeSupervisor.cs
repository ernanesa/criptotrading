using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;

namespace CryptoTrading.Worker;

internal interface IRuntimeSupervisorInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defers checkpoint reconstruction until migrations have completed.</summary>
internal sealed class DeferredRuntimeSupervisor(
    IRuntimeTransitionSink sink,
    IRuntimeStateStore? store,
    long localJournalRevision = 0) : IRuntimeSupervisor, IRuntimeSupervisorInitializer
{
    private RuntimeSupervisor? inner;

    public RuntimeSupervisorSnapshot Snapshot => Get().Snapshot;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (inner is not null) return;
        var checkpoint = store is null
            ? new RuntimeStateCheckpoint(RuntimeState.Stopped, localJournalRevision)
            : await store.ReadRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
        var state = checkpoint.State == RuntimeState.KillSwitch ? RuntimeState.KillSwitch
            : checkpoint.Revision == 0 ? RuntimeState.Stopped : RuntimeState.SafeMode;
        var supervisor = new RuntimeSupervisor(sink, initialRevision: checkpoint.Revision, initialState: state);
        if (Interlocked.CompareExchange(ref inner, supervisor, null) is not null)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default) => Get().BeginBootstrappingAsync(reason, cancellationToken);
    public Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default) => Get().MarkReconciliationAsync(healthy, reason, cancellationToken);
    public Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default) => Get().MarkAccountStreamAsync(connected, reason, cancellationToken);
    public Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default) => Get().MarkWarmupAsync(complete, reason, cancellationToken);
    public Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default) => Get().EnterSafeModeAsync(reason, cancellationToken);
    public Task ObserveOperationalControlAsync(CryptoTrading.Domain.Risk.OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default) => Get().ObserveOperationalControlAsync(operationalControl, cancellationToken);
    public Task StopAsync(string reason, CancellationToken cancellationToken = default) => Get().StopAsync(reason, cancellationToken);
    private RuntimeSupervisor Get() => Volatile.Read(ref inner) ?? throw new InvalidOperationException("Runtime supervisor was used before persistence initialization.");
}
