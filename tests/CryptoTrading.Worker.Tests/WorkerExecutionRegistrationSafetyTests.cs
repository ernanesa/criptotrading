using CryptoTrading.Exchange.Binance;
using CryptoTrading.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class OfflineAccount : IBinanceSpotAccountClient
    {
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
