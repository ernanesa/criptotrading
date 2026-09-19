# Strategy Spec - Nome

## Hypothesis

### Economic/behavioral rationale and falsifiable prediction

## Target market/regime

## Instruments/timeframes

## UniversePolicy and point-in-time availability

## Signal definition

### Feature timestamp/availability and warmup

## Entry/exit/invalidation

## Position sizing

## Required data/features

- event/available timestamps and lineage:
- raw/normalized feature parity:

## Expected holding period

## State machine and order lifecycle assumptions

## Cost model

- maker/taker fee tier and commission asset:
- spread source:
- slippage/impact/fill model:
- latency/rejection assumptions:
- fee asset/rebate/valuation:
- rounding, balance and post-partial-fill notional:

## Failure modes

## Risk limits

## Parameters and allowed search space

- experiment family / trial budget:
- parameters fixed before holdout:

## Benchmarks

- `NO_TRADE/cash`:
- buy-and-hold or economic alternative:
- ablations:

## Validation plan
- In-sample:
- Validation:
- Out-of-sample:
- Walk-forward:
- Sensitivity:
- Stress/Monte Carlo:
- PBO/DSR if applicable:

## Quantitative pass/fail thresholds

- minimum sample/trades/periods:
- maximum OOS degradation:
- maximum drawdown/tail risk:
- sensitivity region:
- paper/shadow divergence:
- confidence interval/lower bound:
- metric definition/annualization/risk-free rate:

## Promotion gates

## Live monitoring / Strategy Health

- degradation and re-enable rules:
- fallback/NO_TRADE and rollback conditions:
- training-serving/parity checks:

## Rejection criteria

## Evidence

- dataset/universe/config/code hashes:
- report/command/output:
- reviewer and approval:
