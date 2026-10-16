using CryptoTrading.Configuration;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

internal static class WorkerExecutionRegistration
{
    public static IServiceCollection AddOutboxExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IOutboxWakeupSignal, OutboxWakeupSignal>();

        services.AddSingleton<IOutboxMessageExecutor>(serviceProvider =>
        {
            var runtimeOptions = serviceProvider.GetRequiredService<IOptions<WorkerRuntimeOptions>>();
            if (string.Equals(runtimeOptions.Value.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase))
            {
                return new TestnetOutboxMessageExecutor(
                    serviceProvider.GetRequiredService<IBinanceSpotOrderClient>(),
                    serviceProvider.GetRequiredService<IBinanceSpotAccountClient>(),
                    runtimeOptions,
                    serviceProvider.GetRequiredService<IOptions<TradingOptions>>(),
                    serviceProvider.GetRequiredService<OrderProjectionService>(),
                    serviceProvider.GetRequiredService<ILogger<TestnetOutboxMessageExecutor>>(),
                    serviceProvider.GetService<IOptions<BinanceAuthenticatedClientOptions>>(),
                    serviceProvider.GetRequiredService<IExecutionSubmissionGuard>(),
                    serviceProvider.GetRequiredService<IDurableSubmissionStore>());
            }

            return new PaperOutboxMessageExecutor(
                serviceProvider.GetRequiredService<IPaperExchange>(),
                runtimeOptions,
                serviceProvider.GetRequiredService<ILogger<PaperOutboxMessageExecutor>>());
        });

        if (string.Equals(configuration["WORKER_EXECUTION_MODE"], "TESTNET", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IOutboxMessageExecutor>(serviceProvider => new TestnetCancelOutboxMessageExecutor(
                serviceProvider.GetRequiredService<IBinanceSpotOrderClient>(),
                serviceProvider.GetRequiredService<IOptions<WorkerRuntimeOptions>>(),
                serviceProvider.GetRequiredService<IOptions<TradingOptions>>(),
                serviceProvider.GetRequiredService<ILocalOpenOrderStore>(),
                serviceProvider.GetRequiredService<OrderProjectionService>(),
                serviceProvider.GetRequiredService<IOrderCancellationStore>(),
                serviceProvider.GetRequiredService<ILogger<TestnetCancelOutboxMessageExecutor>>()));
        }

        if (!string.Equals(configuration["WORKER_EXECUTION_MODE"], "TESTNET", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        // PAPER intentionally does not require credentials. TESTNET does:
        // validate TradingOptions before the adapter can be resolved.
        _ = services.AddOptions<TradingOptions>().ValidateOnStart();
        _ = services.AddSingleton<IExecutionSubmissionGuard, ExecutionSubmissionGuard>();
        _ = services.AddSingleton<IBinanceClockDriftEstimator, BinanceClockDriftEstimator>();
        _ = services.AddHttpClient("BinanceSpotOrder", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/0.1");
        });
        _ = services.AddBinanceSpotMarketClient();
        _ = services.AddSingleton<IBinanceSymbolTradingRulesProvider>(serviceProvider =>
            new BinanceSymbolTradingRulesCache(
                serviceProvider.GetRequiredService<IBinanceSpotMarketClient>(),
                TimeSpan.FromMinutes(5)));
        _ = services.AddSingleton<BinanceSpotOrderClient>(serviceProvider =>
        {
            var trading = serviceProvider.GetRequiredService<IOptions<TradingOptions>>().Value;
            var orderOptions = new BinanceSpotOrderClientOptions
            {
                Environment = BinanceEnvironment.Testnet,
                RestBaseAddress = trading.Binance.RestBaseUrl,
                ApiKey = trading.Binance.ApiKey,
                ApiSecret = trading.Binance.ApiSecret,
                EnableOrderExecution = true,
                ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
            };
            return new BinanceSpotOrderClient(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("BinanceSpotOrder"),
                orderOptions,
                clock: serviceProvider.GetRequiredService<IBinanceClockDriftEstimator>());
        });
        _ = services.AddSingleton<IBinanceSpotOrderClient>(serviceProvider =>
            new BinancePreTradeValidatedOrderClient(
                serviceProvider.GetRequiredService<BinanceSpotOrderClient>(),
                serviceProvider.GetRequiredService<IBinanceSymbolTradingRulesProvider>()));

        return services;
    }
}
