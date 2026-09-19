using System.Text;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
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

    [Fact]
    public async Task ManagesListenKeyLifecycleWithoutExposingMutationEndpoints()
    {
        using var handler = new LifecycleHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://testnet.binance.vision/api/")
        };
        var client = new BinanceUserDataStreamLifecycleClient(
            httpClient,
            new BinanceUserDataStreamRestOptions
            {
                RestBaseAddress = httpClient.BaseAddress,
                ApiKey = "testnet-key"
            });

        var listenKey = await client.CreateListenKeyAsync();
        await client.KeepAliveListenKeyAsync(listenKey);
        await client.CloseListenKeyAsync(listenKey);

        Assert.Equal([HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete], handler.Methods);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("testnet-key", request.Headers.GetValues("X-MBX-APIKEY").Single());
            Assert.Contains("userDataStream", request.RequestUri!.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LifecycleClientRejectsProductionHostAndBlankKey()
    {
        Assert.Throws<InvalidOperationException>(() => new BinanceUserDataStreamLifecycleClient(
            new HttpClient(new LifecycleHandler()),
            new BinanceUserDataStreamRestOptions
            {
                RestBaseAddress = new Uri("https://api.binance.com/api/"),
                ApiKey = "testnet-key"
            }));
        using var invalidHandler = new LifecycleHandler();
        using var invalidHttpClient = new HttpClient(invalidHandler);
        Assert.Throws<ArgumentException>(() => new BinanceUserDataStreamLifecycleClient(
            invalidHttpClient,
            new BinanceUserDataStreamRestOptions
            {
                RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
                ApiKey = " "
            }));
    }

    [Fact]
    public void BuildsModernHmacSubscriptionWithSortedSignaturePayload()
    {
        var request = BinanceUserDataStreamApiClient.BuildSubscriptionRequest(
            "api-key", "secret", 1_700_000_000_000, 5_000, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        using var document = JsonDocument.Parse(request);
        var parameters = document.RootElement.GetProperty("params");
        var expectedPayload = "apiKey=api-key&recvWindow=5000&timestamp=1700000000000";
        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(expectedPayload))).ToUpperInvariant();
        Assert.Equal("userDataStream.subscribe.signature", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(expectedSignature, parameters.GetProperty("signature").GetString()!.ToUpperInvariant());
    }

    [Fact]
    public void RejectsSessionLifetimeBeyondBinanceMaximum()
    {
        var options = new BinanceUserDataStreamApiOptions
        {
            ApiKey = "api-key",
            ApiSecret = "secret",
            MaxSessionLifetime = TimeSpan.FromHours(24).Add(TimeSpan.FromMilliseconds(1))
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public async Task ModernApiClientConsumesAcknowledgementAndEmitsNestedEvents()
    {
        using var transport = new ModernFakeTransport();
        var client = new BinanceUserDataStreamApiClient(
            new BinanceUserDataStreamApiOptions
            {
                ApiKey = "api-key",
                ApiSecret = "secret",
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectAttempts = 1
            },
            () => transport);

        using var cancellation = new CancellationTokenSource();
        await using var enumerator = client.ReadAsync("ignored-by-modern-api", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        var established = Assert.IsType<BinanceUserDataStreamControlEvent>(enumerator.Current);
        Assert.Equal("connectionEstablished", established.EventType);
        Assert.Equal(7, established.SubscriptionId);
        Assert.NotNull(established.SessionId);
        Assert.Equal(1, established.SessionGeneration);
        Assert.Equal(7, established.StreamSubscriptionId);
        Assert.True(await enumerator.MoveNextAsync());
        var execution = Assert.IsType<BinanceExecutionReportEvent>(enumerator.Current);
        Assert.Equal(99, execution.OrderId);
        Assert.Equal(established.SessionId, execution.SessionId);
        Assert.Equal(established.SessionGeneration, execution.SessionGeneration);
        Assert.Equal(established.StreamSubscriptionId, execution.StreamSubscriptionId);
        Assert.True(await enumerator.MoveNextAsync());
        var shutdown = Assert.IsType<BinanceUserDataStreamControlEvent>(enumerator.Current);
        Assert.Equal("serverShutdown", shutdown.EventType);
        await cancellation.CancelAsync();
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

    private sealed class ModernFakeTransport : IBinanceWebSocketTransport, IDisposable
    {
        private readonly List<ReadOnlyMemory<byte>> payloads = [];

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            using var request = JsonDocument.Parse(payload.ToArray());
            var id = request.RootElement.GetProperty("id").GetGuid();
            payloads.Add(Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"status\":200,\"result\":{{\"subscriptionId\":7}}}}"));
            payloads.Add(Encoding.UTF8.GetBytes("{\"subscriptionId\":7,\"event\":{\"e\":\"executionReport\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"i\":99,\"t\":8,\"c\":\"client-99\",\"S\":\"BUY\",\"o\":\"LIMIT\",\"x\":\"TRADE\",\"X\":\"FILLED\",\"l\":\"0.01\",\"z\":\"0.01\",\"L\":\"100.50\",\"n\":\"0.001\",\"N\":\"USDT\"}}"));
            payloads.Add(Encoding.UTF8.GetBytes("{\"subscriptionId\":7,\"event\":{\"e\":\"serverShutdown\",\"E\":1720000000124}}"));
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return payload;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() { }
    }

    private sealed class LifecycleHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = request.Method == HttpMethod.Post
                    ? new StringContent("{\"listenKey\":\"listen-key-1\"}", Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty)
            };
            return Task.FromResult(response);
        }
    }

}
