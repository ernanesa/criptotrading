---
name: trading-strategy-engineering
description: Research, design, backtest, stress-test, and review crypto trading strategies and bot architecture with explicit risk controls, reproducibility, and no claims of guaranteed profit.
metadata:
  short-description: Safe quantitative strategy engineering
---

# Trading Strategy Engineering

Use this skill for crypto trading strategy research, exchange integration, portfolio construction, execution design, backtesting, risk analysis, and bot safety.

## Scope and safety

This skill is for software and quantitative research, not personalized financial advice or a promise of returns. Never recommend live deployment solely from a backtest. Default to read-only market data, simulation, and paper trading. Live trading requires explicit approval, exchange-specific safeguards, API keys without withdrawal permission, limits, monitoring, and a kill switch.

## Strategy research

Define the hypothesis, market universe, timeframe, signal, execution assumption, costs, liquidity, risk budget, invalidation condition, and expected failure modes. Compare simple baselines before complex or machine-learning strategies. Avoid presenting one strategy as universally best; performance is regime-dependent and can disappear after costs or leakage correction.

## Validation ladder

1. Deterministic unit tests for indicators, signals, sizing, fees, rounding, and exchange rules.
2. Historical backtest with fees, spread, slippage, latency, partial fills, limits, delistings, and realistic order execution.
3. Walk-forward or rolling out-of-sample validation.
4. Parameter sensitivity and anti-overfitting checks.
5. Stress tests for volatility, gaps, illiquidity, exchange outage, websocket loss, duplicate events, clock skew, and reconciliation mismatch.
6. Paper/dry-run forward test against live market data.
7. Small, bounded live deployment only after explicit approval and a rollback/kill-switch plan.

## Risk controls

Treat risk management as an independent layer before execution: maximum notional, per-trade risk, exposure by asset and venue, leverage cap, daily loss limit, drawdown stop, volatility pause, stale-data guard, price-band guard, duplicate-order guard, circuit breaker, reconciliation, and manual emergency stop. The bot must fail closed when balances, market data, order state, or risk limits are uncertain.

## Architecture

Separate market data, strategy signals, portfolio/risk, order management, exchange adapters, reconciliation, audit, and observability. Use ports and adapters, immutable domain events, idempotent commands, an outbox/inbox or equivalent delivery guarantee, and deterministic clocks in tests. Never let an AI model place a live order directly; it may propose a strategy or configuration that passes validation gates.

## Metrics and evaluation

Report net returns only with costs, volatility, maximum drawdown, downside deviation, Sharpe/Sortino with caveats, profit factor, turnover, exposure, hit rate, average win/loss, tail losses, capacity, slippage, latency, fill ratio, and out-of-sample stability. Include the data range, universe, assumptions, seed, code version, and configuration hash so results are reproducible.

Read [references/research-checklist.md](references/research-checklist.md) before accepting a strategy.
