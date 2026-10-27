using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

/// <summary>
/// Denies automatic operational-control reset until all active incidents can be
/// durably reconstructed and resolved. A healthy reconciliation alone cannot
/// establish that an accounting incident has been resolved.
/// </summary>
internal sealed class ReconciliationOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    internal static bool IsAutoRecoverable(OperationalControlSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
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
        return false;
    }
}
