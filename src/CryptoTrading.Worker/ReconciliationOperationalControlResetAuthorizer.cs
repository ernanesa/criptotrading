using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Authorizes automatic operational-control reset for purely transient transport stream events,
/// transient autonomy cycle failures, and transient reconciliation failures once reconciliation
/// proves that balances and orders are completely healthy.
/// Protects against flapping via rolling 1-hour auto-recovery limit.
/// Strictly denies automatic reset for accounting execution failures, fee drifts, durable stops,
/// drift detections, and operator interventions.
/// </summary>
internal sealed class ReconciliationOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    private static readonly HashSet<string> AutoRecoverableStreamReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCOUNT_STREAM_EVENTSTREAMTERMINATED",
        "ACCOUNT_STREAM_SERVERSHUTDOWN",
        "ACCOUNT_STREAM_CONNECTIONLOST",
        "ACCOUNT_STREAM_DISCONNECTED",
        "ACCOUNT_STREAM_SOCKET_CLOSED",
        "ACCOUNT_STREAM_TIMEOUT",
        "ACCOUNT_STREAM_ENDED"
    };

    private static readonly string[] NonRecoverableReasonTokens =
    [
        "POSITION_BASIS_DRIFT",
        "FEE_DRIFT",
        "Fee rate exceeds policy maximum",
        "ACCOUNTING_EXECUTION_FAILURE",
        "ACCOUNT_STREAM_FAILURE",
        "DAILY_LOSS_LIMIT",
        "MAX_DRAWDOWN",
        "DISK_SPACE_EXHAUSTED",
        "KILL_SWITCH",
        "UNAUTHORIZED_RESET_ATTEMPT",
        "INVALID_ORDER_LIFECYCLE_TRANSITION",
        "AUTHENTICATION_SECURITY_VIOLATION",
        "SECURITY_WITHDRAWALS_OR_KILL_SWITCH",
        "DATA_OR_STATE_DRIFT",
        "Account reconciliation drift detected",
        "periodic-reconciliation-mismatch",
        "RESTORED_DURABLE_STOP"
    ];

    private readonly object gate = new();
    private readonly List<DateTimeOffset> recentResetTimestamps = [];
    private readonly int maxResetsPerHour;
    private static readonly TimeSpan RollingWindow = TimeSpan.FromHours(1);

    public ReconciliationOperationalControlResetAuthorizer(int maxResetsPerHour = 3)
    {
        this.maxResetsPerHour = Math.Max(1, maxResetsPerHour);
    }

    internal static bool IsAutoRecoverable(OperationalControlSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Mode != OperationalMode.SafeMode)
        {
            return false;
        }

        if (string.Equals(current.ChangedBy, "operator", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current.Reason))
        {
            return false;
        }

        // Strict fail-closed check for any non-recoverable accounting, risk, or durable administrative token
        foreach (var token in NonRecoverableReasonTokens)
        {
            if (current.Reason.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 1. Account stream transient disconnects
        if (string.Equals(current.ChangedBy, "account-stream", StringComparison.OrdinalIgnoreCase))
        {
            return AutoRecoverableStreamReasons.Contains(current.Reason);
        }

        // 2. Periodic reconciliation tick transient failure or purely stale snapshot
        if (string.Equals(current.ChangedBy, "account-reconciliation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(current.ChangedBy, "periodic-reconciliation", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(current.Reason, "PERIODIC_RECONCILIATION_FAILURE", StringComparison.OrdinalIgnoreCase) ||
                current.Reason.StartsWith("Account reconciliation stale snapshot", StringComparison.OrdinalIgnoreCase);
        }

        // 3. Autonomy cycle transient failures
        if (string.Equals(current.ChangedBy, "autonomy-cycle", StringComparison.OrdinalIgnoreCase))
        {
            if (current.Reason.Contains("severity=Transient", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Reason.Contains("Binance kline quality gate rejected the REST snapshot", StringComparison.OrdinalIgnoreCase) ||
                current.Reason.Contains("Binance REST kline snapshot has a gap after the durable autonomy checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsCircuitBreakerTripped(DateTimeOffset now)
    {
        lock (gate)
        {
            recentResetTimestamps.RemoveAll(ts => now - ts > RollingWindow);
            return recentResetTimestamps.Count >= maxResetsPerHour;
        }
    }

    public bool IsAuthorized(
        OperationalControlSnapshot current,
        OperationalControlCommand resetCommand,
        OperationalControlResetAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(resetCommand);
        ArgumentNullException.ThrowIfNull(authorization);

        if (current.Mode != OperationalMode.SafeMode)
        {
            return false;
        }

        if (!string.Equals(authorization.Actor, "account-reconciliation", StringComparison.Ordinal) ||
            !string.Equals(resetCommand.Actor, "account-reconciliation", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(authorization.ApprovalId) ||
            !authorization.ApprovalId.StartsWith("reconciliation-auto-recovery:", StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsAutoRecoverable(current))
        {
            return false;
        }

        lock (gate)
        {
            var now = resetCommand.OccurredAtUtc;
            recentResetTimestamps.RemoveAll(ts => now - ts > RollingWindow);
            if (recentResetTimestamps.Count >= maxResetsPerHour)
            {
                return false;
            }

            recentResetTimestamps.Add(now);
            return true;
        }
    }
}
