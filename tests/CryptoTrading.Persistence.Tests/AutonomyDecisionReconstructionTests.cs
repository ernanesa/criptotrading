using System.Globalization;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class AutonomyDecisionReconstructionTests
{
    [Fact]
    public void IdentityVerificationDetectsTamperedDecisionId()
    {
        var close = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);
        var validId = AutonomyDecisionReconstructor.ComputeCanonicalDecisionId("PAPER", "exchange-wallet", "BTCUSDT", "trend", "1.0.0", close);

        var validCommit = CreateCommit(validId, close);
        Assert.True(AutonomyDecisionReconstructor.VerifyCommitIdentity(validCommit));

        var tamperedCommit = CreateCommit("ad-0000000000000000000000000000000000000000000000000000000000000000", close);
        Assert.False(AutonomyDecisionReconstructor.VerifyCommitIdentity(tamperedCommit));
    }

    [Fact]
    public async Task ReconstructsNoTradeDecisionPurelyWithoutSideEffects()
    {
        var close = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);
        var decisionId = AutonomyDecisionReconstructor.ComputeCanonicalDecisionId("PAPER", "exchange-wallet", "BTCUSDT", "trend", "1.0.0", close);
        var commit = CreateCommit(decisionId, close);

        var commitStore = new FakeDecisionStore([commit]);
        var report = await AutonomyDecisionReconstructor.ReconstructAsync(decisionId, commitStore);

        Assert.NotNull(report);
        Assert.True(report.CanonicalIdentityHashMatches);
        Assert.True(report.IsAuditVerified);
        Assert.Equal("NO_TRADE", report.Commit.Outcome);
        Assert.Null(report.IntentClientOrderId);
        Assert.Null(report.SubmissionContext);
        Assert.Null(report.ExpectedFee);
        Assert.Empty(report.LifecycleEvents!);
    }

    [Fact]
    public async Task ReconstructsDispatchableDecisionWithFullAuditChain()
    {
        var close = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);
        var decisionId = AutonomyDecisionReconstructor.ComputeCanonicalDecisionId("TESTNET", "exchange-wallet", "BTCUSDT", "trend", "1.0.0", close);
        var clientOrderId = "ct-1234567890abcdef";

        var command = new SubmitOrderCommand(
            OrderId.New(),
            new ClientOrderId(clientOrderId),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Limit,
            new Quantity(0.01m),
            new Price(60000m),
            600m,
            CorrelationId.New(),
            new IdempotencyKey("idem-1"),
            "v1",
            close,
            ExecutionAttempt: 1,
            ExecutionExpiresAtUtc: close.AddMinutes(1));

        var outbox = new OutboxRecord(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            command.CorrelationId,
            command.IdempotencyKey,
            "v1",
            command,
            OutboxRecordStatus.Pending,
            close);

        var risk = new RiskDecision(
            RiskDecisionStatus.Approved,
            600m,
            600m,
            "v1",
            []);

        var traceJson = JsonSerializer.Serialize(new
        {
            IntentClientOrderId = clientOrderId,
            OutboxMessageId = outbox.MessageId
        });

        var commit = new AutonomyDecisionCommit(
            decisionId,
            "TESTNET",
            "exchange-wallet",
            "BTCUSDT:1m:1.0.0",
            new Symbol("BTCUSDT"),
            "trend",
            "1.0.0",
            close,
            close,
            "1.0",
            "cfg-fp",
            "feat-fp",
            "EnterLong",
            "FAST_MA_ABOVE_SLOW_MA",
            risk,
            "DISPATCHABLE",
            null,
            traceJson,
            outbox);

        var fee = new ExecutionFeeEstimate(
            0.001m,
            "snap-hash",
            "binance:commission",
            close,
            close.AddMinutes(5),
            referencePrice: 60000m,
            authorizedQuantity: 0.01m,
            decisionAtUtc: close);

        var submission = new ExecutionSubmissionContext(
            clientOrderId,
            command.OrderId,
            command.CorrelationId,
            outbox.MessageId,
            command.Symbol,
            command.Side,
            command.Type,
            command.Quantity,
            command.ReferencePrice,
            600m,
            1,
            close,
            close.AddMinutes(1));

        var lifecycleEvent = OrderLifecycleEvent.Create(
            clientOrderId,
            new OrderTransition(OrderStatus.Created, OrderStatus.PartiallyFilled, new IdempotencyKey("trans-1"), close.AddSeconds(5), "ex-999"));

        var commitStore = new FakeDecisionStore([commit]);
        var feeStore = new FakeFeeStore(fee);
        var subStore = new FakeSubmissionStore(submission);
        var lifeStore = new FakeLifecycleStore([lifecycleEvent]);

        var report = await AutonomyDecisionReconstructor.ReconstructAsync(
            decisionId,
            commitStore,
            feeStore,
            subStore,
            lifeStore);

        Assert.NotNull(report);
        Assert.True(report.CanonicalIdentityHashMatches);
        Assert.True(report.IsAuditVerified);
        Assert.True(report.HasCompleteDispatchAudit);
        Assert.Equal(clientOrderId, report.IntentClientOrderId);
        Assert.NotNull(report.SubmissionContext);
        Assert.Equal(600m, report.SubmissionContext.AuthorizedNotional);
        Assert.NotNull(report.ExpectedFee);
        Assert.Equal(0.001m, report.ExpectedFee.Rate);
        Assert.NotNull(report.LifecycleEvents);
        Assert.Single(report.LifecycleEvents);
        Assert.Equal(OrderStatus.PartiallyFilled, report.LifecycleEvents[0].Transition.To);
    }

    private static AutonomyDecisionCommit CreateCommit(string decisionId, DateTimeOffset close) =>
        new(
            decisionId,
            "PAPER",
            "exchange-wallet",
            "BTCUSDT:1m:1.0.0",
            new Symbol("BTCUSDT"),
            "trend",
            "1.0.0",
            close,
            close,
            "1.0",
            "cfg-fp",
            "feat-fp",
            "NoTrade",
            "BELOW_THRESHOLD",
            null,
            "NO_TRADE",
            "BELOW_THRESHOLD",
            "{}",
            null);

    private sealed class FakeDecisionStore(IReadOnlyList<AutonomyDecisionCommit> commits) : IAutonomyDecisionCommitStore
    {
        public Task<DateTimeOffset?> GetLastProcessedCloseAsync(string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task CommitAsync(AutonomyDecisionCommit commit, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AutonomyDecisionCommit?> ReadDecisionCommitAsync(string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(commits.FirstOrDefault(c => string.Equals(c.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeFeeStore(ExecutionFeeEstimate fee) : IExpectedExecutionFeeStore
    {
        public Task RecordExpectedExecutionFeeAsync(SubmitOrderCommand command, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ExecutionFeeEstimate?> ReadExpectedExecutionFeeAsync(string clientOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExecutionFeeEstimate?>(fee);
    }

    private sealed class FakeSubmissionStore(ExecutionSubmissionContext context) : IExecutionSubmissionContextStore
    {
        public Task<ExecutionSubmissionContext?> ReadExecutionSubmissionContextAsync(string clientOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExecutionSubmissionContext?>(context);
    }

    private sealed class FakeLifecycleStore(IReadOnlyList<OrderLifecycleEvent> events) : IOrderLifecycleStore
    {
        public Task<bool> AppendOrderLifecycleEventAsync(OrderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<OrderLifecycleEvent>> ReadOrderLifecycleEventsAsync(string clientOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(events);
    }
}
