using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;

namespace CryptoTrading.Persistence;

/// <summary>
/// Recovery metadata captured from an outbox reader. It intentionally contains
/// no payload or credential material; the payload remains in the durable outbox.
/// </summary>
public sealed record OutboxRecoveryMetadata(
    Guid MessageId,
    string Status,
    int Attempts,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset? LockedUntilUtc = null,
    DateTimeOffset? ProcessedAtUtc = null)
{
    public void Validate()
    {
        if (MessageId == Guid.Empty)
        {
            throw new ArgumentException("Outbox recovery message IDs cannot be empty.", nameof(MessageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Status);
        if (Status.Trim().Length > 32 || Status.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(Status));
        }

        if (Attempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Attempts));
        }

        EnsureUtc(AvailableAtUtc, nameof(AvailableAtUtc));
        if (LockedUntilUtc is { } lockedUntil)
        {
            EnsureUtc(lockedUntil, nameof(LockedUntilUtc));
        }

        if (ProcessedAtUtc is { } processedAt)
        {
            EnsureUtc(processedAt, nameof(ProcessedAtUtc));
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recovery timestamps must be UTC.", parameterName);
        }
    }
}

/// <summary>All local durable evidence needed to produce a recovery manifest.</summary>
public sealed record FullStateReconstructionInput(
    IReadOnlyCollection<LedgerEntry> LedgerJournal,
    IReadOnlyCollection<LocalOpenOrderRecord> OpenOrderProjection,
    IReadOnlyCollection<AccountStreamEventRecord> AccountStreamEvidence,
    IReadOnlyCollection<OutboxRecoveryMetadata> OutboxRecoveryMetadata)
{
    public static FullStateReconstructionInput Create(
        IEnumerable<LedgerEntry> ledgerJournal,
        IEnumerable<LocalOpenOrderRecord> openOrderProjection,
        IEnumerable<AccountStreamEventRecord> accountStreamEvidence,
        IEnumerable<OutboxRecoveryMetadata> outboxRecoveryMetadata)
    {
        ArgumentNullException.ThrowIfNull(ledgerJournal);
        ArgumentNullException.ThrowIfNull(openOrderProjection);
        ArgumentNullException.ThrowIfNull(accountStreamEvidence);
        ArgumentNullException.ThrowIfNull(outboxRecoveryMetadata);

        return new(
            ledgerJournal.ToArray(),
            openOrderProjection.ToArray(),
            accountStreamEvidence.ToArray(),
            outboxRecoveryMetadata.ToArray());
    }
}

/// <summary>
/// A deterministic, read-only recovery manifest for the currently durable
/// local state slices. The hash excludes capture time and rebuild duration.
/// </summary>
public sealed record FullStateReconstructionManifest(
    LedgerReconstructionResult Ledger,
    int OpenOrderCount,
    int AccountStreamEventCount,
    int OutboxRecoveryRecordCount,
    string ManifestSha256,
    DateTimeOffset CapturedAtUtc,
    bool HasAllLocalEvidence)
{
    public const string SchemaVersion = "full-state-reconstruction/v1";

}

/// <summary>
/// Validates local state slices and rebuilds the ledger without changing the
/// database or any projection. It is suitable for restore/restart drills.
/// </summary>
public static class FullStateReconstructor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false
    };

    public static FullStateReconstructionManifest Build(
        FullStateReconstructionInput input,
        IEnumerable<AccountId>? unconstrainedAccounts = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.LedgerJournal);
        ArgumentNullException.ThrowIfNull(input.OpenOrderProjection);
        ArgumentNullException.ThrowIfNull(input.AccountStreamEvidence);
        ArgumentNullException.ThrowIfNull(input.OutboxRecoveryMetadata);
        ValidateLedger(input.LedgerJournal);
        ValidateOpenOrders(input.OpenOrderProjection);
        ValidateAccountStream(input.AccountStreamEvidence);
        ValidateOutbox(input.OutboxRecoveryMetadata);

        var ledger = CryptoTrading.Domain.Accounting.LedgerReconstructor.Rebuild(
            input.LedgerJournal,
            unconstrainedAccounts,
            timeProvider);
        var canonical = Canonicalize(input);

        return new(
            ledger,
            input.OpenOrderProjection.Count,
            input.AccountStreamEvidence.Count,
            input.OutboxRecoveryMetadata.Count,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            true);
    }

    private static void ValidateLedger(IReadOnlyCollection<LedgerEntry> entries)
    {
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!ids.Add(entry.Id.Value))
            {
                throw new InvalidOperationException($"Ledger entry {entry.Id.Value} appears more than once.");
            }
        }
    }

    private static void ValidateOpenOrders(IReadOnlyCollection<LocalOpenOrderRecord> orders)
    {
        var clientOrderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            ArgumentNullException.ThrowIfNull(order);
            if (!clientOrderIds.Add(order.ClientOrderId))
            {
                throw new InvalidOperationException($"Open-order projection contains duplicate client order '{order.ClientOrderId}'.");
            }

            if (order.UpdatedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Open-order projection timestamps must be UTC.", nameof(orders));
            }
        }
    }

    private static void ValidateAccountStream(IReadOnlyCollection<AccountStreamEventRecord> events)
    {
        var eventIds = new HashSet<Guid>();
        var dedupeKeys = new HashSet<(string Type, string Key)>();
        foreach (var record in events)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (!eventIds.Add(record.EventId))
            {
                throw new InvalidOperationException($"Account stream event {record.EventId} appears more than once.");
            }

            if (!dedupeKeys.Add((record.EventType, record.DedupeKey)))
            {
                throw new InvalidOperationException($"Account stream dedupe identity '{record.EventType}/{record.DedupeKey}' appears more than once.");
            }

            if (!string.Equals(record.PayloadSha256, AccountStreamEventRecord.ComputeHash(record.PayloadJson), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Account stream event {record.EventId} has an invalid payload hash.");
            }
        }
    }

    private static void ValidateOutbox(IReadOnlyCollection<OutboxRecoveryMetadata> metadata)
    {
        var ids = new HashSet<Guid>();
        foreach (var item in metadata)
        {
            ArgumentNullException.ThrowIfNull(item);
            item.Validate();
            if (!ids.Add(item.MessageId))
            {
                throw new InvalidOperationException($"Outbox message {item.MessageId} appears more than once.");
            }
        }
    }

    private static string Canonicalize(FullStateReconstructionInput input)
    {
        var canonical = new
        {
            schemaVersion = FullStateReconstructionManifest.SchemaVersion,
            ledger = input.LedgerJournal
                .OrderBy(static entry => entry.OccurredAt.ToUniversalTime())
                .ThenBy(static entry => entry.Id.Value)
                .Select(static entry => new
                {
                    id = entry.Id.Value,
                    correlationId = entry.CorrelationId.Value,
                    occurredAtTicks = entry.OccurredAt.ToUniversalTime().Ticks,
                    entry.Reason,
                    idempotencyKey = entry.IdempotencyKey?.Value,
                    postings = entry.Postings
                        .Select(static posting => new
                        {
                            account = posting.Account.Value,
                            asset = posting.Amount.Asset.Value,
                            amount = posting.Amount.Amount
                        })
                        .ToArray()
                })
                .ToArray(),
            openOrders = input.OpenOrderProjection
                .OrderBy(static order => order.ClientOrderId, StringComparer.Ordinal)
                .Select(static order => new
                {
                    order.Symbol,
                    order.OrderId,
                    order.ClientOrderId,
                    order.Status,
                    updatedAtTicks = order.UpdatedAtUtc.ToUniversalTime().Ticks
                })
                .ToArray(),
            accountStream = input.AccountStreamEvidence
                .OrderBy(static item => item.ReceivedAtUtc.ToUniversalTime())
                .ThenBy(static item => item.EventId)
                .Select(static item => new
                {
                    eventId = item.EventId,
                    item.EventType,
                    item.DedupeKey,
                    item.EventTimeMilliseconds,
                    receivedAtTicks = item.ReceivedAtUtc.ToUniversalTime().Ticks,
                    item.Symbol,
                    item.PayloadSha256,
                    item.SessionId,
                    item.SessionGeneration,
                    item.SubscriptionId
                })
                .ToArray(),
            outbox = input.OutboxRecoveryMetadata
                .OrderBy(static item => item.MessageId)
                .Select(static item => new
                {
                    messageId = item.MessageId,
                    item.Status,
                    item.Attempts,
                    availableAtTicks = item.AvailableAtUtc.ToUniversalTime().Ticks,
                    lockedUntilTicks = item.LockedUntilUtc?.ToUniversalTime().Ticks,
                    processedAtTicks = item.ProcessedAtUtc?.ToUniversalTime().Ticks
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(canonical, JsonOptions);
    }
}
