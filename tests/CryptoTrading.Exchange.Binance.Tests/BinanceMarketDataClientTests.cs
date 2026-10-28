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
        Assert.Equal(BinanceMarketDisconnectReason.ReadFailure, Assert.IsType<BinanceMarketStreamDisconnectedEvent>(enumerator.Current).Reason);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(43, Assert.IsType<BinanceTradeEvent>(enumerator.Current).TradeId);
    }

    [Theory]
    [InlineData("{\"e\":\"serverShutdown\",\"E\":1770123456789}")]
    [InlineData("{\"stream\":\"!serverShutdown\",\"data\":{\"e\":\"serverShutdown\",\"E\":1770123456789}}")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns both test transports.")]
    public async Task ReadAsyncReconnectsOnServerShutdownWithoutConsumingOldSession(string shutdown)
    {
        const string oldTrade = "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":1,\"p\":\"100.50\",\"q\":\"0.010\",\"m\":true}}";
        const string newTrade = "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":43,\"p\":\"101.50\",\"q\":\"0.010\",\"m\":false}}";
        var first = new FakeTransport(shutdown, oldTrade);
        var second = new FakeTransport(newTrade);
        var transports = new Queue<IBinanceWebSocketTransport>([first, second]);
        var client = new BinanceSpotMarketDataClient(new BinanceMarketDataOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 2
        }, () => transports.Dequeue());

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(BinanceMarketDisconnectReason.ServerShutdown, Assert.IsType<BinanceMarketStreamDisconnectedEvent>(enumerator.Current).Reason);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(43, Assert.IsType<BinanceTradeEvent>(enumerator.Current).TradeId);
        Assert.True(second.Connected);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns both test transports.")]
    public async Task ReadAsyncSignalsCleanClosureBeforeNextSession()
    {
        var first = new FakeTransport();
        var second = new FakeTransport("{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":43,\"p\":\"101.50\",\"q\":\"0.010\",\"m\":false}}");
        var transports = new Queue<IBinanceWebSocketTransport>([first, second]);
        var client = new BinanceSpotMarketDataClient(new BinanceMarketDataOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 2
        }, () => transports.Dequeue());
        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(BinanceMarketDisconnectReason.TransportClosed, Assert.IsType<BinanceMarketStreamDisconnectedEvent>(enumerator.Current).Reason);
        Assert.False(second.Connected);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(43, Assert.IsType<BinanceTradeEvent>(enumerator.Current).TradeId);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns both test transports.")]
    public async Task TransportCancellationDuringConnectSignalsAndReconnects()
    {
        var second = new FakeTransport("{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"t\":43,\"p\":\"101.50\",\"q\":\"0.010\",\"m\":false}}");
        var transports = new Queue<IBinanceWebSocketTransport>([new CancelledConnectTransport(), second]);
        var client = new BinanceSpotMarketDataClient(new BinanceMarketDataOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
            MaxReconnectAttempts = 2
        }, () => transports.Dequeue());
        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(BinanceMarketDisconnectReason.ReadFailure, Assert.IsType<BinanceMarketStreamDisconnectedEvent>(enumerator.Current).Reason);
        Assert.False(second.Connected);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(43, Assert.IsType<BinanceTradeEvent>(enumerator.Current).TradeId);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns the test transport.")]
    public async Task CallerCancellationDuringConnectEndsWithoutFailureEventOrReconnect()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var attempts = 0;
        var client = new BinanceSpotMarketDataClient(new BinanceMarketDataOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream")
        }, () =>
        {
            attempts++;
            return new CancelledConnectTransport(cancellation.Cancel);
        });
        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(1, attempts);
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

    [Fact]
    public void TryParseHandlesOverflowExceptionGracefully()
    {
        var overflowPayload = Encoding.UTF8.GetBytes(
            "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":400900217,\"s\":\"BTCUSDT\",\"b\":\"999999999999999999999999999999999999999999999999999999999999\",\"B\":\"1.5\",\"a\":\"100.0\",\"A\":\"1.0\"}}");
        var result = BinanceMarketDataParser.TryParse((ReadOnlyMemory<byte>)overflowPayload, DateTimeOffset.UtcNow);
        Assert.Null(result);
        var spanResult = BinanceMarketDataParser.TryParse((ReadOnlySpan<byte>)overflowPayload, DateTimeOffset.UtcNow);
        Assert.Null(spanResult);
    }

    [Fact]
    public void ReadOnlyMemoryAndSpanOverloadsProduceEquivalentResults()
    {
        var validPayload = Encoding.UTF8.GetBytes(
            "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":400900217,\"s\":\"BTCUSDT\",\"b\":\"25350.00\",\"B\":\"1.500\",\"a\":\"25351.00\",\"A\":\"2.000\"}}");
        var now = DateTimeOffset.UtcNow;
        var memoryResult = BinanceMarketDataParser.TryParse((ReadOnlyMemory<byte>)validPayload, now);
        var spanResult = BinanceMarketDataParser.TryParse((ReadOnlySpan<byte>)validPayload, now);

        Assert.NotNull(memoryResult);
        Assert.NotNull(spanResult);
        var memTicker = Assert.IsType<BinanceBookTickerStreamEvent>(memoryResult);
        var spanTicker = Assert.IsType<BinanceBookTickerStreamEvent>(spanResult);
        Assert.Equal(memTicker.Symbol, spanTicker.Symbol);
        Assert.Equal(memTicker.BestBidPrice, spanTicker.BestBidPrice);
        Assert.Equal(memTicker.BestAskPrice, spanTicker.BestAskPrice);
    }

#pragma warning disable CA1812 // Test transport is instantiated through the interface queue.
    private sealed class CancelledConnectTransport(Action? onConnect = null) : IBinanceWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            onConnect?.Invoke();
            return Task.FromException(new OperationCanceledException());
        }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
