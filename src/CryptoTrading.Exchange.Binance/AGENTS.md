# Binance Adapter Rules

- Preserve strict environment separation and the default Testnet/read-only boundary.
- No order, cancel, withdrawal, transfer, margin or Futures path is added without explicit approval and an independently reviewed contract.
- Never log request signatures, credentials, response bodies containing sensitive account data or raw secrets.
- Validate exchange filters and reconcile ambiguous outcomes before any retry.
