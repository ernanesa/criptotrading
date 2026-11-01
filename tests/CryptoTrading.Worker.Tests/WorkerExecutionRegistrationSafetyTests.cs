using CryptoTrading.Exchange.Binance;
using CryptoTrading.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class WorkerExecutionRegistrationSafetyTests
{
    [Fact]
    public void TestnetRegistrationDoesNotExposeConcreteMutableClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WORKER_EXECUTION_MODE"] = "TESTNET" }).Build();
        var services = new ServiceCollection();

        services.AddOutboxExecution(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(BinanceSpotOrderClient));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBinanceSpotOrderClient));
    }

    [Fact]
    public void TestnetResolvesOnlyTheFailClosedOrderBoundary()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WORKER_EXECUTION_MODE"] = "TESTNET" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<TradingOptions>().Configure(options =>
        {
            options.Binance.RestBaseUrl = new Uri("https://testnet.binance.vision/api/");
            options.Binance.ApiKey = "fixture-key";
            options.Binance.ApiSecret = "fixture-secret";
        });
        services.AddSingleton<IBinanceSpotAccountClient>(new OfflineAccount());
        services.AddOutboxExecution(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<BinanceSpotOrderClient>());
        Assert.IsType<BinancePreTradeValidatedOrderClient>(provider.GetRequiredService<IBinanceSpotOrderClient>());
    }

    [Fact]
    public async Task OrderFactorySharesTheInjectedClockWithItsDriftEstimator()
    {
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var directory = Path.Combine(Path.GetTempPath(), "ct-order-clock-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WORKER_EXECUTION_MODE"] = "TESTNET" }).Build();
        using var handler = new ClockHandler(at);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FixedClock(at));
            services.AddOptions<TradingOptions>().Configure(options =>
            {
                options.Binance.RestBaseUrl = new Uri("https://testnet.binance.vision/api/");
                options.Binance.ApiKey = "synthetic-fixture-key";
                options.Binance.ApiSecret = "synthetic-fixture-secret";
            });
            BinanceRateLimitDefinition[] limits = [new("REQUEST_WEIGHT", "MINUTE", 1, 1000), new("RAW_REQUESTS", "MINUTE", 1, 1000), new("ORDERS", "MINUTE", 1, 1000)];
            services.AddSingleton(new BinanceRequestBudget(directory, initialLimits: limits));
            services.AddSingleton<IBinanceSpotAccountClient>(new OfflineAccount());
            services.AddOutboxExecution(configuration);
            services.AddHttpClient("BinanceSpotOrder").ConfigurePrimaryHttpMessageHandler(() => handler);
            using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IBinanceSpotOrderClient>();
            await Assert.ThrowsAsync<BinanceApiException>(() => client.QueryByClientOrderIdAsync("BTCUSDT", "ct-clock", TestContext.Current.CancellationToken));
            Assert.Contains("timestamp=" + at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), handler.OrderQuery!, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed class ClockHandler(DateTimeOffset at) : HttpMessageHandler
    {
        public string? OrderQuery { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/time", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"serverTime\":" + at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", Encoding.UTF8, "application/json") });
            OrderQuery = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("{\"code\":-2013,\"msg\":\"synthetic missing order\"}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class OfflineAccount : IBinanceSpotAccountClient
    {
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
