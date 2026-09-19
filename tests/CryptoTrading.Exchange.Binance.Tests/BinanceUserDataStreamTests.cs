using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceUserDataStreamTests
{
    [Fact]
    public void BuildsAllowlistedUserStreamUriAndBackoff()
    {
        var options = new BinanceUserDataStreamOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/ws/")
        };
        var uri = BinanceUserDataStreamClient.BuildStreamUri(options.WebSocketBaseAddress, "listen-key-1");

        Assert.Equal("wss://stream.testnet.binance.vision/ws/listen-key-1", uri.ToString());
        Assert.Equal(TimeSpan.FromSeconds(2), BinanceUserDataStreamClient.ComputeBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1));
    }

    [Fact]
    public async Task ReadsExecutionAndPositionEventsAndQuarantinesInvalidPayload()
    {
        using var transport = new FakeTransport(
            "not-json",
            "{\"event\":{\"e\":\"executionReport\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"i\":42,\"t\":7,\"c\":\"client-1\",\"S\":\"BUY\",\"o\":\"LIMIT\",\"x\":\"TRADE\",\"X\":\"FILLED\",\"l\":\"0.01\",\"z\":\"0.01\",\"L\":\"100.50\",\"n\":\"0.001\",\"N\":\"USDT\"}}",
            "{\"e\":\"outboundAccountPosition\",\"E\":1720000000124,\"u\":1720000000120,\"B\":[{\"a\":\"USDT\",\"f\":\"100\",\"l\":\"0\"}]}");
        var client = new BinanceUserDataStreamClient(
            new BinanceUserDataStreamOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/ws/"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync("listen-key-1").GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var execution = Assert.IsType<BinanceExecutionReportEvent>(enumerator.Current);
        Assert.Equal(7, execution.TradeId);
        Assert.Equal(100.50m, execution.LastExecutedPrice);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<BinanceOutboundAccountPositionEvent>(enumerator.Current);
    }

    [Fact]
    public void RejectsUnsafeListenKeyAndEndpoint()
    {
        Assert.Throws<ArgumentException>(() => BinanceUserDataStreamClient.BuildStreamUri(new Uri("wss://stream.test.invalid/ws/"), "bad/key"));
        var options = new BinanceUserDataStreamOptions { WebSocketBaseAddress = new Uri("wss://api.binance.com/ws/") };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    private sealed class FakeTransport(params string[] payloads) : IBinanceWebSocketTransport, IDisposable
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

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

        public void Dispose()
        {
        }
    }
}
