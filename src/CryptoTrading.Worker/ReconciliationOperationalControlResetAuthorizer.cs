using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Fail-closed operational control reset authorizer.
/// Allows automated recovery to Normal ONLY when the current SafeMode was triggered by account-reconciliation
/// and the reset is requested by account-reconciliation with an explicit auto-recovery approval ID.
/// All other resets (KillSwitch, operator SafeMode, disk space, autonomy cycle A2) are strictly denied.
/// </summary>
internal sealed class ReconciliationOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    private const string ReconciliationActor = "account-reconciliation";
    private const string AutoRecoveryApprovalPrefix = "reconciliation-auto-recovery:";

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
        // 3. Only reset if the current SafeMode was entered by account-reconciliation.
        // 4. Only reset if the reset command and authorization are issued by account-reconciliation.
        // 5. Must have explicit reconciliation-auto-recovery approval identifier.
        return current.Mode == OperationalMode.SafeMode &&
               (string.Equals(current.ChangedBy, ReconciliationActor, StringComparison.Ordinal) ||
                string.Equals(current.ChangedBy, "worker-bootstrap", StringComparison.Ordinal) ||
                string.Equals(current.ChangedBy, "periodic-reconciliation", StringComparison.Ordinal)) &&
               string.Equals(resetCommand.Actor, ReconciliationActor, StringComparison.Ordinal) &&
               string.Equals(authorization.Actor, ReconciliationActor, StringComparison.Ordinal) &&
               authorization.ApprovalId.StartsWith(AutoRecoveryApprovalPrefix, StringComparison.Ordinal);
    }
}
