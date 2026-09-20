# 17 - Product Backlog

O backlog abaixo é inicial e ordenado por dependência/risco. IDs são estáveis; detalhes podem evoluir.

## Governança de decomposição

Cada história deve ser expandida antes do Sprint para `TASK-US-XXX-NN` e, quando necessário, `SUBTASK-TASK-US-XXX-NN-NN`. Cada task aponta para AC, boundary, teste, comando e `EVIDENCE-<id>-<date>`. As linhas `Tasks:` existentes são sementes de decomposição, não evidência de implementação.

O caminho crítico do MVP é: governance/CI → runtime local reproduzível → journal/ledger/durabilidade → exchange read-only → OMS/reconciliation → RiskPolicy/kill switch → SafeMode/restore → paper/backtest → baseline Trend BTC/USDT → shadow/Testnet → canary readiness. Multi-strategy, Regime Detection adaptativo, continuous learning e hosting cloud não pertencem ao caminho crítico do R1.0.

### MVP task registry inicial

| ID | Parent | Resultado verificável |
|---|---|---|
| `TASK-US-001-01` | US-001 | repo/manifestos/CI mínimos sem código de trading |
| `TASK-US-003-01` | US-003 | matriz DEV/PAPER/TESTNET/DEMO/PRODUCTION e banner |
| `TASK-US-012-01` | US-012 | journal/event envelope versionado |
| `TASK-US-013-01` | US-013 | ledger multi-commodity, fee e movimentação |
| `TASK-US-014-01` | US-014 | outbox/inbox e política de perda por classe de evento |
| `TASK-US-015-01` | US-015 | manifesto e validator de rastreabilidade |
| `TASK-US-020-01` | US-020 | exchangeInfo/filtros/limites snapshotados |
| `TASK-US-024-01` | US-024 | modelo temporal, universo point-in-time e lineage |
| `TASK-US-030-01` | US-030 | user-data WS moderno + lifecycle/reconnect |
| `TASK-US-032-01` | US-032 | persist-before-submit + dedupe/query-before-retry |
| `TASK-US-033-01` | US-033 | startup/reconnect/timeout reconciliation |
| `TASK-US-040-01` | US-040 | RiskPolicy versionada e auditável |
| `TASK-US-041-01` | US-041 | sizing decimal determinístico e filters |
| `TASK-US-042-01` | US-042 | protections e circuit breakers hierárquicos |
| `TASK-US-043-01` | US-043 | kill switch independente e teste de bloqueio |
| `TASK-US-044-01` | US-044 | schemas, aprovação e expiração dos gates de risco |
| `TASK-US-050-01` | US-050 | dataset/universe point-in-time com hash |
| `TASK-US-051-01` | US-051 | simulador/backtest com custos e fills |
| `TASK-US-052-01` | US-052 | analytics e fórmulas versionadas |
| `TASK-US-053-01` | US-053 | OOS/walk-forward/sensitivity/stress framework |
| `TASK-US-054-01` | US-054 | preregistration, trial budget e locked holdout |
| `TASK-US-055-01` | US-055 | RAG local com citações, frescor e confirmação no commit |
| `TASK-US-060-01` | US-060 | contrato Strategy sem dependência de exchange |
| `TASK-US-061-01` | US-061 | baseline BTC/USDT com Strategy Spec completa |
| `TASK-US-073-01` | US-073 | Strategy Health da baseline e transições seguras |
| `TASK-US-090-01` | US-090 | paper exchange determinístico com market data real |
| `TASK-US-094-01` | US-094 | integração Binance Spot Testnet real e evidence sem mock |
| `TASK-US-180-01` | US-180 | SafeMode, trust gate e bloqueio de novas entradas |
| `TASK-US-181-01` | US-181 | restart/reconnect supervisionados e recovery evidence |
| `TASK-US-182-01` | US-182 | backup/restore drill com RPO/RTO medidos |
| `TASK-US-124-01` | US-124 | ambiente local completo, reproduzível e sem dependência cloud |
| `TASK-US-130-01` | US-130 | readiness matrix, restore drill e aprovação explícita |
| `TASK-US-134-01` | US-134 | canary plan, abort/rollback e release evidence |

## EPIC E-00 - Governance & Bootstrap

### US-001 Como desenvolvedor, quero estrutura de repositório e regras para evoluir com segurança.
Tasks: solution skeleton; editorconfig; gitignore; global.json; Node/pnpm pinning; contribution guide; CI skeleton.

### US-002 Como PO, quero backlog e ADRs versionados para rastrear decisões.
Tasks: templates; labels; milestones; ADR policy.

### US-003 Como operador, quero ambientes explicitamente separados.
Tasks: environment model; config hierarchy; visible environment banner.

## EPIC E-01 - Platform Foundation

### US-010 Quero modular boundaries para impedir acoplamento entre Strategy/Risk/Execution.
Tasks: projects/modules; architecture tests; DI composition root.

### US-011 Quero clock e IDs determinísticos/testáveis.
Tasks: IClock; monotonic timing; correlation/causation IDs; client order ID generator.

### US-012 Quero journal de eventos críticos.
Tasks: event contracts; persistence schema; correlation queries.

### US-013 Quero ledger financeiro multi-commodity para reconstruir balances, fees e PnL.
Tasks: double-entry postings imutáveis; free/locked; fee asset; cost basis; valuation; compensating entries; reconciliação.

### US-014 Quero semântica de entrega durável por classe de evento.
Tasks: event criticality; transactional outbox/inbox; dedupe; poison handling; Channel overflow policies; crash recovery.

### US-015 Quero rastreabilidade validável por máquina.
Tasks: manifest schema; ID/link validator; orphan/duplicate/superseded checks; evidence report.

## EPIC E-02 - Binance Reference & Market Data

### US-020 Quero consultar símbolos/filtros dinamicamente.
Tasks: exchangeInfo adapter; caching/versioning; filter tests.

### US-021 Quero trades/candles realtime resilientes.
Tasks: WS client; reconnect; heartbeat/staleness; metrics.

### US-022 Quero local order book correto.
Tasks: snapshot+diff algorithm; sequence validation; gap/resync tests.

### US-023 Quero gravar/reproduzir market data.
Tasks: recorder; Parquet schema; replay clock.

### US-024 Quero datasets temporais e snapshots de universo reconstruíveis.
Tasks: event/exchange/receive/process time; raw immutable; exchangeInfo/listing/delisting snapshots; corrections/quarantine.

## EPIC E-03 - Account & OMS

### US-030 Quero User Data Stream moderno.
Tasks: WS API auth/subscription; event mapping; reconnect/reconcile.

### US-031 Quero state machine de ordens robusta.
Tasks: statuses; partial fills; cancel; reject; ambiguous timeout tests.

### US-032 Quero envio idempotente de ordens.
Tasks: clientOrderId; command dedup; lookup-before-retry.

### US-033 Quero reconciliação automática.
Tasks: startup; periodic; mismatch policy; alert/block.

## EPIC E-04 - Risk Engine

### US-040 Quero políticas de risco versionadas.
Tasks: model; persistence; audit; validation.

### US-041 Quero sizing por risco.
Tasks: fixed fractional; volatility adjustment; exchange rounding.

### US-042 Quero protections/circuit breakers.
Tasks: daily loss; DD; spread; liquidity; stale data; exchange/clock health.

### US-043 Quero kill switch independente.
Tasks: command; cancel policy; UI/API; integration test.

### US-044 Quero RiskPolicy e PromotionReadinessMatrix aprováveis e versionadas.
Tasks: templates; thresholds; validity; approvals; abort/rollback; evidence links.

## EPIC E-05 - Research Data & Backtesting

### US-050 Quero datasets reproduzíveis.
Tasks: importer; data QA; hash/version; Parquet partitioning.

### US-051 Quero backtest event-driven com fees.
Tasks: simulated exchange; fill model L1; fees/spread/slippage.

### US-052 Quero analytics completos.
Tasks: equity; DD; Sharpe/Sortino/Calmar; expectancy; attribution.

### US-053 Quero validation framework.
Tasks: temporal split; walk-forward; sensitivity; Monte Carlo; PBO/DSR research.

### US-054 Quero governança contra seleção e múltiplos testes.
Tasks: preregistration; experiment family/trial budget; locked holdout; contamination policy; confidence intervals; ablations.

### US-055 Quero consultar conhecimento do projeto por RAG local verificável.
Tasks: source registry; ingestão incremental; FTS/embeddings locais; citações path/linha/hash; authority/freshness/conflict policy; confirmação contra commit; testes de isolamento do hot path.

## EPIC E-06 - Strategy SDK & Baseline

### US-060 Quero contrato de Strategy sem acesso à exchange.
Tasks: AlphaSignal; context; lifecycle; strategy registry.

### US-061 Quero Trend Following baseline.
Tasks: spec; implementation; tests; backtest.

### US-073 Quero Strategy Health automático para a baseline R1.0.
Tasks: rolling metrics; HEALTHY/DEGRADED/SUSPICIOUS/DISABLED; thresholds versionados; quarantine/re-enable policy; NO_TRADE e redução de risco.

**Escopo R1.0:** avaliar exclusivamente a baseline Trend ativa. Comparação e alocação entre múltiplas estratégias pertencem à `US-077` pós-R1.0.

As histórias US-062, US-063 e US-064 são pós-R1.0 e só entram após US-061 atravessar o pipeline completo.

### US-062 Quero Momentum/Breakout baselines.
Tasks: specs; implementations; tests; validation.

### US-063 Quero Mean Reversion baseline.
Tasks: z-score/VWAP variants; regime guard; costs.

### US-064 Quero Adaptive Grid baseline.
Tasks: grid planner; inventory cap; volatility spacing; simulation.

## EPIC E-07 - Regime, Portfolio & Autonomous Multi-Strategy (pós-R1.0)

### US-070 Quero detectar regimes explicáveis.
Tasks: trend/volatility/liquidity/correlation features; classifier; confidence; transition hysteresis; tests.

### US-071 Quero combinar sinais e fazer netting.
Tasks: intent aggregation; conflict rules; target builder.

### US-072 Quero risk budgets por estratégia.
Tasks: allocation model; caps; strategy health integration.

### US-077 Quero Strategy Health comparativo para múltiplas estratégias.
Tasks: health normalization; comparação por regime; portfolio eligibility; interações de quarantine/re-enable entre estratégias.

### US-074 Quero Autonomy Orchestrator.
Tasks: MarketContext; candidate eligibility; orchestration API; decision trace; NO_TRADE policy.

### US-075 Quero seleção automática de estratégia.
Tasks: regime matrix bootstrap; expected-net-utility scorer; risk/cost/uncertainty/correlation penalties; ranking; hysteresis/cooldown.

### US-076 Quero alocação dinâmica entre estratégias.
Tasks: multi-strategy budget allocator; rebalancing rules; capacity constraints; simulation; audit.

## EPIC E-08 - Execution Quality

### US-080 Quero estimar edge líquido antes de ordenar.
Tasks: fees/spread/slippage estimator; threshold policy.

### US-081 Quero passive/aggressive execution.
Tasks: market vs limit decision; cancel stale; fill tracking.

### US-082 Quero execution attribution.
Tasks: arrival price; implementation shortfall; expected vs realized slippage.

### US-083 Quero adaptive execution.
Tasks: execution context; tactic scorer; maker/taker choice; urgency/liquidity/edge decay model; bounded adaptive selection.

## EPIC E-09 - Paper, Shadow & Canary

### US-090 Quero paper trading com mercado real.
Tasks: simulator; virtual balances; same risk/execution interfaces.

### US-091 Quero shadow trading.
Tasks: full decision pipeline; no exchange send; comparison report.

### US-092 Quero canary controls.
Tasks: tiny capital policy; reduced symbols; stricter limits; rollback.

### US-093 Quero operação autônoma prolongada em shadow.
Tasks: unattended soak test da baseline; forced failure scenarios; transições de health; NO_TRADE/safe fallback; recovery metrics. Seleção ou troca automática multi-strategy não pertence ao R1.0.

### US-094 Quero validar a integração real na Binance Spot Testnet.
Tasks: credencial/endpoints segregados; submit/query/cancel; user-data lifecycle; filters/rate limits; timeout ambíguo; reconciliation; evidence sem mock/fake.

## EPIC E-10 - Web Dashboard

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

## EPIC E-11 - Security & Observability

### US-110 Quero secret management por ambiente.
Tasks: local secret provider; file permissions/Docker secrets; redaction tests; provider cloud somente se aprovado futuramente.

### US-111 Quero OTel end-to-end.
Tasks: tracing; metrics; logs; dashboards; alerts.

### US-112 Quero secure supply chain.
Tasks: scanners; dependency updates; SBOM; branch rules.

### US-113 Quero threat model e controle administrativo forte.
Tasks: trust boundaries; misuse cases; MFA/WebAuthn; session/revocation; network restriction; step-up; replay protection.

### US-114 Quero SLOs e alertas testáveis por jornada crítica.
Tasks: SLIs/error budgets; bounded labels; synthetic probes; ownership/escalation; alert delivery tests.

## EPIC E-12 - Runtime local & portabilidade futura

### US-120 Quero containers multi-arch.
Tasks: Dockerfiles; buildx; smoke test amd64 local; ARM smoke test em runner/emulação compatível quando disponível.

### US-124 Quero o ambiente inicial completo nesta máquina local.
Tasks: Compose profiles; PostgreSQL; volumes; OTel/local dashboards; health checks; resource budgets; backup/restore; bootstrap e smoke test sem credencial cloud.

**Critério:** `AC-LOCAL-01` e `AC-LOCAL-02`. Esta história pertence ao foundation e precede qualquer iniciativa de hosting externo.

### Itens futuros não comprometidos

As histórias abaixo ficam em estado `Deferred/Option`, fora do roadmap R0.x/R1.0. Só podem ser refinadas após decisão explícita de produto e ADR específico.

### US-121 Quero laboratório AWS.
Tasks: IaC; EC2; IAM; VPC; S3; CloudWatch; SSM; Budget alarms.

### US-122 Quero deploy Oracle/VPS portátil.
Tasks: compose profile; volumes; reverse proxy; backups; migration runbook.

### US-123 Quero laboratório AWS com custo limitado e teardown verificável.
Tasks: eligibility/account-plan check; low budget; prohibited-costly-services list; TTL; cost estimate; teardown evidence.

## EPIC E-13 - Live Spot 1.0

### US-130 Quero readiness gate live.
Tasks: checklist; strategy evidence; keys; risk policy; restore drill.

### US-131 Quero operar canary e promover gradualmente.
Tasks: canary observation window; incident review; scale rules.

### US-132 Quero relatório pós-release.
Tasks: reliability; execution quality; strategy performance; autonomy metrics; backlog update.

### US-133 Quero Spot 1.0 operar sem intervenção rotineira.
Tasks: baseline no-trade; safe fallback; recovery; soak period/volume; intervention-rate KPI. Multi-strategy não é requisito.

### US-134 Quero canary governado por plano e evidência de release.
Tasks: canary envelope; abort criteria; rollback; in-flight orders; approvals; observation report.

## EPIC E-14 - Futures & Hedging (opção exploratória)
US-140 Futures adapter; US-141 leverage/margin/liquidation risk; US-142 shorts; US-143 hedging; US-144 funding/basis.

## EPIC E-15 - Market Neutral & Arbitrage (opção exploratória)
US-150 pairs/stat-arb; US-151 triangular arb; US-152 multi-exchange adapter; US-153 XEMM/cross-exchange.

## EPIC E-16 - Market Making Advanced (opção exploratória)
US-160 L3 order-book simulator; US-161 inventory-aware MM; US-162 Avellaneda-Stoikov research; US-163 adverse-selection controls.

## EPIC E-17 - Continuous Learning & Adaptive Allocation (pós-R1.0)

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

## EPIC E-18 - Self-Healing & High Availability

### US-180 Quero SafeMode automático.
Tasks: financial-state trust gate; no-new-entry; reconcile; recovery policy; tests.

### US-181 Quero supervised self-healing.
Tasks: restart policy; backoff/jitter; bounded queues; failure injection; unattended recovery.

### US-182 Quero disaster recovery verificável.
Tasks: backup/restore; corrupt-state scenario; RPO/RTO measurement; runbook automation.

### US-183 Quero primary/standby futuro sem double-trading.
Tasks: leader election design; fencing token; standby state sync; failover simulation; chaos test.

## EPIC E-19 - Closed-loop Autonomous Runtime Integration (R1.0-A2)

O épico fecha a lacuna entre componentes já planejados: lifecycle/readiness,
market-data acionável, ciclo baseline, Risk obrigatório, OMS/projeções,
user-stream/reconciliação, execution planner, self-healing, observabilidade e
promotion ladder. O detalhamento operacional, cinco tasks e três subtasks por
task está em `ClickUp/07-autonomous-runtime-completion.md`.

### US-190…US-199

US-190 lifecycle/readiness; US-191 market-data acionável; US-192 ciclo baseline;
US-193 risk obrigatório; US-194 OMS/projeções; US-195 user stream e
reconciliação; US-196 execution planner; US-197 self-healing/fail-safe;
US-198 prova de autonomia; US-199 Paper→Shadow→Testnet com gates e rollback.

**Critério de entrada:** nenhuma story é Ready sem contrato, invariantes,
dependências, comandos de validação e condição explícita de não concluído.
**Critério de saída do épico:** A2 opera em Testnet com reconciliação, account
stream, RiskPolicy obrigatória, OMS/projeções idempotentes e evidence bundle.

Registro de tasks do E-19 (o detalhamento pai/subtask fica no pacote ClickUp):
`TASK-US-190-01`, `TASK-US-190-02`, `TASK-US-190-03`, `TASK-US-190-04`, `TASK-US-190-05`;
`TASK-US-191-01`, `TASK-US-191-02`, `TASK-US-191-03`, `TASK-US-191-04`, `TASK-US-191-05`;
`TASK-US-192-01`, `TASK-US-192-02`, `TASK-US-192-03`, `TASK-US-192-04`, `TASK-US-192-05`;
`TASK-US-193-01`, `TASK-US-193-02`, `TASK-US-193-03`, `TASK-US-193-04`, `TASK-US-193-05`;
`TASK-US-194-01`, `TASK-US-194-02`, `TASK-US-194-03`, `TASK-US-194-04`, `TASK-US-194-05`;
`TASK-US-195-01`, `TASK-US-195-02`, `TASK-US-195-03`, `TASK-US-195-04`, `TASK-US-195-05`;
`TASK-US-196-01`, `TASK-US-196-02`, `TASK-US-196-03`, `TASK-US-196-04`, `TASK-US-196-05`;
`TASK-US-197-01`, `TASK-US-197-02`, `TASK-US-197-03`, `TASK-US-197-04`, `TASK-US-197-05`;
`TASK-US-198-01`, `TASK-US-198-02`, `TASK-US-198-03`, `TASK-US-198-04`, `TASK-US-198-05`;
`TASK-US-199-01`, `TASK-US-199-02`, `TASK-US-199-03`, `TASK-US-199-04`, `TASK-US-199-05`.
