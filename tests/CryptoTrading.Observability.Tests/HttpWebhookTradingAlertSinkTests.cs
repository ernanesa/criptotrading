using System.Net;
using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class HttpWebhookTradingAlertSinkTests
{
    [Fact]
    public async Task DisabledWebhookFailsClosedWithoutMakingARequest()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var sink = new HttpWebhookTradingAlertSink(client, new TradingAlertWebhookOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sink.DeliverAsync(CreateAlert()).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnabledWebhookUsesAllowlistedHttpsEndpointAndRedactedEnvelope()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        using var sink = new HttpWebhookTradingAlertSink(client, EnabledOptions());

        await sink.DeliverAsync(CreateAlert()).ConfigureAwait(true);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://alerts.example.test/hook", request.RequestUri?.AbsoluteUri);
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(
            "authorization",
            string.Join("\n", request.Headers.SelectMany(header => header.Value)),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation:BTCUSDT", handler.Bodies.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", handler.Bodies.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://alerts.example.test/hook")]
    [InlineData("https://alerts.example.test:8443/hook")]
    [InlineData("https://alerts.example.test/hook?token=secret")]
    public void EnabledWebhookRejectsUnsafeEndpoint(string endpoint)
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        Assert.Throws<InvalidOperationException>(() => new HttpWebhookTradingAlertSink(
            client,
            new TradingAlertWebhookOptions
            {
                Enabled = true,
                Endpoint = new Uri(endpoint),
                AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alerts.example.test" }
            }));
    }

    [Fact]
    public async Task NonSuccessResponseIsReturnedAsDeliveryFailure()
    {
        using var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        using var sink = new HttpWebhookTradingAlertSink(client, EnabledOptions());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await sink.DeliverAsync(CreateAlert()).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
    }

    private static TradingAlertWebhookOptions EnabledOptions() => new()
    {
        Enabled = true,
        Endpoint = new Uri("https://alerts.example.test/hook"),
        AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alerts.example.test" },
        RequestTimeout = TimeSpan.FromSeconds(1)
    };

    private static TradingAlert CreateAlert() => new(
        "TESTNET",
        "BINANCE",
        "BTCUSDT/account",
        new DateTimeOffset(2026, 9, 19, 15, 0, 0, TimeSpan.Zero),
        "corr-webhook",
        TradingAlertSeverity.Critical,
        "ops",
        "reconcile before new entry",
        "docs/runbook-local.md#reconciliation",
        "reconciliation:BTCUSDT");

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode = statusCode;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(statusCode) { RequestMessage = request };
        }
    }
}
