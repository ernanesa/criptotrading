namespace CryptoTrading.Exchange.Binance;

public enum BinanceEnvironment
{
    Testnet,
    Demo
}

public sealed class BinanceClientOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;

    public Uri? RestBaseAddress { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional TTL for reusing a recently fetched GET /api/v3/klines response
    /// for the same (symbol, interval, limit) when startTimeUtc and endTimeUtc are null.
    /// Defaults to zero (always fetch fresh).
    /// </summary>
    public TimeSpan KlineCacheDuration { get; set; } = TimeSpan.Zero;

    internal Uri GetRestBaseAddress()
    {
        var defaultAddress = Environment switch
        {
            BinanceEnvironment.Testnet => new Uri("https://testnet.binance.vision/api/"),
            BinanceEnvironment.Demo => new Uri("https://demo-api.binance.com/api/"),
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };

        var address = RestBaseAddress ?? defaultAddress;
        var expectedHost = Environment switch
        {
            BinanceEnvironment.Testnet => "testnet.binance.vision",
            BinanceEnvironment.Demo => "demo-api.binance.com",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };
        return BinanceEndpointPolicy.ValidateRest(address, expectedHost, "/api", $"{Environment} REST base address");
    }

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Request timeout must be greater than zero and at most two minutes.");
        }

        _ = GetRestBaseAddress();
    }
}
