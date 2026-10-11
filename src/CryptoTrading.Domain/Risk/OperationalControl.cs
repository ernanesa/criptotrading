using CryptoTrading.Domain.Primitives;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>
/// Async operational-control boundary for hosted services. Implementations
/// must await durable audit persistence before returning. Restrictive transitions
/// may publish a local fail-closed snapshot before the durable append completes.
/// </summary>
public interface IAsyncOperationalControl
{
    OperationalControlSnapshot Snapshot { get; }

    Task<OperationalControlResult> ActivateKillSwitchAsync(
        OperationalControlCommand command,
        CancellationToken cancellationToken = default);

    Task<OperationalControlResult> EnterSafeModeAsync(
        OperationalControlCommand command,
        CancellationToken cancellationToken = default);

    Task<OperationalControlResult> ResetAsync(
        OperationalControlCommand command,
        OperationalControlResetAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public sealed record OperationalControlAuditEvent(
    Guid EventId,
    OperationalMode From,
    OperationalMode To,
    string Actor,
    string Reason,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset OccurredAtUtc,
    string? ApprovalId)
{
    /// <summary>Versioned financial-control audit contract; do not change without a migration.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; } = CurrentSchemaVersion;

    public string EventType { get; } = "OPERATIONAL_CONTROL_TRANSITION";

    /// <summary>Canonical payload digest used to detect altered local evidence.</summary>
    public string PayloadSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|',
            EventType,
            SchemaVersion,
            EventId.ToString("D"),
            From,
            To,
            Actor,
            Reason,
            IdempotencyKey.ToString(),
            OccurredAtUtc.ToUniversalTime().ToString("O"),
            ApprovalId ?? string.Empty))));
}

/// <summary>Append-only boundary for critical operational-control audit events.</summary>
public interface IOperationalControlAuditSink
{
    void Append(OperationalControlAuditEvent auditEvent);
}

/// <summary>Async append-only boundary used by persistence-backed hosted services.</summary>
public interface IAsyncOperationalControlAuditSink
{
    Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic local sink used by Paper/NO_TRADE and contract tests.</summary>
public sealed class InMemoryOperationalControlAuditSink : IOperationalControlAuditSink, IAsyncOperationalControlAuditSink
{
    private readonly object gate = new();
    private readonly List<OperationalControlAuditEvent> events = [];

    public IReadOnlyList<OperationalControlAuditEvent> Events
    {
        get
        {
            lock (gate)
            {
                return events.ToArray();
            }
        }
    }

    public void Append(OperationalControlAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        lock (gate)
        {
            if (events.Any(existing => existing.EventId == auditEvent.EventId))
            {
                return;
            }

            events.Add(auditEvent);
        }
    }

    public Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(auditEvent);
        return Task.CompletedTask;
    }
}

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
#pragma warning disable CA1001 // The process-scoped control gate is owned by the host singleton.
public sealed class InMemoryOperationalControl : IOperationalControl, IAsyncOperationalControl
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IOperationalControlResetAuthorizer resetAuthorizer;
    private readonly IOperationalControlAuditSink? auditSink;
    private readonly IAsyncOperationalControlAuditSink? asyncAuditSink;
    private readonly Dictionary<IdempotencyKey, (OperationalControlCommand Command, OperationalControlResult Result)> commands = [];
    private OperationalControlSnapshot snapshot = OperationalControlSnapshot.Initial;

    public InMemoryOperationalControl(
        IOperationalControlResetAuthorizer resetAuthorizer,
        IOperationalControlAuditSink? auditSink = null,
        IAsyncOperationalControlAuditSink? asyncAuditSink = null)
    {
        ArgumentNullException.ThrowIfNull(resetAuthorizer);
        this.resetAuthorizer = resetAuthorizer;
        this.auditSink = auditSink ?? new InMemoryOperationalControlAuditSink();
        this.asyncAuditSink = asyncAuditSink ?? this.auditSink as IAsyncOperationalControlAuditSink;
    }

    public OperationalControlSnapshot Snapshot
    {
        get => Volatile.Read(ref snapshot);
    }

    public OperationalControlResult ActivateKillSwitch(OperationalControlCommand command) =>
        Apply(command, OperationalMode.KillSwitch, approvalId: null);

    public OperationalControlResult EnterSafeMode(OperationalControlCommand command) =>
        Apply(command, OperationalMode.SafeMode, approvalId: null);

    public OperationalControlResult Reset(OperationalControlCommand command, OperationalControlResetAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorization);

        gate.Wait();
        try
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
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationalControlResult> ActivateKillSwitchAsync(
        OperationalControlCommand command,
        CancellationToken cancellationToken = default) =>
        await ApplyAsync(command, OperationalMode.KillSwitch, approvalId: null, cancellationToken).ConfigureAwait(false);

    public async Task<OperationalControlResult> EnterSafeModeAsync(
        OperationalControlCommand command,
        CancellationToken cancellationToken = default) =>
        await ApplyAsync(command, OperationalMode.SafeMode, approvalId: null, cancellationToken).ConfigureAwait(false);

    public async Task<OperationalControlResult> ResetAsync(
        OperationalControlCommand command,
        OperationalControlResetAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorization);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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

            return await ApplyLockedAsync(command, OperationalMode.Normal, authorization.ApprovalId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private OperationalControlResult Apply(OperationalControlCommand command, OperationalMode requestedMode, string? approvalId)
    {
        ArgumentNullException.ThrowIfNull(command);

        gate.Wait();
        try
        {
            if (commands.TryGetValue(command.IdempotencyKey, out var previous))
            {
                EnsureReplayMatches(previous.Command, command);
                return previous.Result with { StateChanged = false };
            }

            return ApplyLocked(command, requestedMode, approvalId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OperationalControlResult> ApplyAsync(
        OperationalControlCommand command,
        OperationalMode requestedMode,
        string? approvalId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (commands.TryGetValue(command.IdempotencyKey, out var previous))
            {
                EnsureReplayMatches(previous.Command, command);
                return previous.Result with { StateChanged = false };
            }

            return await ApplyLockedAsync(command, requestedMode, approvalId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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
        var audit = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            previous.Mode,
            effectiveMode,
            command.Actor,
            command.Reason,
            command.IdempotencyKey,
            command.OccurredAtUtc,
            approvalId);
        // Restrictive transitions latch locally before persistence so that a
        // failed audit database cannot leave concurrent guards in Normal mode.
        var restrictiveTransition = requestedMode is OperationalMode.SafeMode or OperationalMode.KillSwitch;
        if (restrictiveTransition)
        {
            snapshot = next;
        }

        if (auditSink is null)
        {
            throw new InvalidOperationException("Synchronous operational-control transition is unavailable for an async-only audit sink.");
        }

        auditSink.Append(audit);
        snapshot = next;
        var result = new OperationalControlResult(next, audit, changed);
        commands.Add(command.IdempotencyKey, (command, result));
        return result;
    }

    private async Task<OperationalControlResult> ApplyLockedAsync(
        OperationalControlCommand command,
        OperationalMode requestedMode,
        string? approvalId,
        CancellationToken cancellationToken)
    {
        var previous = snapshot;
        var effectiveMode = requestedMode is OperationalMode.SafeMode && previous.Mode is OperationalMode.KillSwitch
            ? OperationalMode.KillSwitch
            : requestedMode;
        var changed = previous.Mode != effectiveMode;
        var next = changed
            ? new OperationalControlSnapshot(effectiveMode, previous.Revision + 1, command.OccurredAtUtc, command.Actor, command.Reason)
            : previous;
        var audit = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            previous.Mode,
            effectiveMode,
            command.Actor,
            command.Reason,
            command.IdempotencyKey,
            command.OccurredAtUtc,
            approvalId);
        // Never wait for durable audit I/O before blocking new risk. This local
        // latch survives provider failures; audit remains mandatory and the
        // failure still propagates to the caller.
        var restrictiveTransition = requestedMode is OperationalMode.SafeMode or OperationalMode.KillSwitch;
        if (restrictiveTransition)
        {
            snapshot = next;
        }

        if (asyncAuditSink is null)
        {
            throw new InvalidOperationException("Async operational-control transition requires an async audit sink.");
        }

        await asyncAuditSink.AppendAsync(audit, cancellationToken).ConfigureAwait(false);
        snapshot = next;
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
#pragma warning restore CA1001
