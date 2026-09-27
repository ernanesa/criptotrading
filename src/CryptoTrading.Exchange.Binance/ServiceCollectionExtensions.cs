using CryptoTrading.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Exchange.Binance;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBinanceSpotAccountClientFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddBinanceSpotAccountClient(options =>
        {
            options.ApiKey = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE");
            options.ApiSecret = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE");
            options.RestBaseAddress = Uri.TryCreate(configuration["BINANCE_REST_BASE_URL"], UriKind.Absolute, out var restBaseAddress)
                ? restBaseAddress
                : null;
            options.Environment = string.Equals(configuration["TRADING_ENVIRONMENT"], "DEMO", StringComparison.OrdinalIgnoreCase)
                ? BinanceEnvironment.Demo
                : BinanceEnvironment.Testnet;
            options.AllowTestnetWithdrawCapability = options.Environment == BinanceEnvironment.Testnet;
        });
    }

    public static IServiceCollection AddBinanceSpotMarketClient(
        this IServiceCollection services,
        Action<BinanceClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BinanceClientOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }, "Binance client options are invalid.")
            .ValidateOnStart();

        services.AddHttpClient<IBinanceSpotMarketClient, BinanceSpotMarketClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BinanceClientOptions>>().Value;
            client.BaseAddress = options.GetRestBaseAddress();
            client.Timeout = options.RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/0.1");
        });

        return services;
    }

    public static IServiceCollection AddBinanceSpotAccountClient(
        this IServiceCollection services,
        Action<BinanceAuthenticatedClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBinanceClockDriftEstimator, BinanceClockDriftEstimator>();
        services.AddOptions<BinanceAuthenticatedClientOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }, "Binance authenticated client options are invalid.")
            .ValidateOnStart();

        services.AddHttpClient<IBinanceSpotAccountClient, BinanceSpotAccountClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BinanceAuthenticatedClientOptions>>().Value;
            client.BaseAddress = options.GetRestBaseAddress();
            client.Timeout = options.RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/0.1");
        });

        return services;
    }

    public static IServiceCollection AddBinanceUserDataStreamLifecycleClient(
        this IServiceCollection services,
        Action<BinanceUserDataStreamRestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BinanceUserDataStreamRestOptions>()
            .Configure(options => configure?.Invoke(options))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }, "Binance user-data lifecycle options are invalid.")
            .ValidateOnStart();

        services.AddHttpClient<IBinanceUserDataStreamLifecycle, BinanceUserDataStreamLifecycleClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BinanceUserDataStreamRestOptions>>().Value;
            client.BaseAddress = options.GetRestBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/0.1");
        });

        return services;
    }
}
