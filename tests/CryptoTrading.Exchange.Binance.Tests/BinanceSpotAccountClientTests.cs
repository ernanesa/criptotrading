using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceSpotAccountClientTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AccountRequestSynchronizesClockAndSignsOnlyTheCanonicalQuery()
    {
        using var handler = new RecordingHandler(
            ("time", "{\"serverTime\":1789819200000}"),
            ("v3/account", "{\"makerCommission\":15,\"takerCommission\":15,\"buyerCommission\":0,\"sellerCommission\":0,\"canTrade\":true,\"canWithdraw\":false,\"canDeposit\":true,\"updateTime\":1,\"accountType\":\"SPOT\",\"balances\":[{\"asset\":\"USDT\",\"free\":\"100.00\",\"locked\":\"0.00\"}]}")
        );
        var client = CreateClient(handler);

        var account = await client.GetAccountAsync();

        Assert.True(account.CanTrade);
        Assert.Single(account.Balances);
        Assert.Equal("USDT", account.Balances[0].Asset);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("time", handler.Requests[0].RequestUri!.AbsolutePath.Trim('/').Split('/').Last());

        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("test-api-key", request.Headers.GetValues("X-MBX-APIKEY").Single());
        Assert.DoesNotContain("test-api-secret", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("recvWindow=5000", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("timestamp=1789819200000", request.RequestUri.Query, StringComparison.Ordinal);

        var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var unsignedQuery = string.Join('&', query.Where(part => !part.StartsWith("signature=", StringComparison.Ordinal)));
        var signature = query.Single(part => part.StartsWith("signature=", StringComparison.Ordinal))["signature=".Length..];
        Assert.Equal(Sign(unsignedQuery, "test-api-secret"), signature);
    }

    [Fact]
    public async Task OpenOrdersAndTradesUseReadOnlyGetEndpointsAndNormalizeFilters()
    {
        using var handler = new RecordingHandler(
            ("time", "{\"serverTime\":1789819200000}"),
            ("v3/openOrders", "[{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"local-1\",\"price\":\"100\",\"origQty\":\"0.01\",\"executedQty\":\"0\",\"cummulativeQuoteQty\":\"0\",\"status\":\"NEW\",\"time\":1,\"updateTime\":1,\"isWorking\":true,\"type\":\"LIMIT\",\"side\":\"BUY\"}]"),
            ("v3/myTrades", "[{\"symbol\":\"BTCUSDT\",\"id\":7,\"orderId\":42,\"price\":\"100\",\"qty\":\"0.01\",\"quoteQty\":\"1\",\"commission\":\"0.001\",\"commissionAsset\":\"USDT\",\"time\":1,\"isBuyer\":true,\"isMaker\":false,\"isBestMatch\":true}]")
        );
        var client = CreateClient(handler);

        var orders = await client.GetOpenOrdersAsync(" btcusdt ");
        var trades = await client.GetMyTradesAsync("btcusdt", new BinanceTradeQuery(FromId: 4, Limit: 25));

        Assert.Single(orders);
        Assert.Single(trades);
        Assert.Equal(4, handler.Requests.Count); // one public time sync per signed read
        Assert.All(handler.Requests.Where(request => request.RequestUri!.AbsolutePath.Contains("v3/", StringComparison.Ordinal)), request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(request.RequestUri!.Query.Contains("signature=", StringComparison.Ordinal));
        });
        Assert.Contains("symbol=BTCUSDT", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("fromId=4", handler.Requests[3].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("limit=25", handler.Requests[3].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcessiveClockDriftFailsClosedBeforeAuthenticatedRequest()
    {
        using var handler = new RecordingHandler(("time", "{\"serverTime\":1789819260000}"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<BinanceClockDriftException>(() => client.GetAccountAsync());

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains("v3/", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidReadOnlyOptionsAreRejected()
    {
        var options = new BinanceAuthenticatedClientOptions { ApiKey = "key", ApiSecret = "secret", RecvWindowMilliseconds = 0 };
        using var handler = new RecordingHandler(("time", "{}"));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://testnet.binance.vision/api/")
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new BinanceSpotAccountClient(httpClient, options));
    }

    [Fact]
    public void AuthenticatedOptionsRejectNonTestnetEndpoint()
    {
        var options = new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://api.binance.com/api/"),
            ApiKey = "key",
            ApiSecret = "secret"
        };

        using var handler = new RecordingHandler(("time", "{}"));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = options.RestBaseAddress
        };

        Assert.Throws<InvalidOperationException>(() => new BinanceSpotAccountClient(httpClient, options));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The test handler owns the HttpClient lifetime for the duration of each test.")]
    private static BinanceSpotAccountClient CreateClient(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binance.vision/api/") };
        var options = new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            ClockDriftTolerance = TimeSpan.FromSeconds(5)
        };
        return new BinanceSpotAccountClient(httpClient, options, timeProvider: new FixedTimeProvider(FixedNow));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Binance signatures are conventionally represented as lowercase hexadecimal.")]
    private static string Sign(string query, string secret)
    {
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private sealed class FixedTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class RecordingHandler(params (string Endpoint, string Payload)[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var endpoint = request.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
            var response = responses.FirstOrDefault(candidate =>
                endpoint.Equals(candidate.Endpoint, StringComparison.Ordinal) ||
                endpoint.EndsWith('/' + candidate.Endpoint, StringComparison.Ordinal));
            if (response == default)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.Payload),
                RequestMessage = request
            });
        }
    }
}
