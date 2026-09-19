using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Exchange.Binance;

public static class ServiceCollectionExtensions
{
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
}
