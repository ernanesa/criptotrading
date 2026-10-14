using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceMarketDataClientTests
{
    [Fact]
    public void SubscriptionBuildsCombinedStreamName()
    {
        var trade = new BinanceMarketSubscription(" btcusdt ", BinanceMarketStreamKind.Trade);
        var kline = new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Kline, "1m");
        var bookTicker = new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.BookTicker);

        Assert.Equal("btcusdt@trade", trade.ToStreamName());
        Assert.Equal("btcusdt@kline_1m", kline.ToStreamName());
        Assert.Equal("btcusdt@bookTicker", bookTicker.ToStreamName());
        Assert.Equal(
            "wss://stream.testnet.binance.vision/stream?streams=btcusdt%40trade/btcusdt%40kline_1m/btcusdt%40bookTicker",
            BinanceSpotMarketDataClient.BuildStreamUri(
                new Uri("wss://stream.testnet.binance.vision/stream"),
                [trade.ToStreamName(), kline.ToStreamName(), bookTicker.ToStreamName()]).ToString());
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport for the duration of this test.")]
    public async Task ReadAsyncMapsBookTickerPayload()
    {
        var transport = new FakeTransport(
            "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":400900217,\"s\":\"BTCUSDT\",\"b\":\"25350.00\",\"B\":\"1.500\",\"a\":\"25351.00\",\"A\":\"2.000\"}}");
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.BookTicker)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var ticker = Assert.IsType<BinanceBookTickerStreamEvent>(enumerator.Current);
        Assert.Equal("BTCUSDT", ticker.Symbol);
        Assert.Equal(400900217, ticker.UpdateId);
        Assert.Equal(25350.00m, ticker.BestBidPrice);
        Assert.Equal(1.500m, ticker.BestBidQuantity);
        Assert.Equal(25351.00m, ticker.BestAskPrice);
        Assert.Equal(2.000m, ticker.BestAskQuantity);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport for the duration of this test.")]
    public async Task ReadAsyncQuarantinesInvalidBookTickerPayload()
    {
        var transport = new FakeTransport(
            "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":1,\"s\":\"BTCUSDT\",\"b\":\"25352.00\",\"B\":\"1.500\",\"a\":\"25350.00\",\"A\":\"2.000\"}}", // Crossed: ask < bid
            "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":2,\"s\":\"BTCUSDT\",\"b\":\"25350.00\",\"B\":\"1.500\",\"a\":\"25351.00\",\"A\":\"2.000\"}}"); // Valid
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.BookTicker)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var ticker = Assert.IsType<BinanceBookTickerStreamEvent>(enumerator.Current);
        Assert.Equal(2, ticker.UpdateId);
        Assert.Equal(25350.00m, ticker.BestBidPrice);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns the transport for the duration of this test.")]
    public async Task ReadAsyncMapsTradeAndQuarantinesInvalidPayload()
    {
        var transport = new FakeTransport(
            "not-json",
            "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":42,\"p\":\"100.50\",\"q\":\"0.010\",\"m\":true}}");
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var trade = Assert.IsType<BinanceTradeEvent>(enumerator.Current);
        Assert.Equal(42, trade.TradeId);
        Assert.Equal(100.50m, trade.Price);
        Assert.True(transport.Connected);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns both test transports for the duration of this test.")]
    public async Task ReadAsyncReconnectsWhenEstablishedSocketFailsWhileReading()
    {
        var first = new ThrowingTransport(new WebSocketException("simulated receive failure"));
        var second = new FakeTransport(
            "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":43,\"p\":\"101.50\",\"q\":\"0.010\",\"m\":false}}");
        var transports = new Queue<IBinanceWebSocketTransport>([first, second]);
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectAttempts = 2
            },
            () => transports.Dequeue());

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(43, Assert.IsType<BinanceTradeEvent>(enumerator.Current).TradeId);
    }

    [Fact]
    public void BackoffIsBounded()
    {
        var initial = TimeSpan.FromSeconds(1);
        var maximum = TimeSpan.FromSeconds(5);

        Assert.Equal(TimeSpan.FromSeconds(1), BinanceSpotMarketDataClient.ComputeBackoff(initial, maximum, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), BinanceSpotMarketDataClient.ComputeBackoff(initial, maximum, 1));
        Assert.Equal(maximum, BinanceSpotMarketDataClient.ComputeBackoff(initial, maximum, 10));
    }

    private sealed class FakeTransport(params string[] payloads) : IBinanceWebSocketTransport
    {
        public bool Connected { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Encoding.UTF8.GetBytes(payload);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

#pragma warning disable CA1812 // Test transport is instantiated through the interface queue.
    private sealed class ThrowingTransport(Exception exception) : IBinanceWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ReadOnlyMemory<byte>.Empty;
            throw exception;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CA1812
}
