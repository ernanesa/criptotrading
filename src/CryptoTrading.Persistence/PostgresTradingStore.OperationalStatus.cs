#pragma warning disable CA2007 // Npgsql disposal follows the existing store convention.
using CryptoTrading.Domain.Risk;
using Npgsql;

namespace CryptoTrading.Persistence;

public interface IOperationalControlCurrentStateStore
{
    Task<OperationalControlAuditEvent?> ReadLatestOperationalControlAuditEventAsync(CancellationToken cancellationToken = default);
}

public sealed partial class PostgresTradingStore : IOperationalControlCurrentStateStore
{
    public async Task<OperationalControlAuditEvent?> ReadLatestOperationalControlAuditEventAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT event_id, schema_version, event_type, from_mode, to_mode, actor, reason,
                   idempotency_key, occurred_at, occurred_at_ticks, approval_id, payload_sha256
            FROM trading.operational_control_audit_events
            ORDER BY occurred_at DESC, occurred_at_ticks DESC, event_id DESC LIMIT 1;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var auditEvent = ReadOperationalControlAuditEvent(reader);
        if (!string.Equals(auditEvent.PayloadSha256, reader.GetString(11).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Operational-control current state has invalid audit integrity.");
        }
        return auditEvent;
    }
}
