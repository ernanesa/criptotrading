using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AccountStreamOrderProjectionTests
{
    [Fact]
    public async Task ActiveExecutionReportUpsertsOpenOrder()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW"));

        var record = Assert.Single(store.Records);
        Assert.Equal("BTCUSDT", record.Symbol);
        Assert.Equal(42, record.OrderId);
        Assert.Equal("client-42", record.ClientOrderId);
        Assert.Equal("NEW", record.Status);
        Assert.Equal(0.01m, record.OriginalQuantity);
        Assert.Equal(0m, record.CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task TerminalExecutionReportRemovesOpenOrder()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW"));
        await AccountStreamOrderProjection.ApplyAsync(store, Event("FILLED"));

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task PendingCancelExecutionReportRemainsOpenUntilTerminalOutcome()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PENDING_CANCEL"));

        var record = Assert.Single(store.Records);
        Assert.Equal("PENDING_CANCEL", record.Status);
    }

    [Fact]
    public async Task UnknownStatusFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(new RecordingStore(), Event("UNKNOWN")));
    }

    [Fact]
    public async Task CumulativeExecutionAboveOriginalQuantityFailsClosedWithoutChangingProjection()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.006m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.011m)));

        var record = Assert.Single(store.Records);
        Assert.Equal(0.006m, record.CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task CumulativeExecutionRegressionFailsClosedWithoutChangingProjection()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.006m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.005m)));

        Assert.Equal(0.006m, Assert.Single(store.Records).CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task FilledExecutionMustMatchOriginalQuantity()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(
                new RecordingStore(),
                Event("FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.009m)));
    }

    private static BinanceExecutionReportEvent Event(
        string status,
        decimal? originalQuantity = 0.01m,
        decimal? cumulativeQuantity = null) => new(
        DateTimeOffset.UtcNow,
        1_700_000_000_123,
        "BTCUSDT",
        42,
        7,
        "client-42",
        "BUY",
        "LIMIT",
        status == "FILLED" ? "TRADE" : "NEW",
        status,
        status == "FILLED" ? 0.01m : 0m,
        cumulativeQuantity ?? (status == "FILLED" ? 0.01m : 0m),
        100.50m,
        0.001m,
        "USDT",
        originalQuantity);

    private sealed class RecordingStore : ILocalOpenOrderStore
    {
        public List<LocalOpenOrderRecord> Records { get; } = [];

        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>(Records);

        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.ClientOrderId == order.ClientOrderId);
            Records.Add(order);
            return Task.CompletedTask;
        }

        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.ClientOrderId == clientOrderId);
            return Task.CompletedTask;
        }
    }
}
