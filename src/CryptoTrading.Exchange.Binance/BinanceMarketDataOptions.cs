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
        var expectedHost = Environment switch
        {
            BinanceEnvironment.Testnet => "stream.testnet.binance.vision",
            BinanceEnvironment.Demo => "demo-stream.binance.com",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };
        return BinanceEndpointPolicy.ValidateWebSocket(address, expectedHost, "/stream", "market data address");
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
