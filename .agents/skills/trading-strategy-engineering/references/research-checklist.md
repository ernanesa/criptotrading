# Strategy research checklist

- [ ] Hypothesis and market regime are explicit.
- [ ] Universe and survivorship rules are defined.
- [ ] No lookahead, leakage, or future-information bias.
- [ ] Fees, spread, slippage, latency, partial fills, and rejected orders are modeled.
- [ ] Position sizing and risk limits are independent of signal generation.
- [ ] Backtest, out-of-sample, walk-forward, stress, and paper results are separated.
- [ ] Parameters were not selected only on the evaluation period.
- [ ] Baseline strategy and ablation comparisons exist.
- [ ] Results are reproducible from data/config/code hashes.
- [ ] Kill switch, reconciliation, audit, and observability are tested.
- [ ] No live deployment without explicit approval.
