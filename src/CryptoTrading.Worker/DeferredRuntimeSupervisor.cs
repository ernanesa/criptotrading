using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;

#pragma warning disable CA2213 // inner is atomically exchanged and disposed by both disposal paths.

namespace CryptoTrading.Worker;

internal interface IRuntimeSupervisorInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defers checkpoint reconstruction until migrations have completed.</summary>
internal sealed class DeferredRuntimeSupervisor(
    IRuntimeTransitionSink sink,
    IRuntimeStateStore? store,
    long localJournalRevision = 0) : IRuntimeSupervisor, IRuntimeSupervisorInitializer, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private RuntimeSupervisor? inner;

    public RuntimeSupervisorSnapshot Snapshot => Get().Snapshot;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (inner is not null) return;
            var checkpoint = store is null
                ? new RuntimeStateCheckpoint(RuntimeState.Stopped, localJournalRevision)
                : await store.ReadRuntimeStateAsync(cancellationToken).ConfigureAwait(false);
            var supervisor = new RuntimeSupervisor(sink, initialRevision: checkpoint.Revision, initialState: checkpoint.State);
            try
            {
                await RestoreFailClosedAsync(supervisor, checkpoint, cancellationToken).ConfigureAwait(false);
                inner = supervisor;
            }
            catch
            {
                await supervisor.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    private static async Task RestoreFailClosedAsync(
        RuntimeSupervisor supervisor,
        RuntimeStateCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Revision == 0 || checkpoint.State == RuntimeState.KillSwitch)
        {
            return;
        }

        if (supervisor.Snapshot.State == RuntimeState.Stopping)
        {
            await supervisor.StopAsync("restart-completing-stop", cancellationToken).ConfigureAwait(false);
        }

        if (supervisor.Snapshot.State == RuntimeState.Stopped)
        {
            await supervisor.BeginBootstrappingAsync("restart-requires-reconciliation", cancellationToken).ConfigureAwait(false);
        }

        if (supervisor.Snapshot.State != RuntimeState.SafeMode)
        {
            await supervisor.EnterSafeModeAsync("restart-requires-reconciliation", cancellationToken).ConfigureAwait(false);
        }
    }

    public Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default) => Get().BeginBootstrappingAsync(reason, cancellationToken);
    public Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default) => Get().MarkReconciliationAsync(healthy, reason, cancellationToken);
    public Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default) => Get().MarkAccountStreamAsync(connected, reason, cancellationToken);
    public Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default) => Get().MarkWarmupAsync(complete, reason, cancellationToken);
    public Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default) => Get().EnterSafeModeAsync(reason, cancellationToken);
    public Task ObserveOperationalControlAsync(CryptoTrading.Domain.Risk.OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default) => Get().ObserveOperationalControlAsync(operationalControl, cancellationToken);
    public Task StopAsync(string reason, CancellationToken cancellationToken = default) => Get().StopAsync(reason, cancellationToken);

    public void Dispose()
    {
        Interlocked.Exchange(ref inner, null)?.Dispose();
        initializationGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        var supervisor = Interlocked.Exchange(ref inner, null);
        if (supervisor is not null)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        initializationGate.Dispose();
    }

    private RuntimeSupervisor Get() => Volatile.Read(ref inner) ?? throw new InvalidOperationException("Runtime supervisor was used before persistence initialization.");
}

#pragma warning restore CA2213
