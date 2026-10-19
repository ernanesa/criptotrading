using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ReconciliationOperationalControlResetAuthorizerTests
{
    private readonly ReconciliationOperationalControlResetAuthorizer _authorizer = new();

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByReconciliationAndResetByReconciliationReturnsTrue()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "account-reconciliation",
            Reason: "Account reconciliation drift detected.");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.True(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeRestoredByWorkerBootstrapReturnsTrue()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "worker-bootstrap",
            Reason: "RESTORED_DURABLE_STOP:abc123");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.True(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByPeriodicReconciliationReturnsTrue()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "periodic-reconciliation",
            Reason: "PERIODIC_RECONCILIATION_FAILURE");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.True(result);
    }

    [Theory]
    [InlineData("A2 failure: HTTP_TRANSIENT (severity=Transient, consecutive=3)")]
    [InlineData("A2 failure: SOCKET_CLOSED (severity=Transient, consecutive=4)")]
    [InlineData("A2 failure: NETWORK_TIMEOUT_TRANSIENT (severity=Transient, consecutive=2)")]
    public void IsAuthorizedWhenSafeModeEnteredByAutonomyTransientNetworkFailureReturnsTrue(string reason)
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "autonomy-cycle",
            Reason: reason);

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.True(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByAutonomyA2ReturnsFalse()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "autonomy-cycle",
            Reason: "A2 failure");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorizedWhenKillSwitchActiveReturnsFalse()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.KillSwitch,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "account-reconciliation",
            Reason: "KillSwitch");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorizedWhenApprovalIdDoesNotMatchPrefixReturnsFalse()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "account-reconciliation",
            Reason: "Account reconciliation drift detected.");

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "manual-override:approval-999");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.False(result);
    }

    [Theory]
    [InlineData("ACCOUNT_STREAM_EVENTSTREAMTERMINATED")]
    [InlineData("ACCOUNT_STREAM_SERVERSHUTDOWN")]
    [InlineData("ACCOUNT_STREAM_CONNECTIONLOST")]
    [InlineData("ACCOUNT_STREAM_DISCONNECTED")]
    [InlineData("ACCOUNT_STREAM_SOCKET_CLOSED")]
    [InlineData("ACCOUNT_STREAM_TIMEOUT")]
    [InlineData("ACCOUNT_STREAM_ENDED")]
    [InlineData("ACCOUNT_STREAM_FAILURE")]
    public void IsAuthorizedWhenSafeModeEnteredByAccountStreamTransientTerminationReturnsTrue(string reason)
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "account-stream",
            Reason: reason);

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.True(result);
    }

    [Theory]
    [InlineData("ACCOUNTING_EXECUTION_FAILURE")]
    [InlineData("FEE_DRIFT_EXCEEDED")]
    [InlineData("Fee rate exceeds policy maximum")]
    public void IsAuthorizedWhenSafeModeEnteredByAccountStreamNonRecoverableReasonReturnsFalse(string reason)
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "account-stream",
            Reason: reason);

        var command = new OperationalControlCommand(
            "account-reconciliation",
            "Reconciliation auto-recovery: balance and orders healthy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow);

        var authorization = new OperationalControlResetAuthorization(
            "account-reconciliation",
            "reconciliation-auto-recovery:test-run-123");

        var result = _authorizer.IsAuthorized(current, command, authorization);

        Assert.False(result);
    }
}
