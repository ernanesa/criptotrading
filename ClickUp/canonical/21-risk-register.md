# 21 - Registro de Riscos

Escala: Probabilidade (P) e Impacto (I) de 1–5. Score=P×I.

A tabela é o sumário. Cada risco ativo deve ter owner, status, trigger/leading indicator, prevenção, contingency, risco residual, próxima revisão e evidência. Risco sem owner ou data de revisão não pode ser aceito para canary.

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
| R-17 | ARM incompatibility em destino futuro | 2 | 3 | 6 | multi-arch CI + smoke test antes de aprovar migração |
| R-18 | AWS custo inesperado em iniciativa futura | 1 | 3 | 3 | fora do escopo ativo; se aprovada, Budgets/alerts, plano da conta e teardown IaC |
| R-19 | Falha da máquina local única | 3 | 4 | 12 | persistência, backups off-host, restart, runbook e restore drill |
| R-20 | Banco cresce com tick/book data | 4 | 3 | 12 | Parquet/object storage, retention, partitions |
| R-21 | Observability consome recursos | 3 | 2 | 6 | sampling/retention/budgets |
| R-22 | Scope creep impede lançamento | 5 | 3 | 15 | Spot-first, tiered strategy catalog, Product Goal |
| R-23 | ML adiciona complexidade sem edge | 4 | 3 | 12 | ML pós-baseline, benchmark against simple models |
| R-24 | Mudança de API Binance | 4 | 4 | 16 | official changelog watch, contract tests, adapter |
| R-25 | Erro humano em LIVE | 3 | 5 | 15 | environment banner, confirmation, permissions, audit |
| R-26 | Ledger/PnL incorreto por fee/valuation | 3 | 5 | 15 | ledger por ativo, fee asset, valuation versionada, reconciliation |
| R-27 | Evento financeiro perdido por Channel/crash | 3 | 5 | 15 | persist-before-submit, outbox/inbox, no-drop policy, recovery tests |
| R-28 | Holdout contaminado ou múltiplos testes ocultos | 4 | 5 | 20 | preregistration, experiment family/trial budget, locked holdout |
| R-29 | Divergência backtest/paper/live | 4 | 5 | 20 | shared core, parity suite, attribution e ADR |
| R-30 | Stablecoin/quote asset depeg | 3 | 5 | 15 | exposure limit, valuation/freshness, breaker, alternative safe state |
| R-31 | Kill switch indisponível ou corrida com ordem em voo | 3 | 5 | 15 | independent path, in-flight policy, drills, reconcile |
| R-32 | Comprometimento da conta administrativa | 2 | 5 | 10 | MFA/WebAuthn, network restriction, step-up, revocation, audit |
| R-33 | Exploração/adaptação causa feedback loop | 3 | 5 | 15 | exploration budget, off-policy evaluation, bounded canary, NO_TRADE |
| R-34 | Fonte histórica não permite universo point-in-time | 4 | 4 | 16 | capture exchangeInfo/listing/delisting snapshots desde o início |
| R-35 | Free plan/créditos AWS futuros expiram ou ambiente é encerrado | 1 | 3 | 3 | fora do escopo ativo; portabilidade, data export, TTL e nenhuma dependência de produção |
| R-36 | Contenção local entre trading, backtest e observabilidade | 4 | 4 | 16 | Compose profiles, cgroups/limits, resource budgets, load test e pausar workloads não críticos |
| R-37 | Falta de energia/rede ou falha de disco na máquina local | 3 | 5 | 15 | SafeMode, restart/reconcile, UPS quando justificado, disk health, backup off-host e restore drill |

## Registro detalhado por risco

Para riscos P0/P1 ou score ≥ 15, usar:

- **Owner / status:** Open | Mitigating | Accepted | Closed;
- **Trigger e leading indicator:** condição mensurável;
- **Prevenção / detecção / contingency:** controles separados;
- **Risco residual:** P, I e justificativa depois dos controles;
- **Prazo / próxima revisão:** data explícita;
- **Artefatos relacionados:** RF/RNF, story, test, runbook e evidence;
- **Acceptance authority:** quem pode aceitar o residual e por quanto tempo.

## Top risks a tratar primeiro

1. Overfitting.
2. Custos/edge líquido.
3. Order correctness/idempotency.
4. Reconciliation.
5. Market data integrity.
6. Volatility/liquidity tail events.
7. Scope creep.
8. Ledger/durabilidade financeira.
9. Backtest-production parity e holdout contamination.
10. Kill switch/control plane.

O registro deve ser revisto em cada Sprint Review e sempre após incidente.
