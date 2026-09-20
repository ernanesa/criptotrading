---
name: trading-validation
description: Validate crypto trading research and backtests for leakage, unrealistic costs, overfitting, robustness, and production readiness.
---

# Trading Validation

Use for strategy research, ranking models, backtests, paper trading, or changes to execution logic.

- Define the hypothesis, universe, timeframe, data provenance, and no-trade conditions.
- Detect look-ahead bias, survivorship bias, leakage, stale joins, and timestamp errors.
- Model fees, spread, slippage, latency, partial fills, rejected orders, liquidity, and turnover.
- Separate in-sample, validation, walk-forward, and out-of-sample periods.
- Run parameter sensitivity, stress scenarios, bootstrap or resampling where appropriate, and realistic forward paper trading.
- Report drawdown, tail loss, exposure, turnover, fill quality, latency, capacity, and uncertainty—not only return or Sharpe.
- Require monitoring, reconciliation, kill switch, rollback, and explicit approval before live trading.

Never claim that historical performance guarantees profit. Reject results whose assumptions cannot be reproduced.
