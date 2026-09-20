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
    public async Task UnknownStatusFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(new RecordingStore(), Event("UNKNOWN")));
    }

    private static BinanceExecutionReportEvent Event(string status) => new(
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
        status == "FILLED" ? 0.01m : 0m,
        100.50m,
        0.001m,
        "USDT");

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
