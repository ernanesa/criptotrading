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
            "{\"event\":{\"e\":\"executionReport\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"i\":42,\"t\":7,\"c\":\"client-1\",\"S\":\"BUY\",\"o\":\"LIMIT\",\"x\":\"TRADE\",\"X\":\"FILLED\",\"l\":\"0.01\",\"z\":\"0.01\",\"L\":\"100.50\",\"n\":\"0.001\",\"N\":\"USDT\",\"q\":\"0.01\"}}",
            "{\"e\":\"outboundAccountPosition\",\"E\":1720000000124,\"u\":1720000000120,\"B\":[{\"a\":\"USDT\",\"f\":\"100\",\"l\":\"0\"}]}");
        var client = new BinanceUserDataStreamClient(
            new BinanceUserDataStreamOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/ws/"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync("listen-key-1", cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var execution = Assert.IsType<BinanceExecutionReportEvent>(enumerator.Current);
        Assert.Equal(7, execution.TradeId);
        Assert.Equal(100.50m, execution.LastExecutedPrice);
        Assert.Equal(0.01m, execution.OriginalQuantity);
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

        var listenKey = await client.CreateListenKeyAsync(cancellationToken: TestContext.Current.CancellationToken);
        await client.KeepAliveListenKeyAsync(listenKey, cancellationToken: TestContext.Current.CancellationToken);
        await client.CloseListenKeyAsync(listenKey, cancellationToken: TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task ModernApiClientEmitsConnectionLostWhenWebSocketTransportClosesWithoutServerShutdown()
    {
        using var transport = new AbruptDisconnectFakeTransport();
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

        Assert.True(await enumerator.MoveNextAsync());
        var lost = Assert.IsType<BinanceUserDataStreamControlEvent>(enumerator.Current);
        Assert.Equal("connectionLost", lost.EventType);
        Assert.Equal(11, lost.SubscriptionId);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task ModernApiClientRecoversWhenInitialSubscriptionHandshakeStalls()
    {
        var attemptCount = 0;
        var client = new BinanceUserDataStreamApiClient(
            new BinanceUserDataStreamApiOptions
            {
                ApiKey = "api-key",
                ApiSecret = "secret",
                ConnectAndSubscribeTimeout = TimeSpan.FromMilliseconds(200),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectAttempts = 3
            },
            () => Interlocked.Increment(ref attemptCount) == 1
                ? new StalledSubscriptionFakeTransport()
                : new ModernFakeTransport());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = client.ReadAsync("ignored-by-modern-api", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        var established = Assert.IsType<BinanceUserDataStreamControlEvent>(enumerator.Current);
        Assert.Equal("connectionEstablished", established.EventType);
        Assert.Equal(2, established.SessionGeneration);
        Assert.Equal(2, attemptCount);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task HandshakeTimeoutWaitsForPendingReadBeforeDisposalAndReconnect()
    {
        using var first = new DelayedCancellationTransport();
        var attemptCount = 0;
        var client = new BinanceUserDataStreamApiClient(
            new BinanceUserDataStreamApiOptions
            {
                ApiKey = "api-key",
                ApiSecret = "secret",
                ConnectAndSubscribeTimeout = TimeSpan.FromMilliseconds(50),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectAttempts = 3
            },
            () => Interlocked.Increment(ref attemptCount) == 1 ? first : new ModernFakeTransport());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = client.ReadAsync("ignored", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var move = enumerator.MoveNextAsync().AsTask();
        try
        {
            await first.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(async () => await move.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(false));
            Assert.Equal(1, attemptCount);
        }
        finally
        {
            first.Release.TrySetResult();
        }

        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(2, attemptCount);
        await cancellation.CancelAsync();
    }

    [Fact]
    public async Task ExternalCancellationDuringHandshakeWaitsForReadWithoutReconnecting()
    {
        using var first = new DelayedCancellationTransport();
        var attempts = 0;
        var client = new BinanceUserDataStreamApiClient(
            new BinanceUserDataStreamApiOptions
            {
                ApiKey = "api-key",
                ApiSecret = "secret",
                ConnectAndSubscribeTimeout = TimeSpan.FromSeconds(5)
            },
            () => { Interlocked.Increment(ref attempts); return first; });
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = client.ReadAsync("ignored", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var move = enumerator.MoveNextAsync().AsTask();
        await first.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        try
        {
            await first.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(async () => await move.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(false));
            Assert.Equal(1, attempts);
        }
        finally
        {
            first.Release.TrySetResult();
        }

        Assert.False(await move.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void BinanceUserDataParserMemoryAndSpanOverloadsProduceEquivalentResults()
    {
        var json = "{\"e\":\"outboundAccountPosition\",\"E\":1499405638848,\"u\":1499405638848,\"B\":[{\"a\":\"BTC\",\"f\":\"100.0\",\"l\":\"0.0\"}]}";
        var payload = Encoding.UTF8.GetBytes(json);
        var now = DateTimeOffset.UtcNow;

        var memoryResult = BinanceUserDataParser.TryParse((ReadOnlyMemory<byte>)payload, now);
        var spanResult = BinanceUserDataParser.TryParse((ReadOnlySpan<byte>)payload, now);

        Assert.NotNull(memoryResult);
        Assert.NotNull(spanResult);
        var memPos = Assert.IsType<BinanceOutboundAccountPositionEvent>(memoryResult);
        var spanPos = Assert.IsType<BinanceOutboundAccountPositionEvent>(spanResult);
        Assert.Equal(memPos.Balances.Count, spanPos.Balances.Count);
        Assert.Equal(memPos.Balances[0].Asset, spanPos.Balances[0].Asset);
        Assert.Equal(memPos.Balances[0].Free, spanPos.Balances[0].Free);
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
            payloads.Add(Encoding.UTF8.GetBytes("{\"subscriptionId\":7,\"event\":{\"e\":\"executionReport\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"i\":99,\"t\":8,\"c\":\"client-99\",\"S\":\"BUY\",\"o\":\"LIMIT\",\"x\":\"TRADE\",\"X\":\"FILLED\",\"l\":\"0.01\",\"z\":\"0.01\",\"L\":\"100.50\",\"n\":\"0.001\",\"N\":\"USDT\",\"q\":\"0.01\"}}"));
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

    private sealed class AbruptDisconnectFakeTransport : IBinanceWebSocketTransport, IDisposable
    {
        private readonly List<ReadOnlyMemory<byte>> payloads = [];

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            using var request = JsonDocument.Parse(payload.ToArray());
            var id = request.RootElement.GetProperty("id").GetGuid();
            payloads.Add(Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"status\":200,\"result\":{{\"subscriptionId\":11}}}}"));
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

    private sealed class StalledSubscriptionFakeTransport : IBinanceWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedCancellationTransport : IBinanceWebSocketTransport, IDisposable
    {
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                throw;
            }

            yield break;
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
