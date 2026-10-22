using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Fail-closed operational control reset authorizer.
/// Allows automated recovery to Normal ONLY when the current SafeMode was triggered by account-reconciliation,
/// worker-bootstrap, periodic-reconciliation, account-stream transient websocket disconnections, or autonomy-cycle transient network failures,
/// and the reset is requested by account-reconciliation with an explicit auto-recovery approval ID.
/// All other resets (KillSwitch, operator SafeMode, disk space, accounting execution failure, fee drift) are strictly denied.
/// </summary>
internal sealed class ReconciliationOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    private const string ReconciliationActor = "account-reconciliation";
    private const string AutoRecoveryApprovalPrefix = "reconciliation-auto-recovery:";

    internal static bool IsAutoRecoverable(OperationalControlSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Mode != OperationalMode.SafeMode)
        {
            return false;
        }

        // Operator manual safe mode is strictly denied auto-recovery.
        if (string.Equals(current.ChangedBy, "operator", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var reason = current.Reason ?? string.Empty;

        // Fail-closed invariant: Accounting failures, position drift, fee drift, risk limit breaches,
        // and disk space exhaustion require explicit operator review and cannot be auto-recovered.
        if (reason.Contains("ACCOUNTING_EXECUTION_FAILURE", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("POSITION_BASIS_DRIFT", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("fee drift", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("FEE_DRIFT", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("withdrawals enabled", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("KILL_SWITCH", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("DISK_SPACE", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("DAILY_LOSS", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("DRAWDOWN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 1. SafeMode entered by reconciliation coordinator, worker bootstrap, or periodic reconciliation:
        if (string.Equals(current.ChangedBy, ReconciliationActor, StringComparison.Ordinal) ||
            string.Equals(current.ChangedBy, "worker-bootstrap", StringComparison.Ordinal) ||
            string.Equals(current.ChangedBy, "periodic-reconciliation", StringComparison.Ordinal))
        {
            return true;
        }

        // 2. SafeMode entered by account-stream due to transient WebSocket disconnection / stream termination:
        if (string.Equals(current.ChangedBy, "account-stream", StringComparison.Ordinal))
        {
            return reason.Contains("EVENTSTREAMTERMINATED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("SERVERSHUTDOWN", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("CONNECTIONLOST", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("DISCONNECTED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("SOCKET_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_ENDED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("ACCOUNT_STREAM_FAILURE", StringComparison.OrdinalIgnoreCase);
        }

        // 3. SafeMode entered by autonomy-cycle due to transient network, market data, rate limit, or cycle failures:
        if (string.Equals(current.ChangedBy, "autonomy-cycle", StringComparison.Ordinal))
        {
            // Fail closed on bare unspecified "A2 failure" string
            if (string.Equals(reason.Trim(), "A2 failure", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return reason.Contains("HTTP_TRANSIENT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("SOCKET_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("OPERATION_CANCELED", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("DB_TRANSIENT", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("quality gate", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("kline", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("gap", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("Stale", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("UNKNOWN_SYSTEM_FAILURE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public bool IsAuthorized(
        OperationalControlSnapshot current,
        OperationalControlCommand resetCommand,
        OperationalControlResetAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(resetCommand);
        ArgumentNullException.ThrowIfNull(authorization);

        // Fail-closed invariants:
        // 1. Never reset KillSwitch.
        // 2. Only reset if current mode is SafeMode.
        // 3. Only reset if the current SafeMode was entered by account-reconciliation, worker-bootstrap,
        //    periodic-reconciliation, account-stream transient disconnections, or autonomy-cycle transient network failures.
        // 4. Only reset if the reset command and authorization are issued by account-reconciliation.
        // 5. Must have explicit reconciliation-auto-recovery approval identifier.
        return IsAutoRecoverable(current) &&
               string.Equals(resetCommand.Actor, ReconciliationActor, StringComparison.Ordinal) &&
               string.Equals(authorization.Actor, ReconciliationActor, StringComparison.Ordinal) &&
               authorization.ApprovalId.StartsWith(AutoRecoveryApprovalPrefix, StringComparison.Ordinal);
    }
}
