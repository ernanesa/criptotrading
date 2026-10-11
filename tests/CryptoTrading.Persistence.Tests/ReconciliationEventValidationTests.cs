using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class ReconciliationEventValidationTests
{
    [Fact]
    public async Task AppendRejectsPayloadWhoseIdentityDiffersFromIndexedColumns()
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventType = "BalanceReconciled",
            eventId = Guid.NewGuid(),
            correlationId,
            occurredAtUtc,
            data = new { asset = "USDT", delta = 0m },
        });
        var record = new ReconciliationEventRecord(
            eventId,
            correlationId,
            occurredAtUtc,
            "BalanceReconciled",
            1,
            payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
#pragma warning disable CA2007 // Preserve xUnit's synchronization context for test lifetime.
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(
            "Host=127.0.0.1;Database=local-test;Username=local-test"));

        var exception = await Record.ExceptionAsync(
            () => store.AppendReconciliationEventAsync(record, TestContext.Current.CancellationToken)).ConfigureAwait(true);
#pragma warning restore CA2007
        Assert.IsType<ArgumentException>(exception);
    }
}
