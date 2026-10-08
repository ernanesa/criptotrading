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
        var result = await client.GetServerTimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1720000000123, result.ServerTime);
    }

    [Fact]
    public async Task GetKlinesParsesPublicHistoricalRowsAndBounds()
    {
        using var handler = new RecordingHandler("[[1720000000000,\"60000.00\",\"61000.00\",\"59000.00\",\"60500.00\",\"12.5\",1720086399999,\"756250.00\",100,\"605000.00\",10,\"0\"]]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/") };
        var client = new BinanceSpotMarketClient(httpClient);

        var result = await client.GetKlinesAsync(
            " btcusdt ",
            "1d",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(2),
            100, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(60500m, result[0].Close);
        Assert.Equal("/api/v3/klines?symbol=BTCUSDT&interval=1d&limit=100&startTime=0&endTime=172800000", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetBookTickerParsesStringDecimalsAndStampsObservation()
    {
        using var handler = new RecordingHandler(
            "{\"symbol\":\"BTCUSDT\",\"bidPrice\":\"60000.00\",\"bidQty\":\"0.25\",\"askPrice\":\"60001.00\",\"askQty\":\"0.10\"}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/") };
        var before = DateTimeOffset.UtcNow;
        var client = new BinanceSpotMarketClient(httpClient);

        var result = await client.GetBookTickerAsync(" btcusdt ", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.Equal(60000m, result.BidPrice);
        Assert.Equal(0.25m, result.BidQuantity);
        Assert.Equal(60001m, result.AskPrice);
        Assert.Equal(0.10m, result.AskQuantity);
        Assert.InRange(result.ObservedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal("/api/v3/ticker/bookTicker?symbol=BTCUSDT", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task AggregateTradesUseNonFutureEndTimeAndParseMatchingEngineEvidence()
    {
        using var handler = new RecordingHandler(
            "[{\"a\":17,\"p\":\"600.12\",\"q\":\"1.5\",\"f\":91,\"l\":92,\"T\":1720000000000,\"m\":true,\"M\":true}]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/") };
        var client = new BinanceSpotMarketClient(httpClient);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(1_720_000_000_001);

        var result = await client.GetAggregateTradesAsync("bnbusdt", end, 1000, cancellationToken: TestContext.Current.CancellationToken);

        var trade = Assert.Single(result);
        Assert.Equal(17, trade.AggregateTradeId);
        Assert.Equal(600.12m, trade.Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_720_000_000_000), trade.OccurredAtUtc);
        Assert.Equal("/api/v3/aggTrades?symbol=BNBUSDT&limit=1000&endTime=1720000000001", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("2d")]
    [InlineData("1D")]
    public async Task GetKlinesRejectsInvalidRequestBeforeNetwork(string interval)
    {
        using var httpClient = CreateHttpClient("[]");
        var client = new BinanceSpotMarketClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetKlinesAsync("BTCUSDT", interval, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetKlinesAsync("BTCUSDT", "1d", limit: 1001, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExchangeInfoNormalizesSymbol()
    {
        using var handler = new RecordingHandler("{\"timezone\":\"UTC\",\"serverTime\":1,\"symbols\":[{\"symbol\":\"BTCUSDT\",\"status\":\"TRADING\",\"baseAsset\":\"BTC\",\"quoteAsset\":\"USDT\",\"baseAssetPrecision\":8,\"quoteAssetPrecision\":8}]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/") };
        var client = new BinanceSpotMarketClient(httpClient);

        var result = await client.GetExchangeInfoAsync(" btcusdt ", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Symbols);
        Assert.Equal("BTCUSDT", result.Symbols[0].Symbol);
        Assert.Equal("/api/v3/exchangeInfo?symbol=BTCUSDT", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetTradingRulesParsesFiltersFromExchangeInfo()
    {
        using var httpClient = CreateHttpClient(
            "{\"timezone\":\"UTC\",\"serverTime\":1,\"symbols\":[{\"symbol\":\"BTCUSDT\",\"status\":\"TRADING\",\"baseAsset\":\"BTC\",\"quoteAsset\":\"USDT\",\"baseAssetPrecision\":8,\"quoteAssetPrecision\":8,\"filters\":[{\"filterType\":\"PRICE_FILTER\",\"minPrice\":\"0.01\",\"maxPrice\":\"100000\",\"tickSize\":\"0.01\"},{\"filterType\":\"LOT_SIZE\",\"minQty\":\"0.0001\",\"maxQty\":\"9000\",\"stepSize\":\"0.0001\"},{\"filterType\":\"MIN_NOTIONAL\",\"minNotional\":\"10\"}]}]}");
        var client = new BinanceSpotMarketClient(httpClient);

        var rules = await client.GetTradingRulesAsync("btcusdt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.01m, rules.PriceTickSize);
        Assert.Equal(0.0001m, rules.QuantityStepSize);
        Assert.Equal(10m, rules.MinimumNotional);
    }

    [Fact]
    public async Task NonSuccessDoesNotExposeResponseBody()
    {
        using var httpClient = CreateHttpClient("{\"code\":-1003,\"msg\":\"secret-like response\"}", HttpStatusCode.TooManyRequests);
        var client = new BinanceSpotMarketClient(httpClient);

        var exception = await Assert.ThrowsAsync<BinanceApiException>(() => client.PingAsync(cancellationToken: TestContext.Current.CancellationToken));

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

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetExchangeInfoAsync(symbol, cancellationToken: TestContext.Current.CancellationToken));
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
