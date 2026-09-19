using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Risk;

/// <summary>Operational state that is allowed to block new exposure independently of a strategy.</summary>
public enum OperationalMode
{
    Normal,
    SafeMode,
    KillSwitch,
}

/// <summary>Immutable, point-in-time state used by risk evaluation and readiness checks.</summary>
public sealed record OperationalControlSnapshot(
    OperationalMode Mode,
    long Revision,
    DateTimeOffset ChangedAtUtc,
    string? ChangedBy,
    string? Reason)
{
    public bool BlocksNewEntries => Mode is OperationalMode.SafeMode or OperationalMode.KillSwitch;

    public static OperationalControlSnapshot Initial { get; } =
        new(OperationalMode.Normal, 0, DateTimeOffset.UnixEpoch, null, null);
}

/// <summary>Explicit command accepted by the operational control plane.</summary>
public sealed record OperationalControlCommand
{
    public string Actor { get; }
    public string Reason { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public DateTimeOffset OccurredAtUtc { get; }

    public OperationalControlCommand(
        string actor,
        string reason,
        IdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc)
    {
        Actor = ValidateText(actor, nameof(actor), 200);
        Reason = ValidateText(reason, nameof(reason), 500);
        IdempotencyKey = idempotencyKey;
        OccurredAtUtc = ValidateUtc(occurredAtUtc);
    }

    private static string ValidateText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }

    private static DateTimeOffset ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Operational control timestamps must be UTC.", nameof(value));
        }

        return value;
    }
}

/// <summary>Authorization material supplied by an external control-plane authorizer for a reset.</summary>
public sealed record OperationalControlResetAuthorization
{
    public string Actor { get; }
    public string ApprovalId { get; }

    public OperationalControlResetAuthorization(string actor, string approvalId)
    {
        Actor = Validate(actor, nameof(actor), 200);
        ApprovalId = Validate(approvalId, nameof(approvalId), 200);
    }

    private static string Validate(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

/// <summary>External authorization boundary. Implementations must enforce authentication and step-up policy.</summary>
public interface IOperationalControlResetAuthorizer
{
    bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand resetCommand, OperationalControlResetAuthorization authorization);
}

public interface IOperationalControl
{
    OperationalControlSnapshot Snapshot { get; }

    OperationalControlResult ActivateKillSwitch(OperationalControlCommand command);

    OperationalControlResult EnterSafeMode(OperationalControlCommand command);

    OperationalControlResult Reset(OperationalControlCommand command, OperationalControlResetAuthorization authorization);
}

public sealed record OperationalControlAuditEvent(
    Guid EventId,
    OperationalMode From,
    OperationalMode To,
    string Actor,
    string Reason,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string? ApprovalId);

public sealed record OperationalControlResult(
    OperationalControlSnapshot Snapshot,
    OperationalControlAuditEvent AuditEvent,
    bool StateChanged);

public sealed class OperationalControlResetDeniedException : UnauthorizedAccessException
{
    public OperationalControlResetDeniedException()
        : base("Operational control reset was not authorized.")
    {
    }

    public OperationalControlResetDeniedException(string message)
        : base(message)
    {
    }

    public OperationalControlResetDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thread-safe in-memory control plane for the local runtime. Persistence and transport
/// adapters can wrap the same contract later; this class deliberately performs no I/O.
/// </summary>
public sealed class InMemoryOperationalControl : IOperationalControl
{
    private readonly object gate = new();
    private readonly IOperationalControlResetAuthorizer resetAuthorizer;
    private readonly Dictionary<IdempotencyKey, (OperationalControlCommand Command, OperationalControlResult Result)> commands = [];
    private OperationalControlSnapshot snapshot = OperationalControlSnapshot.Initial;

    public InMemoryOperationalControl(IOperationalControlResetAuthorizer resetAuthorizer)
    {
        ArgumentNullException.ThrowIfNull(resetAuthorizer);
        this.resetAuthorizer = resetAuthorizer;
    }

    public OperationalControlSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public OperationalControlResult ActivateKillSwitch(OperationalControlCommand command) =>
        Apply(command, OperationalMode.KillSwitch, approvalId: null);

    public OperationalControlResult EnterSafeMode(OperationalControlCommand command) =>
        Apply(command, OperationalMode.SafeMode, approvalId: null);

    public OperationalControlResult Reset(OperationalControlCommand command, OperationalControlResetAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorization);

        lock (gate)
        {
            if (commands.TryGetValue(command.IdempotencyKey, out var previous))
            {
                EnsureReplayMatches(previous.Command, command);
                return previous.Result with { StateChanged = false };
            }

            if (!resetAuthorizer.IsAuthorized(snapshot, command, authorization))
            {
                throw new OperationalControlResetDeniedException();
            }

            return ApplyLocked(command, OperationalMode.Normal, authorization.ApprovalId);
        }
    }

    private OperationalControlResult Apply(OperationalControlCommand command, OperationalMode requestedMode, string? approvalId)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (gate)
        {
            if (commands.TryGetValue(command.IdempotencyKey, out var previous))
            {
                EnsureReplayMatches(previous.Command, command);
                return previous.Result with { StateChanged = false };
            }

            return ApplyLocked(command, requestedMode, approvalId);
        }
    }

    private OperationalControlResult ApplyLocked(OperationalControlCommand command, OperationalMode requestedMode, string? approvalId)
    {
        var previous = snapshot;
        var effectiveMode = requestedMode is OperationalMode.SafeMode && previous.Mode is OperationalMode.KillSwitch
            ? OperationalMode.KillSwitch
            : requestedMode;
        var changed = previous.Mode != effectiveMode;
        var next = changed
            ? new OperationalControlSnapshot(effectiveMode, previous.Revision + 1, command.OccurredAtUtc, command.Actor, command.Reason)
            : previous;
        snapshot = next;

        var audit = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            previous.Mode,
            effectiveMode,
            command.Actor,
            command.Reason,
            command.IdempotencyKey,
            command.OccurredAtUtc,
            approvalId);
        var result = new OperationalControlResult(next, audit, changed);
        commands.Add(command.IdempotencyKey, (command, result));
        return result;
    }

    private static void EnsureReplayMatches(OperationalControlCommand previous, OperationalControlCommand current)
    {
        if (previous.Actor != current.Actor || previous.Reason != current.Reason || previous.OccurredAtUtc != current.OccurredAtUtc)
        {
            throw new InvalidOperationException(
                $"Operational control idempotency key '{current.IdempotencyKey}' was reused with different command data.");
        }
    }
}
