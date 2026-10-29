using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Observability;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

[Collection(TelemetryActivityFixture.Name)]
public sealed class BinanceSpotOrderClientTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SubmitIsFailClosedByDefaultAndDoesNotSendARequest()
    {
        using var handler = new RecordingHandler();
        var client = CreateClient(handler, enableExecution: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SubmitUsesOneSignedPostWithoutRetryAndPreservesClientIdentity()
    {
        using var handler = new RecordingHandler(
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"NEW\",\"transactTime\":1789905600000}");
        var client = CreateClient(handler, enableExecution: true);

        var result = await client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, result.OrderId);
        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.Equal("ct-test-1", result.ClientOrderId);
        Assert.Equal(100.50m, result.Price);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v3/time", handler.Requests[0].RequestUri!.AbsolutePath);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("test-api-key", request.Headers.GetValues("X-MBX-APIKEY").Single());
        Assert.DoesNotContain("test-api-secret", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("newClientOrderId=ct-test-1", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("type=LIMIT", request.RequestUri.Query, StringComparison.Ordinal);
        AssertSignatureIsValid(request, "test-api-secret");
    }

    [Fact]
    public async Task QueryAndCancelUseClientOrderIdAndExpectedHttpVerbs()
    {
        using var handler = new RecordingHandler(
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"CANCELED\",\"transactTime\":1789905600000}",
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"CANCELED\",\"transactTime\":1789905600000}");
        var client = CreateClient(handler, enableExecution: true);

        var queried = await client.QueryByClientOrderIdAsync(" btcusdt ", "ct-test-1", cancellationToken: TestContext.Current.CancellationToken);
        var cancelled = await client.CancelAsync("BTCUSDT", "ct-test-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("CANCELED", queried.Status);
        Assert.Equal("CANCELED", cancelled.Status);
        var orderRequests = handler.Requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith("/v3/order", StringComparison.Ordinal)).ToArray();
        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], orderRequests.Select(request => request.Method));
        Assert.All(orderRequests, request =>
        {
            Assert.Contains("origClientOrderId=ct-test-1", request.RequestUri!.Query, StringComparison.Ordinal);
            AssertSignatureIsValid(request, "test-api-secret");
        });
    }

    [Fact]
    public async Task FinancialOrderActivitiesExposeOnlyBoundedOperationTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TradingTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using var handler = new RecordingHandler(
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"NEW\",\"transactTime\":1789905600000}",
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"CANCELED\",\"transactTime\":1789905600000}",
            "{\"serverTime\":1789905600000}",
            "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-test-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"CANCELED\",\"transactTime\":1789905600000}");
        var client = CreateClient(handler, enableExecution: true);

        _ = await client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken);
        _ = await client.QueryByClientOrderIdAsync("BTCUSDT", "ct-test-1", cancellationToken: TestContext.Current.CancellationToken);
        _ = await client.CancelAsync("BTCUSDT", "ct-test-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["submit", "query", "cancel"], activities
            .Select(activity => activity.GetTagItem("crypto.operation")?.ToString() ?? string.Empty)
            .ToArray());
        Assert.All(activities, activity =>
        {
            Assert.Equal("binance", activity.GetTagItem("crypto.exchange"));
            Assert.DoesNotContain(activity.Tags, tag => tag.Key is "symbol" or "clientOrderId" or "client_order_id");
            Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        });
    }

    [Fact]
    public async Task TransportTimeoutIsAmbiguousAndIsNeverRetried()
    {
        using var handler = new RecordingHandler("{\"serverTime\":1789905600000}", throwTransportError: true);
        var client = CreateClient(handler, enableExecution: true);

        var exception = await Assert.ThrowsAsync<BinanceOrderSubmissionAmbiguousException>(() => client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("ct-test-1", exception.ClientOrderId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownServerFailureIsAmbiguousAndIsNeverRetried()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.BadGateway,
            "{\"serverTime\":1789905600000}",
            "{}");
        var client = CreateClient(handler, enableExecution: true);

        await Assert.ThrowsAsync<BinanceOrderSubmissionAmbiguousException>(() => client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DefinitiveClientRejectionIsTypedAndIsNeverRetried()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            "{\"serverTime\":1789905600000}",
            "{\"code\":-1013,\"msg\":\"Filter failure: LOT_SIZE\"}");
        var client = CreateClient(handler, enableExecution: true);

        var exception = await Assert.ThrowsAsync<BinanceOrderRejectedException>(() => client.SubmitAsync(Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("ct-test-1", exception.ClientOrderId);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void OrderInputsAndEnvironmentAreBounded()
    {
        Assert.Throws<ArgumentException>(() => new BinanceSpotOrderRequest("BTC-USDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Market, 1m, "ct-test-1"));
        Assert.Throws<ArgumentException>(() => new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Market, 1m, "bad id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Market, 0m, "ct-test-1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 1m, "ct-test-1"));

        var options = new BinanceSpotOrderClientOptions
        {
            Environment = BinanceEnvironment.Demo,
            ApiKey = "key",
            ApiSecret = "secret",
            EnableOrderExecution = true
        };
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        Assert.Throws<InvalidOperationException>(() => new BinanceSpotOrderClient(client, options));
    }

    private static BinanceSpotOrderRequest Request() => new(
        "btcusdt",
        BinanceSpotOrderSide.Buy,
        BinanceSpotOrderType.Limit,
        0.01m,
        "ct-test-1",
        100.50m);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The test owns the handler lifetime for the duration of the client assertions.")]
    private static BinanceSpotOrderClient CreateClient(RecordingHandler handler, bool enableExecution) =>
        new(
            new HttpClient(handler),
            new BinanceSpotOrderClientOptions
            {
                RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
                ApiKey = "test-api-key",
                ApiSecret = "test-api-secret",
                EnableOrderExecution = enableExecution
            },
            new FixedTimeProvider(FixedNow));

    private static void AssertSignatureIsValid(HttpRequestMessage request, string secret)
    {
        var parts = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var signature = parts.Single(part => part.StartsWith("signature=", StringComparison.Ordinal))["signature=".Length..];
        var unsigned = string.Join('&', parts.Where(part => !part.StartsWith("signature=", StringComparison.Ordinal)));
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(unsigned))).ToUpperInvariant();
        Assert.Equal(expected, signature.ToUpperInvariant());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        private bool ThrowTransportError { get; init; }
        private HttpStatusCode? ServerFailureStatus { get; init; }

        public RecordingHandler(bool throwTransportError = false)
            : this(Array.Empty<string>())
        {
            ThrowTransportError = throwTransportError;
        }

        public RecordingHandler(string firstResponse, bool throwTransportError)
            : this(new[] { firstResponse })
        {
            ThrowTransportError = throwTransportError;
        }

        public RecordingHandler(HttpStatusCode statusCode, params string[] responses)
            : this(responses)
        {
            ServerFailureStatus = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (ThrowTransportError && request.RequestUri!.AbsolutePath.EndsWith("/v3/order", StringComparison.Ordinal))
            {
                throw new HttpRequestException("simulated transport failure");
            }

            var response = responses.Length is 0
                ? "{}"
                : responses[Math.Min(Requests.Count - 1, responses.Length - 1)];
            var statusCode = ServerFailureStatus is { } failure && request.RequestUri!.AbsolutePath.EndsWith("/v3/order", StringComparison.Ordinal)
                ? failure
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
