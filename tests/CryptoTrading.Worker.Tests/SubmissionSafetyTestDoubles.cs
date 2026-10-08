using CryptoTrading.Domain.Execution;
using CryptoTrading.Persistence;

namespace CryptoTrading.Worker.Tests;

internal sealed class TestSubmissionGuard : IExecutionSubmissionGuard
{
    public bool Reject { get; set; }
    public bool RejectFinal { get; set; }

    public Task ValidateAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (Reject) throw new ExecutionDispatchBlockedException("test-preflight-rejected");
        if (command.ExecutionExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ExecutionDispatchBlockedException("EXECUTION_EXPIRED_OR_INVALID_TIME");
        }
        return Task.CompletedTask;
    }

    public Task ValidateFinalAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (RejectFinal) throw new ExecutionDispatchBlockedException("test-final-check-rejected");
        return Task.CompletedTask;
    }
}

internal sealed class TestSubmissionStore : IDurableSubmissionStore
{
    private readonly HashSet<Guid> started = [];
    public bool LeaseValid { get; set; } = true;
    public TimeSpan MinimumLeaseRemaining { get; private set; }
    public bool HasStarted(Guid messageId) => started.Contains(messageId);
    public Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(started.Contains(message.MessageId));
    public Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message, TimeSpan minimumLeaseRemaining, CancellationToken cancellationToken = default)
    {
        MinimumLeaseRemaining = minimumLeaseRemaining;
        return Task.FromResult(LeaseValid && started.Add(message.MessageId));
    }
    public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(LeaseValid);

    public Task<bool> ValidateOutboxLeaseAsync(
        LeasedOutboxMessage message,
        TimeSpan minimumLeaseRemaining,
        CancellationToken cancellationToken = default) => Task.FromResult(LeaseValid);
}
