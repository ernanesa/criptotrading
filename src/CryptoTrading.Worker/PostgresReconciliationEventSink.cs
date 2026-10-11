using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;

namespace CryptoTrading.Worker;

/// <summary>Persists reconciliation events as versioned append-only evidence.</summary>
internal sealed class PostgresReconciliationEventSink(IReconciliationEventStore store) : IReconciliationEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(ReconciliationEvent reconciliationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliationEvent);
        cancellationToken.ThrowIfCancellationRequested();
        var eventType = reconciliationEvent.GetType().Name;
        var serializedEvent = JsonSerializer.SerializeToElement(reconciliationEvent, reconciliationEvent.GetType(), JsonOptions);
        var eventData = serializedEvent.EnumerateObject()
            .Where(static property => property.Name is not ("eventId" or "correlationId" or "occurredAtUtc"))
            .ToDictionary(static property => property.Name, static property => property.Value.Clone(), StringComparer.Ordinal);
        var envelope = new ReconciliationEventEnvelope(
            1,
            eventType,
            reconciliationEvent.EventId,
            reconciliationEvent.CorrelationId.Value,
            reconciliationEvent.OccurredAtUtc,
            eventData);
        var payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return store.AppendReconciliationEventAsync(new ReconciliationEventRecord(
            reconciliationEvent.EventId,
            reconciliationEvent.CorrelationId.Value,
            reconciliationEvent.OccurredAtUtc,
            eventType,
            SchemaVersion: 1,
            payloadJson,
            payloadHash), cancellationToken);
    }

    private sealed record ReconciliationEventEnvelope(
        short SchemaVersion,
        string EventType,
        Guid EventId,
        Guid CorrelationId,
        DateTimeOffset OccurredAtUtc,
        IReadOnlyDictionary<string, JsonElement> Data);
}
