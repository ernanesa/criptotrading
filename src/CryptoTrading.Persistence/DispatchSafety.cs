namespace CryptoTrading.Persistence;

/// <summary>Durable, at-most-once permission to start a venue submission for an outbox intent.</summary>
public interface IDurableSubmissionStore
{
    Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default);
    Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message, TimeSpan minimumLeaseRemaining, CancellationToken cancellationToken = default);
    Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default);
    Task<bool> ValidateOutboxLeaseAsync(
        LeasedOutboxMessage message,
        TimeSpan minimumLeaseRemaining,
        CancellationToken cancellationToken = default);
}
