# Binance Spot engineering reference

Use official documentation as the source of truth and recheck version-sensitive details before coding.

## Environments

- Spot Testnet REST base: `https://testnet.binance.vision/api`.
- Spot Testnet is API-only, uses virtual balances, does not support `/sapi/*`, can reset periodically, and has market data/order books independent from production.
- Demo Mode REST base: `https://demo-api.binance.com/api`. It is useful for paper-like behavior and more representative market data, but it is not production.
- Keep credentials, URLs, and database namespaces separate for each environment.

## Interfaces

- REST: exchange metadata, historical data, account snapshots, order submission, cancellation, and reconciliation.
- Market WebSocket: candles, trades, book ticker, and depth streams.
- User-data stream: account, order, execution, and balance events.
- WebSocket connections require lifecycle and heartbeat handling; implement reconnect, backoff, resubscription, and snapshot-plus-delta recovery.

## Safety invariants

- Query `exchangeInfo` at startup and refresh it in a controlled way.
- Enforce symbol status, price filters, quantity filters, notional filters, and available balance locally before submission.
- Track request weight and order limits; back off on rate-limit responses and never retry an order blindly without reconciliation.
- Use client order IDs, persist intent before submission, and reconcile against authoritative exchange events.
- Disable withdrawals, use least privilege, redact keys, and make live trading opt-in.

## Official sources

- https://github.com/binance/binance-spot-api-docs
- https://github.com/binance/binance-spot-api-docs/blob/master/testnet/general-info.md
- https://github.com/binance/binance-spot-api-docs/blob/master/demo-mode/general-info.md
- https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-api.md
- https://developers.binance.com/en/docs/products/spot/rest-api
