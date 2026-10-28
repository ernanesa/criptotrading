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

## Bounded local order book

The Binance adapter also exposes `BinanceLocalOrderBook` for public diff-depth
streams. It is intentionally a stateful, read-only projection: it does not
authenticate, submit orders, or fetch a snapshot by itself.

The synchronization contract is:

1. Fetch a REST depth snapshot and call `ApplySnapshot`.
2. Buffer WebSocket `depthUpdate` events while the snapshot is being fetched,
   as required by the [Binance synchronization procedure](https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-streams.md#how-to-manage-a-local-order-book-correctly).
   **Not implemented in the current Worker:** it discards deltas while the
   book is invalid, which can require repeated resynchronization.
3. Apply the first buffered event only when it bridges `lastUpdateId + 1`
   (`U <= lastUpdateId + 1 <= u`).
4. The current implementation requires every following event to be contiguous
   (`U == previous u + 1`). When `pu` is present after the initial bridge, it
   must also equal the local last update ID. This is stricter than the official
   procedure for overlapping updates and can trigger conservative resyncs.
5. If a gap, overlap/out-of-order event, invalid level, or symbol mismatch is
   detected, the book becomes `Invalid`, the event is quarantined, and
   `ResyncRequired` remains true until a fresh snapshot is applied.

`BinanceLocalOrderBook` is bounded in two dimensions. `maxLevels` retains only
the best bid and ask levels, while `quarantineCapacity` bounds diagnostic
records. Duplicate events (`u <= lastUpdateId`) are quarantined without
invalidating an otherwise healthy book; they never mutate the projection.
`Healthy` immediately after a snapshot does not establish a bridge: consumers
must require `IsSynchronized`. Until then, and after invalidation, `Bids`,
`Asks` and depth-derived signals are unavailable. The Worker invalidates
cached market state when the adapter reports a stream disconnection; a complete
buffered reconstruction and the treatment of decisions already in flight remain
unverified.

```csharp
var book = new BinanceLocalOrderBook("BTCUSDT", maxLevels: 1000);
var snapshotResult = book.ApplySnapshot(snapshot);

if (snapshotResult.ResyncRequired)
{
    // Fetch a new snapshot and retry; do not publish this book downstream.
}

var result = book.ApplyDelta(depthUpdate);
if (result.ResyncRequired)
{
    // Stop consuming this projection, request a REST snapshot, then apply it.
}
```

The projection is not a substitute for the exchange adapter's snapshot fetch
or for persistence. Downstream consumers must not generate a trading decision
from an `Invalid` book, and should record the resynchronization signal as a
market-data integrity event.

The transport is injectable through `IBinanceWebSocketTransport`, so parser,
reconnect and cancellation behavior can be tested without treating fixtures as
external integration evidence.

## User Data Stream (read-only event ingestion)

The Worker uses `BinanceUserDataStreamApiClient`, the modern Spot WebSocket API
with HMAC `userDataStream.subscribe.signature` over the allowlisted Testnet or
Demo endpoint. It parses nested `executionReport` and
`outboundAccountPosition` events, preserves `subscriptionId`, recognizes
`eventStreamTerminated`/`serverShutdown`, quarantines malformed events and
reconnects with bounded backoff. Each connection receives a new `sessionId` and
monotonic `sessionGeneration`; the durable account-stream envelope retains both
plus `subscriptionId`, including the synthetic `connectionEstablished` marker.
Sessions are rotated proactively after the configured 23-hour default (never
later than Binance's 24-hour WebSocket session limit), then resubscribed and
reconciled before downstream processing continues.
The legacy `listenKey` adapter below remains
available only as a compatibility/test fixture and is not wired into the
Worker.

Enable the authenticated consumer only in a local process with
`WORKER_ACCOUNT_STREAM_ENABLED=true`, `WORKER_RECONCILIATION_ENABLED=true`,
`WORKER_PERSISTENCE_ENABLED=true`, `WORKER_APPLY_MIGRATIONS=true` and an
approved active `WORKER_RISK_POLICY_REQUIRED=true`. In PAPER, keep
`WORKER_NO_TRADE=true`; in Testnet execution, `WORKER_NO_TRADE=false` is
allowed only with every explicit Testnet gate. The service starts after migrations and the initial
reconciliation, persists an append-only event
envelope, and enters SafeMode on stream termination or storage failure.
The same read-only reconciliation also runs every 15 minutes by default
(`WORKER_RECONCILIATION_INTERVAL_MINUTES`) and writes a local redacted report;
an unexpected reconciliation error fails closed.

```text
BINANCE_API_KEY=<restricted-testnet-key>
BINANCE_API_SECRET=<restricted-testnet-secret>
BINANCE_USER_STREAM_WS_API_BASE_URL=wss://ws-api.testnet.binance.vision/ws-api/v3
```

The credentials are loaded from the local environment only; never commit them
or place them in logs, fixtures or CI variables.

Register the lifecycle adapter only in an explicitly authenticated local
process:

```csharp
services.AddBinanceUserDataStreamLifecycleClient(options =>
{
    options.Environment = BinanceEnvironment.Testnet;
    options.ApiKey = configuration["BINANCE_API_KEY"] ?? string.Empty;
});
```

```csharp
var lifecycle = serviceProvider.GetRequiredService<IBinanceUserDataStreamLifecycle>();
var listenKey = await lifecycle.CreateListenKeyAsync(stoppingToken);
var stream = new BinanceUserDataStreamClient(new BinanceUserDataStreamOptions
{
    Environment = BinanceEnvironment.Testnet,
    MaxReconnectAttempts = 10
});

await foreach (var userEvent in stream.ReadAsync(listenKey, stoppingToken))
{
    // Persist execution/account events before updating read models.
}
await lifecycle.CloseListenKeyAsync(listenKey, stoppingToken);
```

The parser, transport and lifecycle HTTP verbs are covered with fake tests.
These tests do not prove connectivity to Binance or replace the pending
authenticated Testnet reconciliation gate. Keepalive scheduling, durable event
projection and snapshot-plus-delta recovery remain downstream responsibilities.
