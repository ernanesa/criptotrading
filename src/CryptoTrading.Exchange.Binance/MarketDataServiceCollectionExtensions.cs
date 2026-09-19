using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Exchange.Binance;

public static class MarketDataServiceCollectionExtensions
{
    public static IServiceCollection AddBinanceSpotMarketDataClient(
        this IServiceCollection services,
        Action<BinanceMarketDataOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BinanceMarketDataOptions>()
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
            }, "Binance market data options are invalid.")
            .ValidateOnStart();

        services.AddSingleton<IBinanceSpotMarketDataClient>(serviceProvider =>
            new BinanceSpotMarketDataClient(serviceProvider.GetRequiredService<IOptions<BinanceMarketDataOptions>>().Value));

        return services;
    }
}
