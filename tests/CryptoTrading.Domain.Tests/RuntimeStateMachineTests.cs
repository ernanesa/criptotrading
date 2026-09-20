using CryptoTrading.Domain.Runtime;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class RuntimeStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadyIsTheOnlyStateThatAllowsNewExposure()
    {
        var machine = new RuntimeStateMachine();

        Assert.True(machine.BlocksNewExposure);
        machine.TransitionTo(RuntimeState.Bootstrapping, "startup", Now);
        machine.TransitionTo(RuntimeState.Reconciling, "startup-reconcile", Now.AddSeconds(1));
        machine.TransitionTo(RuntimeState.WarmingUp, "market-warmup", Now.AddSeconds(2));
        machine.TransitionTo(RuntimeState.Ready, "dependencies-healthy", Now.AddSeconds(3));

        Assert.True(machine.CanCreateNewExposure);
        Assert.False(machine.BlocksNewExposure);
        machine.TransitionTo(RuntimeState.SafeMode, "stale-data", Now.AddSeconds(4));
        Assert.False(machine.CanCreateNewExposure);
    }

    [Fact]
    public void SafeModeMustReconcileBeforeReady()
    {
        var machine = new RuntimeStateMachine(RuntimeState.SafeMode);

        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(RuntimeState.Ready, "unsafe-bypass", Now));
        machine.TransitionTo(RuntimeState.Reconciling, "recovery", Now);
        machine.TransitionTo(RuntimeState.WarmingUp, "fresh-data", Now.AddSeconds(1));
        machine.TransitionTo(RuntimeState.Ready, "recovered", Now.AddSeconds(2));
        Assert.True(machine.CanCreateNewExposure);
    }

    [Fact]
    public void KillSwitchCannotResetWithoutExplicitAuthorization()
    {
        var machine = new RuntimeStateMachine(RuntimeState.Ready);
        machine.TransitionTo(RuntimeState.KillSwitch, "operator-stop", Now);

        Assert.Throws<UnauthorizedAccessException>(() => machine.ResetKillSwitch(false, "not-authorized", Now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(RuntimeState.Ready, "automatic-reset", Now.AddSeconds(1)));

        var reset = machine.ResetKillSwitch(true, "approved-recovery", Now.AddSeconds(2), "approval-1");
        Assert.Equal(RuntimeState.Reconciling, reset.To);
        Assert.Equal(2, machine.Revision);
    }

    [Fact]
    public void TransitionsAreMonotonicAndRequireUtcReason()
    {
        var machine = new RuntimeStateMachine();
        var transition = machine.TransitionTo(RuntimeState.Bootstrapping, "startup", Now, "corr-1");

        Assert.Equal(1, transition.Revision);
        Assert.Equal("corr-1", transition.CorrelationId);
        Assert.Throws<ArgumentException>(() => machine.TransitionTo(RuntimeState.Reconciling, " ", Now));
        Assert.Throws<ArgumentException>(() => machine.TransitionTo(RuntimeState.Reconciling, "local-time", Now.ToOffset(TimeSpan.FromHours(-3))));
    }
}
