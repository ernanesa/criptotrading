# 21 - Registro de Riscos

Escala: Probabilidade (P) e Impacto (I) de 1–5. Score=P×I.

| ID | Risco | P | I | Score | Mitigação |
|---|---|---:|---:|---:|---|
| R-01 | Perda de capital por bug de ordem | 3 | 5 | 15 | OMS, idempotência, canary, limits, kill switch |
| R-02 | Estratégia overfit | 5 | 5 | 25 | OOS, walk-forward, sensitivity, PBO/DSR, paper/shadow |
| R-03 | Custos anulam edge | 5 | 4 | 20 | PnL líquido, fee/spread/slippage models, edge threshold |
| R-04 | WebSocket gap produz decisão errada | 3 | 5 | 15 | sequence check, stale flag, resync, breaker |
| R-05 | Timeout duplica ordem | 3 | 5 | 15 | clientOrderId + query-before-retry |
| R-06 | Estado local diverge da Binance | 3 | 5 | 15 | startup/periodic reconciliation, block-on-mismatch |
| R-07 | API key vazada | 2 | 5 | 10 | no withdrawals, secret manager, IP allowlist, rotation |
| R-08 | Clock drift invalida requests | 3 | 4 | 12 | chrony/NTP, server-time monitoring, breaker |
| R-09 | Rate limiting/ban | 3 | 4 | 12 | centralized limiter, backoff, WS-first |
| R-10 | Exchange outage | 3 | 5 | 15 | safe state, cancellation/reconcile policy, alert |
| R-11 | Volatilidade extrema | 4 | 5 | 20 | volatility breaker, reduced risk/no-trade |
| R-12 | Liquidez evapora/slippage extremo | 3 | 5 | 15 | depth/spread checks, max slippage, smaller sizes |
| R-13 | DCA/Grid amplia perda em tendência | 4 | 4 | 16 | regime guard + hard exposure cap |
| R-14 | Estratégias se auto-conflitam | 3 | 4 | 12 | portfolio netting + STP |
| R-15 | Dependência comprometida | 2 | 5 | 10 | scanning, lockfiles, SBOM, update policy |
| R-16 | Preview .NET muda API | 4 | 2 | 8 | global.json, upgrade PRs, live only after GA |
| R-17 | ARM incompatibility | 2 | 4 | 8 | multi-arch CI + Oracle smoke tests |
| R-18 | AWS custo inesperado | 3 | 3 | 9 | Budgets/alerts, Free Tier plan, IaC teardown |
| R-19 | Single VM failure | 3 | 4 | 12 | backups, restart, runbook, eventual HA if justified |
| R-20 | Banco cresce com tick/book data | 4 | 3 | 12 | Parquet/object storage, retention, partitions |
| R-21 | Observability consome recursos | 3 | 2 | 6 | sampling/retention/budgets |
| R-22 | Scope creep impede lançamento | 5 | 3 | 15 | Spot-first, tiered strategy catalog, Product Goal |
| R-23 | ML adiciona complexidade sem edge | 4 | 3 | 12 | ML pós-baseline, benchmark against simple models |
| R-24 | Mudança de API Binance | 4 | 4 | 16 | official changelog watch, contract tests, adapter |
| R-25 | Erro humano em LIVE | 3 | 5 | 15 | environment banner, confirmation, permissions, audit |

## Top risks a tratar primeiro

1. Overfitting.
2. Custos/edge líquido.
3. Order correctness/idempotency.
4. Reconciliation.
5. Market data integrity.
6. Volatility/liquidity tail events.
7. Scope creep.

O registro deve ser revisto em cada Sprint Review e sempre após incidente.
