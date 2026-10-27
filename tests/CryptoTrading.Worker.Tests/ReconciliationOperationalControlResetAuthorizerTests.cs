using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ReconciliationOperationalControlResetAuthorizerTests
{
    private readonly ReconciliationOperationalControlResetAuthorizer _authorizer = new();

    [Fact]
    public void AccountingFailureDuringTransientSafeModeCannotBeAutomaticallyCleared()
    {
        var control = new InMemoryOperationalControl(_authorizer);
        var now = DateTimeOffset.UtcNow;
        control.EnterSafeMode(new OperationalControlCommand("account-stream", "ACCOUNT_STREAM_FAILURE", IdempotencyKey.New(), now));
        control.EnterSafeMode(new OperationalControlCommand("account-stream", "ACCOUNTING_EXECUTION_FAILURE", IdempotencyKey.New(), now));
        var reset = new OperationalControlCommand("account-reconciliation", "Reconciliation healthy", IdempotencyKey.New(), now);
        var authorization = new OperationalControlResetAuthorization("account-reconciliation", "reconciliation-auto-recovery:test");

        Assert.Throws<OperationalControlResetDeniedException>(() => control.Reset(reset, authorization));
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public void RestoredDurableStopCannotBeAutomaticallyCleared()
    {
        var control = new InMemoryOperationalControl(_authorizer);
        var now = DateTimeOffset.UtcNow;
        control.EnterSafeMode(new OperationalControlCommand("worker-bootstrap", "RESTORED_DURABLE_STOP:abc", IdempotencyKey.New(), now));
        var reset = new OperationalControlCommand("account-reconciliation", "Reconciliation healthy", IdempotencyKey.New(), now);
        var authorization = new OperationalControlResetAuthorization("account-reconciliation", "reconciliation-auto-recovery:test");

        Assert.Throws<OperationalControlResetDeniedException>(() => control.Reset(reset, authorization));
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task AccountingFailureCannotClearOrDowngradeKillSwitchAsync()
    {
        var control = new InMemoryOperationalControl(_authorizer);
        var now = DateTimeOffset.UtcNow;
        await control.ActivateKillSwitchAsync(new OperationalControlCommand("operator", "stop", IdempotencyKey.New(), now), TestContext.Current.CancellationToken);
        await control.EnterSafeModeAsync(new OperationalControlCommand("account-stream", "ACCOUNTING_EXECUTION_FAILURE", IdempotencyKey.New(), now), TestContext.Current.CancellationToken);
        var reset = new OperationalControlCommand("account-reconciliation", "Reconciliation healthy", IdempotencyKey.New(), now);
        var authorization = new OperationalControlResetAuthorization("account-reconciliation", "reconciliation-auto-recovery:test");

        await Assert.ThrowsAsync<OperationalControlResetDeniedException>(() => control.ResetAsync(reset, authorization, TestContext.Current.CancellationToken));
        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByReconciliationReturnsFalse()
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

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeRestoredByWorkerBootstrapReturnsFalse()
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

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByPeriodicReconciliationReturnsFalse()
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

        Assert.False(result);
    }

    [Theory]
    [InlineData("A2 failure: HTTP_TRANSIENT (severity=Transient, consecutive=3)")]
    [InlineData("A2 failure: SOCKET_CLOSED (severity=Transient, consecutive=4)")]
    [InlineData("A2 failure: NETWORK_TIMEOUT_TRANSIENT (severity=Transient, consecutive=2)")]
    [InlineData("A2 failure: UNKNOWN_SYSTEM_FAILURE (severity=Unknown, consecutive=0)")]
    [InlineData("A2 failure: RATE_LIMIT_EXCEEDED (severity=Transient, consecutive=1)")]
    [InlineData("A2 failure: OPERATION_CANCELED (severity=Transient, consecutive=1)")]
    [InlineData("A2 failure: DB_TRANSIENT_SQLSTATE_40001 (severity=Transient, consecutive=1)")]
    [InlineData("Binance kline quality gate rejected the REST snapshot: OutOfOrder.")]
    [InlineData("Binance REST kline snapshot has a gap after the durable autonomy checkpoint.")]
    [InlineData("A2 failure: Stale market data snapshot (severity=Transient, consecutive=1)")]
    public void IsAuthorizedWhenSafeModeEnteredByAutonomyTransientNetworkFailureReturnsFalse(string reason)
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

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorizedWhenSafeModeEnteredByOperatorReturnsFalse()
    {
        var current = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            Revision: 1,
            ChangedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
            ChangedBy: "operator",
            Reason: "Operator manual maintenance");

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

    [Theory]
    [InlineData("DAILY_LOSS_LIMIT_EXCEEDED")]
    [InlineData("MAX_DRAWDOWN_LIMIT_EXCEEDED")]
    [InlineData("POSITION_BASIS_DRIFT")]
    [InlineData("DISK_SPACE_EXHAUSTED")]
    public void IsAuthorizedWhenSafeModeEnteredByCriticalRiskOrDriftReturnsFalse(string reason)
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

        Assert.False(result);
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
    public void IsAuthorizedWhenSafeModeEnteredByAccountStreamTransientTerminationReturnsFalse(string reason)
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
