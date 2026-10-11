using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

[Trait("Category", "OperationalControl")]
public sealed class OperationalControlTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);
    private static readonly RiskPolicy Policy = new("risk-v1", 100m, 250m, 25m, 50m, TimeSpan.FromSeconds(5));

    [Fact]
    public void KillSwitchIsIdempotentAndProducesAnAuditEvent()
    {
        var control = NewControl();
        var command = Command("kill-1", "operator-1", "manual emergency stop");

        var first = control.ActivateKillSwitch(command);
        var replay = control.ActivateKillSwitch(command);

        Assert.True(first.StateChanged);
        Assert.False(replay.StateChanged);
        Assert.Equal(first.AuditEvent.EventId, replay.AuditEvent.EventId);
        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
        Assert.Equal(OperationalMode.Normal, first.AuditEvent.From);
        Assert.Equal(OperationalMode.KillSwitch, first.AuditEvent.To);
        Assert.Equal(command.IdempotencyKey, first.AuditEvent.IdempotencyKey);
    }

    [Fact]
    public void KillSwitchPersistsVersionedHashedAuditAndLatchesSafety()
    {
        var audit = new InMemoryOperationalControlAuditSink();
        var control = new InMemoryOperationalControl(new TestResetAuthorizer { Allowed = true }, audit);

        var result = control.ActivateKillSwitch(Command("persisted-kill", "operator-1", "manual emergency stop"));

        var persisted = Assert.Single(audit.Events);
        Assert.Equal(result.AuditEvent.EventId, persisted.EventId);
        Assert.Equal(OperationalControlAuditEvent.CurrentSchemaVersion, persisted.SchemaVersion);
        Assert.Equal("OPERATIONAL_CONTROL_TRANSITION", persisted.EventType);
        Assert.Matches("^[0-9A-F]{64}$", persisted.PayloadSha256);
        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
    }

    [Fact]
    public void AuditFailureLatchesKillSwitchBeforePropagatingFailure()
    {
        var control = new InMemoryOperationalControl(
            new TestResetAuthorizer { Allowed = true },
            new ThrowingAuditSink());

        Assert.Throws<InvalidOperationException>(() =>
            control.ActivateKillSwitch(Command("audit-failure", "operator-1", "manual emergency stop")));

        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
    }

    [Fact]
    public async Task AsyncAuditFailureLatchesSafeModeBeforePropagatingFailure()
    {
        var control = new InMemoryOperationalControl(
            new TestResetAuthorizer { Allowed = true },
            auditSink: null,
            asyncAuditSink: new ThrowingAsyncAuditSink());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.EnterSafeModeAsync(
                Command("async-audit-failure", "operator-1", "market data unavailable"),
                TestContext.Current.CancellationToken));

        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
    }

    [Fact]
    public async Task AsyncSafeModeTransitionLatchesStateBeforeAuditCompletes()
    {
        var audit = new BlockingAsyncAuditSink();
        var control = new InMemoryOperationalControl(
            new TestResetAuthorizer { Allowed = true },
            auditSink: null,
            asyncAuditSink: audit);
        var transition = control.EnterSafeModeAsync(
            Command("async-audit-order", "operator-1", "audit first"),
            TestContext.Current.CancellationToken);

        await audit.Started.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.False(transition.IsCompleted);

        audit.Release();
        var result = await transition.ConfigureAwait(true);

        Assert.True(result.StateChanged);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Equal(1, audit.Appends);
    }

    [Fact]
    public void ReusingAControlKeyForDifferentCommandFailsClosed()
    {
        var control = NewControl();
        control.ActivateKillSwitch(Command("same-key", "operator-1", "first reason"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            control.EnterSafeMode(Command("same-key", "operator-1", "different reason")));

        Assert.Contains("reused", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
    }

    [Fact]
    public void SafeModeCannotDowngradeAnActiveKillSwitch()
    {
        var control = NewControl();
        control.ActivateKillSwitch(Command("kill", "operator", "stop"));

        var result = control.EnterSafeMode(Command("safe", "supervisor", "defensive fallback"));

        Assert.False(result.StateChanged);
        Assert.Equal(OperationalMode.KillSwitch, result.Snapshot.Mode);
        Assert.Equal(OperationalMode.KillSwitch, result.AuditEvent.From);
        Assert.Equal(OperationalMode.KillSwitch, result.AuditEvent.To);
        Assert.Equal(1, control.Snapshot.Revision);
    }

    [Fact]
    public void UnauthorizedResetDoesNotChangeStateOrCreateAnAuditEvent()
    {
        var authorizer = new TestResetAuthorizer { Allowed = false };
        var control = new InMemoryOperationalControl(authorizer);
        control.EnterSafeMode(Command("safe", "system", "stale market data"));

        Assert.Throws<OperationalControlResetDeniedException>(() =>
            control.Reset(Command("reset", "operator", "request recovery"), new("operator", "approval-1")));

        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
        Assert.Equal(1, authorizer.Calls);
    }

    [Fact]
    public void AuthorizedResetReturnsToNormalAndCarriesApprovalInAudit()
    {
        var control = NewControl();
        control.EnterSafeMode(Command("safe", "system", "reconciliation required"));

        var result = control.Reset(
            Command("reset", "operator", "reconciliation completed"),
            new("operator", "approval-42"));

        Assert.True(result.StateChanged);
        Assert.Equal(OperationalMode.Normal, result.Snapshot.Mode);
        Assert.Equal(2, result.Snapshot.Revision);
        Assert.Equal("approval-42", result.AuditEvent.ApprovalId);
        Assert.Equal(OperationalMode.SafeMode, result.AuditEvent.From);
        Assert.Equal(OperationalMode.Normal, result.AuditEvent.To);
    }

    [Fact]
    public async Task ConcurrentReplayOfSameKillSwitchCommandHasOneStateTransition()
    {
        var control = NewControl();
        var command = Command("concurrent-kill", "operator", "incident drill");

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => control.ActivateKillSwitch(command))));

        Assert.All(results, result => Assert.Equal(results[0].AuditEvent.EventId, result.AuditEvent.EventId));
        Assert.Equal(OperationalMode.KillSwitch, control.Snapshot.Mode);
        Assert.Equal(1, control.Snapshot.Revision);
        Assert.True(results.Count(result => result.StateChanged) == 1);
    }

    [Fact]
    public void SafeModeBlocksNewEntriesButAllowsAReductionThroughThePipeline()
    {
        var control = NewControl();
        control.EnterSafeMode(Command("safe", "system", "trust gate failed"));

        var entry = OrderExecutionPipeline.Evaluate(NewIntent(80m, currentExposure: 0m), Policy, control);
        var reduction = OrderExecutionPipeline.Evaluate(NewIntent(10m, currentExposure: 20m), Policy, control);

        Assert.Equal(RiskDecisionStatus.Rejected, entry.Decision.Status);
        Assert.Contains(RiskReasonCodes.SafeMode, entry.Decision.ReasonCodes);
        Assert.False(entry.IsDispatchable);
        Assert.Equal(RiskDecisionStatus.Adjusted, reduction.Decision.Status);
        Assert.Contains(RiskReasonCodes.SafeMode, reduction.Decision.ReasonCodes);
        Assert.True(reduction.IsDispatchable);
    }

    private static InMemoryOperationalControl NewControl() => new(new TestResetAuthorizer { Allowed = true });

    private static OperationalControlCommand Command(string key, string actor, string reason) =>
        new(actor, reason, new IdempotencyKey(key), Now);

    private static OrderIntent NewIntent(decimal requestedNotional, decimal currentExposure)
    {
        var evaluation = new RiskEvaluationRequest(
            requestedNotional,
            currentExposure,
            0m,
            0m,
            Now.AddSeconds(-1),
            Now);

        return new(
            OrderId.Parse("11111111111111111111111111111111"),
            new ClientOrderId($"ct-control-{requestedNotional}"),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(requestedNotional / 100m),
            new Price(100m),
            CorrelationId.Parse("22222222222222222222222222222222"),
            new IdempotencyKey($"pipeline-control-{requestedNotional}"),
            evaluation,
            Now);
    }

    private sealed class TestResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool Allowed { get; init; }

        public int Calls { get; private set; }

        public bool IsAuthorized(
            OperationalControlSnapshot current,
            OperationalControlCommand resetCommand,
            OperationalControlResetAuthorization authorization)
        {
            Calls++;
            return Allowed && current.BlocksNewEntries && resetCommand.Actor == authorization.Actor;
        }
    }

    private sealed class ThrowingAuditSink : IOperationalControlAuditSink
    {
        public void Append(OperationalControlAuditEvent auditEvent) =>
            throw new InvalidOperationException("audit sink unavailable");
    }

    private sealed class ThrowingAsyncAuditSink : IAsyncOperationalControlAuditSink
    {
        public Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("async audit sink unavailable"));
    }

    private sealed class BlockingAsyncAuditSink : IAsyncOperationalControlAuditSink
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Appends { get; private set; }

        public async Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Appends++;
        }

        public void Release() => release.SetResult();
    }
}
