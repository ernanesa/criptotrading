#pragma warning disable CA2007 // Async provider disposal follows the existing store convention.
#pragma warning disable CA2100 // Queries are constants and values are parameterized.
#pragma warning disable IDE0058 // Npgsql parameter registrations intentionally ignore the return value.

using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CryptoTrading.Persistence;

public sealed partial class PostgresTradingStore : IDurableSubmissionStore
{
    public async Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT payload_hash FROM trading.execution_submission_starts WHERE message_id = @message_id;", connection);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        var existingHash = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (existingHash is null)
        {
            return false;
        }

        if (!string.Equals(existingHash, SubmissionPayloadHash(message), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Submission identity collision: outbox content differs from its durable start record.");
        }

        return true;
    }

    public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) =>
        ValidateOutboxLeaseAsync(message, TimeSpan.Zero, cancellationToken);

    public async Task<bool> ValidateOutboxLeaseAsync(
        LeasedOutboxMessage message,
        TimeSpan minimumLeaseRemaining,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLeaseRemaining, TimeSpan.Zero);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM trading.outbox_messages
                WHERE message_id = @message_id AND lease_owner = @lease_owner
                AND locked_until >= clock_timestamp() + @minimum_lease_remaining
                AND processed_at IS NULL AND dead_lettered_at IS NULL);
            """, connection);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("lease_owner", message.LeaseOwner);
        command.Parameters.AddWithValue("minimum_lease_remaining", minimumLeaseRemaining);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message, TimeSpan minimumLeaseRemaining, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumLeaseRemaining, TimeSpan.Zero);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = new NpgsqlCommand(
            """
            UPDATE trading.outbox_messages
            SET locked_until = GREATEST(locked_until, clock_timestamp() + @minimum_lease_remaining)
            WHERE message_id = @message_id AND lease_owner = @lease_owner
              AND locked_until > clock_timestamp() AND processed_at IS NULL AND dead_lettered_at IS NULL
            RETURNING message_id;
            """, connection, transaction);
        lease.Parameters.AddWithValue("message_id", message.MessageId);
        lease.Parameters.AddWithValue("lease_owner", message.LeaseOwner);
        lease.Parameters.AddWithValue("minimum_lease_remaining", minimumLeaseRemaining);
        if (await lease.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        // The immutable unique row is the fence for the external side effect.
        // A later owner may query/reconcile, but never start a second submit.
        await using var start = new NpgsqlCommand(
            """
            INSERT INTO trading.execution_submission_starts(message_id, payload_hash, lease_owner, started_at)
            VALUES (@message_id, @payload_hash, @lease_owner, clock_timestamp())
            ON CONFLICT (message_id) DO NOTHING RETURNING message_id;
            """, connection, transaction);
        start.Parameters.AddWithValue("message_id", message.MessageId);
        start.Parameters.AddWithValue("payload_hash", SubmissionPayloadHash(message));
        start.Parameters.AddWithValue("lease_owner", message.LeaseOwner);
        var inserted = await start.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    private static string SubmissionPayloadHash(LeasedOutboxMessage message) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.PayloadJson)));
}
