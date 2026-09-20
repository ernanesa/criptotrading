---
description: Non-negotiable financial-trading safety boundary for CryptoTrading.
alwaysApply: true
---

# Trading safety boundary

Production and Testnet must use the configured real exchange adapter. Mocks, fakes
and simulators are permitted only in named tests or paper-trading boundaries. Preserve
NO_TRADE and fail-closed defaults. Never read credentials, place, cancel or amend an
order, or make a Testnet-mutating request without explicit user approval immediately
before the action.

Before approving a trading slice, inspect risk limits, reconciliation,
persistence-before-submit, idempotency and redacted evidence. Backtests require
realistic fees, spread, slippage, latency and partial-fill treatment.
