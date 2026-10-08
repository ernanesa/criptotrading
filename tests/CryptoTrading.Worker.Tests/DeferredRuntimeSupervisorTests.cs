using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;
using Xunit;

#pragma warning disable CA2007 // xUnit assertions intentionally follow the test task context.

namespace CryptoTrading.Worker.Tests;

public sealed class DeferredRuntimeSupervisorTests
{
    [Theory]
    [InlineData(RuntimeState.Stopped, 9, 12)]
    [InlineData(RuntimeState.Bootstrapping, 8, 11)]
    [InlineData(RuntimeState.Reconciling, 8, 11)]
    [InlineData(RuntimeState.WarmingUp, 8, 11)]
    [InlineData(RuntimeState.Ready, 8, 11)]
    [InlineData(RuntimeState.Degraded, 8, 11)]
    [InlineData(RuntimeState.SafeMode, 7, 10)]
    [InlineData(RuntimeState.Stopping, 10, 13)]
    public async Task NonTerminalPersistedStateRestoresDurablyAsSafeModeUntilFreshReadinessEvidence(
        RuntimeState checkpointState,
        long restoredRevision,
        long readyRevision)
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(checkpointState, 7));
        await using var supervisor = new DeferredRuntimeSupervisor(new PostgresRuntimeTransitionSink(store), store);

        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        Assert.Equal(restoredRevision, supervisor.Snapshot.Revision);
        Assert.Equal(new RuntimeStateCheckpoint(RuntimeState.SafeMode, restoredRevision), store.Current);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.BeginBootstrappingAsync("restart", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);

        await supervisor.MarkReconciliationAsync(healthy: true, "reconciled-after-restart", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(connected: true, "stream-connected-after-restart", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(complete: true, "warmup-complete-after-restart", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.Equal(readyRevision, supervisor.Snapshot.Revision);
        Assert.Equal(new RuntimeStateCheckpoint(RuntimeState.Ready, readyRevision), store.Current);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task KillSwitchCheckpointIsPreservedAndNeverRestoredToReady()
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(RuntimeState.KillSwitch, 11));
        await using var supervisor = new DeferredRuntimeSupervisor(new PostgresRuntimeTransitionSink(store), store);

        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(healthy: true, "reconciled", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(connected: true, "stream-connected", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(complete: true, "warmup-complete", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.KillSwitch, supervisor.Snapshot.State);
        Assert.Equal(11, supervisor.Snapshot.Revision);
        Assert.Equal(new RuntimeStateCheckpoint(RuntimeState.KillSwitch, 11), store.Current);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task InitializationIsIdempotentAndReadsTheCheckpointOnlyOnce()
    {
        var store = new CheckpointStore(new RuntimeStateCheckpoint(RuntimeState.Stopped, 0));
        await using var supervisor = new DeferredRuntimeSupervisor(new PostgresRuntimeTransitionSink(store), store);

        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.InitializeAsync(TestContext.Current.CancellationToken);
        await supervisor.BeginBootstrappingAsync("first-start", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, store.ReadCount);
        Assert.Equal(RuntimeState.Bootstrapping, supervisor.Snapshot.State);
        Assert.Equal(1, supervisor.Snapshot.Revision);
    }

    private sealed class CheckpointStore(RuntimeStateCheckpoint checkpoint) : IRuntimeStateStore
    {
        private RuntimeStateCheckpoint current = checkpoint;

        public int ReadCount { get; private set; }

        public RuntimeStateCheckpoint Current => current;

        public Task<RuntimeStateCheckpoint> ReadRuntimeStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(current);
        }

        public Task AppendRuntimeTransitionAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Revision != transition.Revision - 1 || current.State != transition.From)
            {
                throw new InvalidOperationException("Runtime transition predecessor conflicts with the durable checkpoint.");
            }

            current = new RuntimeStateCheckpoint(transition.To, transition.Revision);
            return Task.CompletedTask;
        }
    }
}

#pragma warning restore CA2007
