using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class DeferredRuntimeSupervisorTests
{
    [Theory]
    [InlineData(RuntimeState.Bootstrapping)]
    [InlineData(RuntimeState.Reconciling)]
    [InlineData(RuntimeState.WarmingUp)]
    [InlineData(RuntimeState.Ready)]
    [InlineData(RuntimeState.Degraded)]
    [InlineData(RuntimeState.SafeMode)]
    public async Task NonTerminalPersistedStateRestoresAsSafeModeUntilFreshReadinessEvidence(RuntimeState checkpointState)
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(checkpointState, 7));
        var supervisor = new DeferredRuntimeSupervisor(new InMemoryRuntimeTransitionSink(), store);

        await supervisor.InitializeAsync();

        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        Assert.Equal(7, supervisor.Snapshot.Revision);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.BeginBootstrappingAsync("restart");
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);

        await supervisor.MarkReconciliationAsync(healthy: true, "reconciled-after-restart");
        await supervisor.MarkAccountStreamAsync(connected: true, "stream-connected-after-restart");
        await supervisor.MarkWarmupAsync(complete: true, "warmup-complete-after-restart");

        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.Equal(10, supervisor.Snapshot.Revision);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task KillSwitchCheckpointIsPreservedAndNeverRestoredToReady()
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(RuntimeState.KillSwitch, 11));
        var supervisor = new DeferredRuntimeSupervisor(new InMemoryRuntimeTransitionSink(), store);

        await supervisor.InitializeAsync();
        await supervisor.MarkReconciliationAsync(healthy: true, "reconciled");
        await supervisor.MarkAccountStreamAsync(connected: true, "stream-connected");
        await supervisor.MarkWarmupAsync(complete: true, "warmup-complete");

        Assert.Equal(RuntimeState.KillSwitch, supervisor.Snapshot.State);
        Assert.Equal(11, supervisor.Snapshot.Revision);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task InitializationIsIdempotentAndReadsTheCheckpointOnlyOnce()
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(RuntimeState.Stopped, 0));
        var supervisor = new DeferredRuntimeSupervisor(new InMemoryRuntimeTransitionSink(), store);

        await supervisor.InitializeAsync();
        await supervisor.InitializeAsync();
        await supervisor.BeginBootstrappingAsync("first-start");

        Assert.Equal(1, store.ReadCount);
        Assert.Equal(RuntimeState.Bootstrapping, supervisor.Snapshot.State);
        Assert.Equal(1, supervisor.Snapshot.Revision);
    }

    private sealed class CheckpointStore(RuntimeStateCheckpoint checkpoint) : IRuntimeStateStore
    {
        public int ReadCount { get; private set; }

        public Task<RuntimeStateCheckpoint> ReadRuntimeStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(checkpoint);
        }

        public Task AppendRuntimeTransitionAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The runtime transition sink is in-memory for this restore contract test.");
    }
}
