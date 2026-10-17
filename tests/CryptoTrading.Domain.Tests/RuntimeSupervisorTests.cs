using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class RuntimeSupervisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadyRequiresReconciliationStreamAndWarmup()
    {
        var sink = new InMemoryRuntimeTransitionSink();
        using var supervisor = new RuntimeSupervisor(sink, new FixedTimeProvider(Now));

        await supervisor.BeginBootstrappingAsync("startup", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "reconciled", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "warmed", TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.WarmingUp, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.MarkAccountStreamAsync(true, "connected", TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        Assert.Equal(4, sink.Transitions.Count);
    }

    [Fact]
    public async Task ReconciliationMismatchEntersSafeModeAndRecoveryCannotBypassGates()
    {
        using var supervisor = new RuntimeSupervisor(timeProvider: new FixedTimeProvider(Now));

        await supervisor.BeginBootstrappingAsync("startup", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(false, "balance-drift", TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.MarkAccountStreamAsync(true, "connected", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "warmed", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);

        await supervisor.MarkReconciliationAsync(true, "recovered", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
    }

    [Fact]
    public async Task OperationalSafeModeAndKillSwitchRemainExposureBlocking()
    {
        using var supervisor = new RuntimeSupervisor(timeProvider: new FixedTimeProvider(Now));
        await supervisor.BeginBootstrappingAsync("startup", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "reconciled", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "warmed", TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "connected", TestContext.Current.CancellationToken);

        await supervisor.ObserveOperationalControlAsync(new(
            OperationalMode.SafeMode, 1, Now, "test", "operator-stop"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.ObserveOperationalControlAsync(new(
            OperationalMode.KillSwitch, 2, Now, "test", "emergency-stop"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.KillSwitch, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);
    }

    [Fact]
    public async Task RecoveryFromSafeModeWhenOperationalModeRestoredToNormalAdvancesToReady()
    {
        using var supervisor = new RuntimeSupervisor(timeProvider: new FixedTimeProvider(Now));
        await supervisor.BeginBootstrappingAsync("startup", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "reconciled", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "warmed", TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "connected", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);

        await supervisor.ObserveOperationalControlAsync(new(
            OperationalMode.SafeMode, 1, Now, "worker-bootstrap", "RESTORED_DURABLE_STOP"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.BlocksNewExposure);

        await supervisor.ObserveOperationalControlAsync(new(
            OperationalMode.Normal, 2, Now, "account-reconciliation", "reconciliation-healthy"), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
    }

    [Fact]
    public async Task RecoveryRequiresFreshReconciliationAfterStreamFailure()
    {
        using var supervisor = new RuntimeSupervisor(timeProvider: new FixedTimeProvider(Now));
        await supervisor.BeginBootstrappingAsync("startup", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "reconciled", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "warmed", TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "connected", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);

        await supervisor.MarkAccountStreamAsync(false, "disconnected", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        await supervisor.MarkAccountStreamAsync(true, "reconnected", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.SafeMode, supervisor.Snapshot.State);
        await supervisor.MarkReconciliationAsync(true, "reconciled-after-reconnect", TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
