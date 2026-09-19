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

    internal Uri GetRestBaseAddress()
    {
        var defaultAddress = Environment switch
        {
            BinanceEnvironment.Testnet => new Uri("https://testnet.binance.vision/api/"),
            BinanceEnvironment.Demo => new Uri("https://demo-api.binance.com/api/"),
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };

        var address = RestBaseAddress ?? defaultAddress;
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Binance REST base address must be an absolute HTTP(S) URI.");
        }

        return address.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? address
            : new Uri(address.AbsoluteUri + "/");
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
