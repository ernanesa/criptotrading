# Binance public market data

`CryptoTrading.Exchange.Binance` now exposes `IBinanceSpotMarketDataClient` for
public Spot streams. It supports combined `trade` and `kline` subscriptions,
maps payloads to typed events, ignores malformed payloads, and reconnects with
cancelable bounded exponential backoff.

The slice is deliberately public/read-only: it does not authenticate, read
account data, submit orders, or replace the future user-data stream adapter.
The endpoint defaults to the Binance Spot Testnet stream and can be overridden
with `BinanceMarketDataOptions.WebSocketBaseAddress`.

```csharp
services.AddBinanceSpotMarketDataClient(options =>
{
    options.Environment = BinanceEnvironment.Testnet;
    options.MaxReconnectAttempts = 0; // unlimited until cancellation
});
```

Consume with cancellation and keep the resulting event timestamp/stream state
in the downstream freshness and persistence components:

```csharp
await foreach (var marketEvent in client.ReadAsync(
    [new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade),
     new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Kline, "1m")],
    stoppingToken))
{
    // Route to the market-data journal/projection; do not place orders here.
}
```

The transport is injectable through `IBinanceWebSocketTransport`, so parser,
reconnect and cancellation behavior can be tested without treating fixtures as
external integration evidence.
