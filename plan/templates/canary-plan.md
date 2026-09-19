# Canary Plan CANARY-XXXX

**Release/strategy/config/RiskPolicy:**
**Environment/account/symbol:**
**Owner/approver/start/end:**
**Maximum capital and loss:**

## Preconditions

- PromotionReadinessMatrix:
- restore/reconciliation/kill-switch evidence:
- credentials and withdrawal-disabled proof:
- operator availability and communication channel:

## Envelope and stages

| Stage | Capital/notional | Duration/volume | Allowed orders | Promotion condition |
|---|---:|---|---|---|
| C0 | | | | |
| C1 | | | | |

## Abort and automatic safe actions

| Trigger | Threshold/window | Immediate action | Alert/runbook |
|---|---|---|---|

Include drawdown, daily loss, stale data, reconciliation mismatch, audit failure, clock drift, exchange degradation, slippage/fill divergence and unexpected intervention.

## In-flight orders, rollback and recovery

- cancel/leave/reduce policy:
- unknown-state reconciliation:
- software/config/model rollback:
- fallback `NO_TRADE` and re-entry approval:

## Observation and closure

- decision cadence and metrics:
- incident review:
- final Go/No-Go evidence and residual risk:
