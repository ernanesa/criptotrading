---
name: binance-spot-engineering
description: Design, implement, test, and review safe Binance Spot trading systems with Testnet, Demo Mode, deterministic risk controls, and reproducible validation.
---

# Binance Spot Engineering

Use this skill for Binance Spot crypto-trading software, from market-data ingestion and strategy research through Testnet validation. It is an engineering and research workflow, not a promise of profit or personal financial advice.

## Operating boundary

- Default to read-only analysis, backtesting, paper trading, or Binance Testnet.
- Never place live orders unless the user explicitly authorizes that exact action immediately before execution.
- Testnet must use the real Binance API with virtual balances; paper-trading simulators must be isolated and explicitly labeled.
- Never request or expose secrets. Use API keys without withdrawal permission, least privilege, IP restrictions, environment secrets, and a kill switch.
- Keep Spot, Testnet, Demo Mode, and production configurations visibly separate.
- Do not use leverage, margin, futures, or arbitrage unless the user explicitly changes scope.

## Workflow

1. Inspect the repository, dependency versions, configuration, and existing tests before changing code.
2. Define the fixed trading universe. Start with BTC/USDT; expand only to a reviewed set of liquid pairs.
3. Retrieve current symbol rules from `exchangeInfo`; validate status, tick size, step size, minimum quantity, and minimum notional before creating an order.
4. Separate market data, feature calculation, signal/ranking, portfolio and risk, order management, Binance adapter, reconciliation, and observability.
5. Rank candidates using expected edge after fees, spread, slippage, and latency—not raw volatility alone. Add liquidity thresholds, score margin, cooldown, hysteresis, turnover limits, and exposure caps.
6. Test deterministic calculations and order validation first. Backtest with fees, spread, slippage, latency, partial fills, rejected orders, outages, and exchange filters.
7. Use walk-forward, out-of-sample, sensitivity, stress scenarios, and forward paper trading before Testnet. Treat Testnet as an integration environment because its balances and market data can reset or differ from production.
8. Reconcile orders, fills, balances, and positions from Binance user-data events and periodic REST snapshots. Recover safely after reconnects and WebSocket lifecycle limits.
9. Emit structured audit events, metrics, traces, and alerts for every decision and order lifecycle. Redact secrets.
10. Produce a report with assumptions, data period, costs, drawdown, exposure, turnover, latency, fill quality, failure cases, and an explicit go/no-go decision.

Reject fake or unverifiable completion: no hardcoded market responses, fake order acknowledgements, unreachable exchange adapters, or production wiring to test doubles. Require an actual Testnet or contract check before calling the Binance integration ready.

## References and tool choice

Read [references/binance-spot.md](references/binance-spot.md) for endpoints, environments, rate limits, WebSockets, filters, and authentication. Prefer official Binance documentation and verify version-sensitive details before implementation.

Fetch MCP is not required by this skill. Use it only when a project explicitly needs generic HTTP retrieval inside an agent workflow and no direct web/API connector is available. Do not add it merely to duplicate web access.
