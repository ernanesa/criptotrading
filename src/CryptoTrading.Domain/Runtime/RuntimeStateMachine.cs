namespace CryptoTrading.Domain.Runtime;

/// <summary>Supervisory state of the autonomous trading runtime.</summary>
public enum RuntimeState
{
    Stopped,
    Bootstrapping,
    Reconciling,
    WarmingUp,
    Ready,
    Degraded,
    SafeMode,
    KillSwitch,
    Stopping
}

/// <summary>Immutable evidence for one accepted runtime transition.</summary>
public sealed record RuntimeStateTransition(
    RuntimeState From,
    RuntimeState To,
    long Revision,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId = null);

/// <summary>
/// Pure fail-closed runtime state machine. It deliberately does not know about
/// exchanges, persistence or hosting, so every adapter can test the same gates.
/// </summary>
public sealed class RuntimeStateMachine
{
    private long revision;

    public RuntimeStateMachine(RuntimeState initialState = RuntimeState.Stopped, long initialRevision = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialRevision);

        State = initialState;
        revision = initialRevision;
    }

    public RuntimeState State { get; private set; }

    public long Revision => revision;

    public bool CanCreateNewExposure => State == RuntimeState.Ready;

    public bool BlocksNewExposure => State is not RuntimeState.Ready;

    public RuntimeStateTransition TransitionTo(
        RuntimeState target,
        string reason,
        DateTimeOffset occurredAtUtc,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Runtime transition time must be UTC.", nameof(occurredAtUtc));
        }

        if (!IsAllowed(State, target))
        {
            throw new InvalidOperationException($"Runtime cannot transition from {State} to {target}.");
        }

        var transition = new RuntimeStateTransition(State, target, checked(++revision), reason.Trim(), occurredAtUtc, correlationId);
        State = target;
        return transition;
    }

    /// <summary>
    /// KillSwitch recovery is an explicitly authorized human operation. It
    /// always returns to reconciliation; it can never jump directly to Ready.
    /// </summary>
    public RuntimeStateTransition ResetKillSwitch(
        bool explicitlyAuthorized,
        string reason,
        DateTimeOffset occurredAtUtc,
        string? correlationId = null)
    {
        if (State != RuntimeState.KillSwitch)
        {
            throw new InvalidOperationException("KillSwitch can only be reset while the runtime is in KillSwitch.");
        }

        if (!explicitlyAuthorized)
        {
            throw new UnauthorizedAccessException("KillSwitch reset requires explicit authorization.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Runtime transition time must be UTC.", nameof(occurredAtUtc));
        }

        var transition = new RuntimeStateTransition(
            State,
            RuntimeState.Reconciling,
            checked(++revision),
            reason.Trim(),
            occurredAtUtc,
            correlationId);
        State = RuntimeState.Reconciling;
        return transition;
    }

    private static bool IsAllowed(RuntimeState from, RuntimeState to) =>
        from == to || (from, to) switch
        {
            (RuntimeState.Stopped, RuntimeState.Bootstrapping or RuntimeState.Stopping) => true,
            (RuntimeState.Bootstrapping, RuntimeState.Reconciling or RuntimeState.SafeMode or RuntimeState.Stopping) => true,
            (RuntimeState.Reconciling, RuntimeState.WarmingUp or RuntimeState.SafeMode or RuntimeState.Stopping) => true,
            (RuntimeState.WarmingUp, RuntimeState.Ready or RuntimeState.SafeMode or RuntimeState.Stopping) => true,
            (RuntimeState.Ready, RuntimeState.Degraded or RuntimeState.SafeMode or RuntimeState.KillSwitch or RuntimeState.Stopping) => true,
            (RuntimeState.Degraded, RuntimeState.Ready or RuntimeState.Reconciling or RuntimeState.SafeMode or RuntimeState.KillSwitch or RuntimeState.Stopping) => true,
            (RuntimeState.SafeMode, RuntimeState.Reconciling or RuntimeState.KillSwitch or RuntimeState.Stopping) => true,
            (RuntimeState.KillSwitch, RuntimeState.Stopping) => true,
            (RuntimeState.Stopping, RuntimeState.Stopped) => true,
            _ => false
        };
}
