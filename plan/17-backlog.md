# 17 - Product Backlog

O backlog abaixo é inicial e ordenado por dependência/risco. IDs são estáveis; detalhes podem evoluir.

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

## EPIC E07 - Regime, Portfolio & Multi-Strategy

### US-070 Quero detectar regimes explicáveis.
Tasks: trend/volatility/liquidity features; classifier; confidence; tests.

### US-071 Quero combinar sinais e fazer netting.
Tasks: intent aggregation; conflict rules; target builder.

### US-072 Quero risk budgets por estratégia.
Tasks: allocation model; caps; strategy health integration.

### US-073 Quero Strategy Health automático.
Tasks: rolling metrics; state transitions; quarantine/re-enable policy.

## EPIC E08 - Execution Quality

### US-080 Quero estimar edge líquido antes de ordenar.
Tasks: fees/spread/slippage estimator; threshold policy.

### US-081 Quero passive/aggressive execution.
Tasks: market vs limit decision; cancel stale; fill tracking.

### US-082 Quero execution attribution.
Tasks: arrival price; implementation shortfall; expected vs realized slippage.

## EPIC E09 - Paper, Shadow & Canary

### US-090 Quero paper trading com mercado real.
Tasks: simulator; virtual balances; same risk/execution interfaces.

### US-091 Quero shadow trading.
Tasks: full decision pipeline; no exchange send; comparison report.

### US-092 Quero canary controls.
Tasks: tiny capital policy; reduced symbols; stricter limits; rollback.

## EPIC E10 - Web Dashboard

### US-100 Quero shell React type-safe.
Tasks: Vite; Router; Query; auth shell; generated API types.

### US-101 Quero realtime trading dashboard.
Tasks: SignalR; market/positions/orders/PnL panels.

### US-102 Quero risk dashboard e kill switch.
Tasks: limits/breakers; confirmations; audit view.

### US-103 Quero research dashboard.
Tasks: experiments; metrics; equity/DD; parameter sensitivity.

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
Tasks: reliability; execution quality; strategy performance; backlog update.

## EPIC E14 - Futures & Hedging (pós-1.0)
US-140 Futures adapter; US-141 leverage/margin/liquidation risk; US-142 shorts; US-143 hedging; US-144 funding/basis.

## EPIC E15 - Market Neutral & Arbitrage (pós-1.0)
US-150 pairs/stat-arb; US-151 triangular arb; US-152 multi-exchange adapter; US-153 XEMM/cross-exchange.

## EPIC E16 - Market Making Advanced (pós-1.0)
US-160 L3 order-book simulator; US-161 inventory-aware MM; US-162 Avellaneda-Stoikov research; US-163 adverse-selection controls.

## EPIC E17 - ML & Adaptive Allocation (futuro)
US-170 feature store/registry; US-171 regime ML; US-172 slippage model; US-173 meta-allocation/MoE; US-174 concept drift monitoring.
