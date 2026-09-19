namespace CryptoTrading.Exchange.Binance;

public sealed class BinanceMarketDataOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;

    public Uri? WebSocketBaseAddress { get; set; }

    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxReconnectAttempts { get; set; }

    internal Uri GetWebSocketBaseAddress()
    {
        var defaultAddress = Environment switch
        {
            BinanceEnvironment.Testnet => new Uri("wss://stream.testnet.binance.vision/stream"),
            BinanceEnvironment.Demo => new Uri("wss://demo-stream.binance.com/stream"),
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };

        var address = WebSocketBaseAddress ?? defaultAddress;
        if (!address.IsAbsoluteUri || address.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException("Binance market data address must be an absolute WebSocket URI.");
        }

        return address;
    }

    internal void Validate()
    {
        _ = GetWebSocketBaseAddress();
        if (InitialReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectDelay), "Initial reconnect delay must be greater than zero.");
        }

        if (MaxReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectDelay), "Maximum reconnect delay must be at least the initial delay.");
        }

        if (MaxReconnectAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts), "Maximum reconnect attempts cannot be negative.");
        }
    }
}
