using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceLocalOrderBookTests
{
    [Fact]
    public void DeltaBeforeSnapshotRequestsResync()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");

        var result = book.ApplyDelta(Delta(1, 1));

        Assert.Equal(BinanceOrderBookApplyDisposition.ResyncRequired, result.Disposition);
        Assert.True(result.ResyncRequired);
        Assert.Equal(BinanceOrderBookInvalidReason.NoSnapshot, result.InvalidReason);
        Assert.Single(book.Quarantined);
    }

    [Fact]
    public void FirstDeltaMayBridgeSnapshotBoundaryAndSubsequentDeltaMustBeContiguous()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        Assert.Equal(BinanceOrderBookApplyDisposition.SnapshotApplied, book.ApplySnapshot(Snapshot(100)).Disposition);

        var first = book.ApplyDelta(Delta(99, 101));
        var second = book.ApplyDelta(Delta(102, 102, price: 101));

        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, first.Disposition);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, second.Disposition);
        Assert.Equal(102, book.LastUpdateId);
        Assert.Equal([101m, 100m], book.Bids.Select(level => level.Price).ToArray());
    }

    [Fact]
    public void DuplicateIsQuarantinedWithoutInvalidatingHealthyBook()
    {
        var book = NewBookAt(100);
        book.ApplyDelta(Delta(101, 101));

        var result = book.ApplyDelta(Delta(101, 101, price: 999));

        Assert.Equal(BinanceOrderBookApplyDisposition.DuplicateQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookStatus.Healthy, book.Status);
        Assert.False(book.ResyncRequired);
        Assert.DoesNotContain(book.Bids, level => level.Price == 999);
    }

    [Fact]
    public void GapInvalidatesBookAndRequiresFreshSnapshot()
    {
        var book = NewBookAt(100);
        var result = book.ApplyDelta(Delta(102, 102));

        Assert.Equal(BinanceOrderBookApplyDisposition.GapDetected, result.Disposition);
        Assert.Equal(BinanceOrderBookInvalidReason.SequenceGap, result.InvalidReason);
        Assert.True(book.ResyncRequired);

        Assert.Equal(BinanceOrderBookApplyDisposition.SnapshotApplied, book.ApplySnapshot(Snapshot(102)).Disposition);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(103, 103)).Disposition);
        Assert.Equal(BinanceOrderBookStatus.Healthy, book.Status);
    }

    [Fact]
    public void OutOfOrderDeltaIsQuarantinedAndBookIsInvalid()
    {
        var book = NewBookAt(100);
        book.ApplyDelta(Delta(101, 102));

        var result = book.ApplyDelta(Delta(102, 103));

        Assert.Equal(BinanceOrderBookApplyDisposition.OutOfOrderQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookInvalidReason.OutOfOrder, result.InvalidReason);
        Assert.True(book.ResyncRequired);
    }

    [Fact]
    public void BookKeepsOnlyBestConfiguredLevelsOnEachSide()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT", maxLevels: 2);
        book.ApplySnapshot(new BinanceOrderBookSnapshot(
            "BTCUSDT",
            10,
            [Level(99), Level(100), Level(101)],
            [Level(102), Level(103), Level(104)]));

        Assert.Equal([101m, 100m], book.Bids.Select(level => level.Price).ToArray());
        Assert.Equal([102m, 103m], book.Asks.Select(level => level.Price).ToArray());
    }

    [Fact]
    public void QuarantineIsBounded()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT", quarantineCapacity: 2);
        book.ApplyDelta(Delta(1, 1));
        book.ApplyDelta(Delta(2, 2));
        book.ApplyDelta(Delta(3, 3));

        Assert.Equal(2, book.Quarantined.Count);
        Assert.Equal(2, book.Quarantined.First().FirstUpdateId);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The market-data client owns the injected transport for the duration of this test.")]
    public async Task ParserMapsDepthUpdateAndInvalidPayloadIsQuarantined()
    {
        const string payload = "{\"stream\":\"btcusdt@depth@100ms\",\"data\":{\"e\":\"depthUpdate\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"U\":7,\"u\":8,\"pu\":6,\"b\":[[\"100.00\",\"1.25\"]],\"a\":[[\"101.00\",\"0.75\"]]}}";
        var transport = new DepthTransport(payload);
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.test.invalid/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)]).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var update = Assert.IsType<BinanceDepthUpdateEvent>(enumerator.Current);
        Assert.Equal(7, update.FirstUpdateId);
        Assert.Equal(8, update.FinalUpdateId);
        Assert.Equal(6, update.PreviousFinalUpdateId);
        Assert.Equal(1.25m, Assert.Single(update.Bids).Quantity);
    }

    private static BinanceLocalOrderBook NewBookAt(long updateId)
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        book.ApplySnapshot(Snapshot(updateId));
        return book;
    }

    private static BinanceOrderBookSnapshot Snapshot(long updateId) =>
        new("BTCUSDT", updateId, [Level(100)], [Level(101)]);

    private static BinanceDepthUpdateEvent Delta(long first, long final, decimal price = 100) =>
        new("btcusdt@depth@100ms", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, first, final, null, [Level(price)], []);

    private static BinanceOrderBookLevel Level(decimal price) => new(price, 1m);

    private sealed class DepthTransport(string payload) : IBinanceWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Encoding.UTF8.GetBytes(payload);
            await Task.Yield();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
