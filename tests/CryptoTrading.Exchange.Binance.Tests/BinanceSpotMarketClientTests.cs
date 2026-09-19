using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceSpotMarketClientTests
{
    [Fact]
    public async Task GetServerTimeDeserializesOfficialPayload()
    {
        using var httpClient = CreateHttpClient("{\"serverTime\":1720000000123}");
        var client = new BinanceSpotMarketClient(httpClient);
        var result = await client.GetServerTimeAsync();

        Assert.Equal(1720000000123, result.ServerTime);
    }

    [Fact]
    public async Task ExchangeInfoNormalizesSymbol()
    {
        using var handler = new RecordingHandler("{\"timezone\":\"UTC\",\"serverTime\":1,\"symbols\":[{\"symbol\":\"BTCUSDT\",\"status\":\"TRADING\",\"baseAsset\":\"BTC\",\"quoteAsset\":\"USDT\",\"baseAssetPrecision\":8,\"quoteAssetPrecision\":8}]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/") };
        var client = new BinanceSpotMarketClient(httpClient);

        var result = await client.GetExchangeInfoAsync(" btcusdt ");

        Assert.Single(result.Symbols);
        Assert.Equal("BTCUSDT", result.Symbols[0].Symbol);
        Assert.Equal("/api/exchangeInfo?symbol=BTCUSDT", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task NonSuccessDoesNotExposeResponseBody()
    {
        using var httpClient = CreateHttpClient("{\"code\":-1003,\"msg\":\"secret-like response\"}", HttpStatusCode.TooManyRequests);
        var client = new BinanceSpotMarketClient(httpClient);

        var exception = await Assert.ThrowsAsync<BinanceApiException>(() => client.PingAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.DoesNotContain("secret-like", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BTC-USDT")]
    [InlineData("BTC/USDT")]
    [InlineData("123456789012345678901")]
    public async Task InvalidSymbolIsRejectedBeforeNetwork(string symbol)
    {
        using var httpClient = CreateHttpClient("{}");
        var client = new BinanceSpotMarketClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetExchangeInfoAsync(symbol));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "HttpClient owns and disposes its message handler in the test fixture.")]
    private static HttpClient CreateHttpClient(string payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var httpClient = new HttpClient(new RecordingHandler(payload, statusCode))
        {
            BaseAddress = new Uri("https://test.invalid/api/"),
            DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("CryptoTrading", "test") } }
        };
        return httpClient;
    }

    private sealed class RecordingHandler(string payload, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload),
                RequestMessage = request
            });
        }
    }
}
