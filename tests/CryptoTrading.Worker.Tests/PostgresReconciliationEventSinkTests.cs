using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class PostgresReconciliationEventSinkTests
{
    [Fact]
    public async Task PublishPersistsVersionedEventPayloadAndDigest()
    {
        var store = new RecordingEventStore();
        var sink = new PostgresReconciliationEventSink(store);
        var item = new BalanceReconciled(
            Guid.NewGuid(), CorrelationId.New(), DateTimeOffset.UtcNow, "USDT", 10m, 10m, 0m, 0.01m);

        await sink.PublishAsync(item, TestContext.Current.CancellationToken);

        var persisted = Assert.IsType<ReconciliationEventRecord>(store.Record);
        Assert.Equal(item.EventId, persisted.EventId);
        Assert.Equal(item.CorrelationId.Value, persisted.CorrelationId);
        Assert.Equal("BalanceReconciled", persisted.EventType);
        Assert.Equal((short)1, persisted.SchemaVersion);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(persisted.PayloadJson))),
            persisted.PayloadSha256);
        using var document = JsonDocument.Parse(persisted.PayloadJson);
        Assert.Equal("BalanceReconciled", document.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(persisted.EventId, document.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(persisted.CorrelationId, document.RootElement.GetProperty("correlationId").GetGuid());
        Assert.Contains("USDT", document.RootElement.GetProperty("data").GetRawText(), StringComparison.Ordinal);
    }

    private sealed class RecordingEventStore : IReconciliationEventStore
    {
        public ReconciliationEventRecord? Record { get; private set; }

        public Task<bool> AppendReconciliationEventAsync(
            ReconciliationEventRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record = record;
            return Task.FromResult(true);
        }
    }
}
