# 17 - Product Backlog

O backlog abaixo é inicial e ordenado por dependência/risco. IDs são estáveis; detalhes podem evoluir.

## Governança de decomposição

Cada história deve ser expandida antes do Sprint para `TASK-US-XXX-NN` e, quando necessário, `SUBTASK-TASK-US-XXX-NN-NN`. Cada task aponta para AC, boundary, teste, comando e `EVIDENCE-<id>-<date>`. As linhas `Tasks:` existentes são sementes de decomposição, não evidência de implementação.

O caminho crítico do MVP é: foundation/CI → journal/correlation → exchange read-only → OMS/reconciliation → RiskPolicy/kill switch → SafeMode/restore → paper/backtest → baseline BTC/USDT → shadow/Testnet. `US-170` e `US-180..182` são pré-requisitos de Research/Live, apesar de permanecerem agrupados em épicos históricos posteriores.

### MVP task registry inicial

| ID | Parent | Resultado verificável |
|---|---|---|
| `TASK-US-001-01` | US-001 | repo/manifestos/CI mínimos sem código de trading |
| `TASK-US-003-01` | US-003 | matriz DEV/PAPER/TESTNET/DEMO/PRODUCTION e banner |
| `TASK-US-012-01` | US-012 | journal/event envelope versionado |
| `TASK-US-020-01` | US-020 | exchangeInfo/filtros/limites snapshotados |
| `TASK-US-030-01` | US-030 | user-data WS moderno + lifecycle/reconnect |
| `TASK-US-032-01` | US-032 | persist-before-submit + dedupe/query-before-retry |
| `TASK-US-033-01` | US-033 | startup/reconnect/timeout reconciliation |
| `TASK-US-040-01` | US-040 | RiskPolicy versionada e auditável |
| `TASK-US-043-01` | US-043 | kill switch independente e teste de bloqueio |
| `TASK-US-050-01` | US-050 | dataset/universe point-in-time com hash |
| `TASK-US-051-01` | US-051 | simulador/backtest com custos e fills |
| `TASK-US-061-01` | US-061 | baseline BTC/USDT com Strategy Spec completa |
| `TASK-US-090-01` | US-090 | paper/shadow/Testnet evidence sem mock em integração |
| `TASK-US-130-01` | US-130 | readiness matrix, restore drill e aprovação explícita |

## EPIC E00 - Governance & Bootstrap

### US-001 Como desenvolvedor, quero estrutura de repositório e regras para evoluir com segurança.
Tasks: solution skeleton; editorconfig; gitignore; global.json; Node/pnpm pinning; contribution guide; CI skeleton.

### US-002 Como PO, quero backlog e ADRs versionados para rastrear decisões.
Tasks: templates; labels; milestones; ADR policy.

### US-003 Como operador, quero ambientes explicitamente separados.
Tasks: environment model; config hierarchy; visible environment banner.

## EPIC E01 - Platform Foundation

### US-010 Quero modular boundaries para impedir acoplamento entre Strategy/Risk/Execution.
Tasks: projects/modules; architecture tests; DI composition root.

### US-011 Quero clock e IDs determinísticos/testáveis.
Tasks: IClock; monotonic timing; correlation/causation IDs; client order ID generator.

### US-012 Quero journal de eventos críticos.
Tasks: event contracts; persistence schema; correlation queries.

## EPIC E02 - Binance Reference & Market Data

### US-020 Quero consultar símbolos/filtros dinamicamente.
Tasks: exchangeInfo adapter; caching/versioning; filter tests.

### US-021 Quero trades/candles realtime resilientes.
Tasks: WS client; reconnect; heartbeat/staleness; metrics.

### US-022 Quero local order book correto.
Tasks: snapshot+diff algorithm; sequence validation; gap/resync tests.

### US-023 Quero gravar/reproduzir market data.
Tasks: recorder; Parquet schema; replay clock.

## EPIC E03 - Account & OMS

### US-030 Quero User Data Stream moderno.
Tasks: WS API auth/subscription; event mapping; reconnect/reconcile.

### US-031 Quero state machine de ordens robusta.
Tasks: statuses; partial fills; cancel; reject; ambiguous timeout tests.

### US-032 Quero envio idempotente de ordens.
Tasks: clientOrderId; command dedup; lookup-before-retry.

### US-033 Quero reconciliação automática.
Tasks: startup; periodic; mismatch policy; alert/block.

## EPIC E04 - Risk Engine

### US-040 Quero políticas de risco versionadas.
Tasks: model; persistence; audit; validation.

### US-041 Quero sizing por risco.
Tasks: fixed fractional; volatility adjustment; exchange rounding.

### US-042 Quero protections/circuit breakers.
Tasks: daily loss; DD; spread; liquidity; stale data; exchange/clock health.

### US-043 Quero kill switch independente.
Tasks: command; cancel policy; UI/API; integration test.

## EPIC E05 - Research Data & Backtesting

### US-050 Quero datasets reproduzíveis.
Tasks: importer; data QA; hash/version; Parquet partitioning.

### US-051 Quero backtest event-driven com fees.
Tasks: simulated exchange; fill model L1; fees/spread/slippage.

### US-052 Quero analytics completos.
Tasks: equity; DD; Sharpe/Sortino/Calmar; expectancy; attribution.

### US-053 Quero validation framework.
Tasks: temporal split; walk-forward; sensitivity; Monte Carlo; PBO/DSR research.

## EPIC E06 - Strategy SDK & Baselines

### US-060 Quero contrato de Strategy sem acesso à exchange.
Tasks: AlphaSignal; context; lifecycle; strategy registry.

### US-061 Quero Trend Following baseline.
Tasks: spec; implementation; tests; backtest.

### US-062 Quero Momentum/Breakout baselines.
Tasks: specs; implementations; tests; validation.

### US-063 Quero Mean Reversion baseline.
Tasks: z-score/VWAP variants; regime guard; costs.

### US-064 Quero Adaptive Grid baseline.
Tasks: grid planner; inventory cap; volatility spacing; simulation.

## EPIC E07 - Regime, Portfolio & Autonomous Multi-Strategy

### US-070 Quero detectar regimes explicáveis.
Tasks: trend/volatility/liquidity/correlation features; classifier; confidence; transition hysteresis; tests.

### US-071 Quero combinar sinais e fazer netting.
Tasks: intent aggregation; conflict rules; target builder.

### US-072 Quero risk budgets por estratégia.
Tasks: allocation model; caps; strategy health integration.

### US-073 Quero Strategy Health automático.
Tasks: rolling metrics; state transitions; quarantine/re-enable policy.

### US-074 Quero Autonomy Orchestrator.
Tasks: MarketContext; candidate eligibility; orchestration API; decision trace; NO_TRADE policy.

### US-075 Quero seleção automática de estratégia.
Tasks: regime matrix bootstrap; expected-net-utility scorer; risk/cost/uncertainty/correlation penalties; ranking; hysteresis/cooldown.

### US-076 Quero alocação dinâmica entre estratégias.
Tasks: multi-strategy budget allocator; rebalancing rules; capacity constraints; simulation; audit.

## EPIC E08 - Execution Quality

### US-080 Quero estimar edge líquido antes de ordenar.
Tasks: fees/spread/slippage estimator; threshold policy.

### US-081 Quero passive/aggressive execution.
Tasks: market vs limit decision; cancel stale; fill tracking.

### US-082 Quero execution attribution.
Tasks: arrival price; implementation shortfall; expected vs realized slippage.

### US-083 Quero adaptive execution.
Tasks: execution context; tactic scorer; maker/taker choice; urgency/liquidity/edge decay model; bounded adaptive selection.

## EPIC E09 - Paper, Shadow & Canary

### US-090 Quero paper trading com mercado real.
Tasks: simulator; virtual balances; same risk/execution interfaces.

### US-091 Quero shadow trading.
Tasks: full decision pipeline; no exchange send; comparison report.

### US-092 Quero canary controls.
Tasks: tiny capital policy; reduced symbols; stricter limits; rollback.

### US-093 Quero operação autônoma prolongada em shadow.
Tasks: unattended soak test; forced failure scenarios; regime transitions; automatic strategy switching; recovery metrics.

## EPIC E10 - Web Dashboard

### US-100 Quero shell React type-safe.
Tasks: Vite; Router; Query; auth shell; generated API types.

### US-101 Quero realtime trading dashboard.
Tasks: SignalR; market/positions/orders/PnL panels.

### US-102 Quero risk dashboard e kill switch.
Tasks: limits/breakers; confirmations; audit view.

### US-103 Quero research dashboard.
Tasks: experiments; metrics; equity/DD; parameter sensitivity.

### US-104 Quero autonomy dashboard.
Tasks: current regime; candidate strategies; scores; allocations; health; drift; champion/challenger; decision trace.

## EPIC E11 - Security & Observability

### US-110 Quero secret management por ambiente.
Tasks: SSM adapter; local/VPS adapter; redaction tests.

### US-111 Quero OTel end-to-end.
Tasks: tracing; metrics; logs; dashboards; alerts.

### US-112 Quero secure supply chain.
Tasks: scanners; dependency updates; SBOM; branch rules.

## EPIC E12 - Deployment & Portability

### US-120 Quero containers multi-arch.
Tasks: Dockerfiles; buildx; ARM Oracle smoke test.

### US-121 Quero laboratório AWS.
Tasks: IaC; EC2; IAM; VPC; S3; CloudWatch; SSM; Budget alarms.

### US-122 Quero deploy Oracle/VPS portátil.
Tasks: compose profile; volumes; reverse proxy; backups; migration runbook.

## EPIC E13 - Live Spot 1.0

### US-130 Quero readiness gate live.
Tasks: checklist; strategy evidence; keys; risk policy; restore drill.

### US-131 Quero operar canary e promover gradualmente.
Tasks: canary observation window; incident review; scale rules.

### US-132 Quero relatório pós-release.
Tasks: reliability; execution quality; strategy performance; autonomy metrics; backlog update.

### US-133 Quero Spot 1.0 operar sem intervenção rotineira.
Tasks: autonomous regime/selection/allocation; safe fallback; recovery; soak period; intervention-rate KPI.

## EPIC E14 - Futures & Hedging (pós-1.0)
US-140 Futures adapter; US-141 leverage/margin/liquidation risk; US-142 shorts; US-143 hedging; US-144 funding/basis.

## EPIC E15 - Market Neutral & Arbitrage (pós-1.0)
US-150 pairs/stat-arb; US-151 triangular arb; US-152 multi-exchange adapter; US-153 XEMM/cross-exchange.

## EPIC E16 - Market Making Advanced (pós-1.0)
US-160 L3 order-book simulator; US-161 inventory-aware MM; US-162 Avellaneda-Stoikov research; US-163 adverse-selection controls.

## EPIC E17 - Continuous Learning & Adaptive Allocation

### US-170 Quero Model/Strategy Registry com lineage completo.
Tasks: schema; model/data/code/config versions; status lifecycle; reproducibility query.

### US-171 Quero regime ML challenger.
Tasks: feature pipeline; baseline comparison; OOS/walk-forward; shadow deployment.

### US-172 Quero slippage/market-impact model adaptativo.
Tasks: labels; training; calibration; online monitoring; execution integration.

### US-173 Quero meta-allocation contextual.
Tasks: contextual bandit/Bayesian prototype; offline evaluation; shadow exploration; bounded canary policy.

### US-174 Quero concept/data/execution drift monitoring.
Tasks: drift metrics; thresholds; alerts; retraining trigger; false-positive analysis.

### US-175 Quero champion-challenger automatizado.
Tasks: challenger creation; evaluation policy; promotion gates; model registry integration; rollback.

### US-176 Quero bounded parameter adaptation.
Tasks: parameter envelopes; optimizer/retrainer; automatic validation; promotion/rollback tests.

### US-177 Quero slow learning loop automatizado.
Tasks: scheduled/triggered training; validation DAG; shadow/canary orchestration; evidence persistence.

## EPIC E18 - Self-Healing & High Availability

### US-180 Quero SafeMode automático.
Tasks: financial-state trust gate; no-new-entry; reconcile; recovery policy; tests.

### US-181 Quero supervised self-healing.
Tasks: restart policy; backoff/jitter; bounded queues; failure injection; unattended recovery.

### US-182 Quero disaster recovery verificável.
Tasks: backup/restore; corrupt-state scenario; RPO/RTO measurement; runbook automation.

### US-183 Quero primary/standby futuro sem double-trading.
Tasks: leader election design; fencing token; standby state sync; failover simulation; chaos test.
