using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Authorizes automatic operational-control reset for purely transient transport stream events
/// once reconciliation proves that balances and orders are completely healthy.
/// Denies automatic reset for accounting execution failures, fee drifts, durable stops,
/// drift detections, and operator interventions.
/// </summary>
internal sealed class ReconciliationOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    private static readonly HashSet<string> AutoRecoverableReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCOUNT_STREAM_EVENTSTREAMTERMINATED",
        "ACCOUNT_STREAM_SERVERSHUTDOWN",
        "ACCOUNT_STREAM_CONNECTIONLOST",
        "ACCOUNT_STREAM_DISCONNECTED",
        "ACCOUNT_STREAM_SOCKET_CLOSED",
        "ACCOUNT_STREAM_TIMEOUT",
        "ACCOUNT_STREAM_ENDED"
    };

    internal static bool IsAutoRecoverable(OperationalControlSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Mode != OperationalMode.SafeMode)
        {
            return false;
        }

        if (!string.Equals(current.ChangedBy, "account-stream", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current.Reason))
        {
            return false;
        }

        return AutoRecoverableReasons.Contains(current.Reason);
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

        return IsAutoRecoverable(current);
    }
}
