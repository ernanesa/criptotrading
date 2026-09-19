# 04 - Arquitetura

## Estilo

**Modular Monolith + Event-Driven internamente + Ports & Adapters.**

Microservices não serão usados inicialmente. Processos podem ser separados operacionalmente quando houver benefício claro.

## Fluxo principal autônomo

```text
Binance
  ↓
Market Data → Data Quality → Feature/State → Regime
                                          ↓
                                Strategy Candidates
                                          ↓
                                Autonomy Orchestrator
                                / Strategy Selector
                                          ↓
                                     Alpha/Intent
                                          ↓
                              Portfolio Construction
                                          ↓
                                  Risk Engine
                                          ↓
                             Adaptive Execution
                                          ↓
                                        OMS
                                          ↓
                                      Binance
                                          ↓
                           Outcome / Attribution / Drift
                                          └──────── feedback → Autonomy/Learning
```

## Regra arquitetural principal

`Strategy/Selector -> Intent -> Portfolio -> Risk -> Execution -> OMS -> Exchange`

Nenhum módulo de Strategy, ML ou Autonomy recebe credencial direta da exchange ou permissão para contornar Risk/OMS.

## Autonomy Orchestrator

Responsável por:
- construir contexto atual;
- filtrar estratégias elegíveis e saudáveis;
- ranquear candidatos por utilidade líquida esperada;
- coordenar alocação dinâmica de risk budget;
- escolher `NO_TRADE` quando necessário;
- reagir a mudanças de regime, drift e Strategy Health;
- registrar decisão explicável e auditável.

O Orchestrator não aumenta hard risk limits e não envia ordens.

No R1.0 há uma única baseline elegível; o Orchestrator funciona como policy/eligibility gate determinístico e pode resultar em `NO_TRADE`. Ranking e alocação multi-strategy só são ativados após R1.0.

## Learning Control Plane

Treino/retraining/optimization não acontece no hot path. O fluxo é:

`Telemetry/Data -> Drift -> Train/Tune -> Validate -> Challenger -> Paper/Shadow -> Canary -> Champion/Rollback`.

Model/Strategy Registry guarda lineage completo. Aprendizado online rápido pode atualizar scores/posteriors/alocação dentro de envelopes aprovados; mudanças estruturais passam pelo pipeline de promoção.

## Bounded modules

- `Domain`: value objects, enums, invariantes e eventos neutros.
- `MarketData`: streams, books, candles, quality/gaps.
- `Exchange.Abstractions`: contratos de exchange.
- `Exchange.Binance`: adapter Binance.
- `Strategies`: estratégias e SDK.
- `Regimes`: classificação de mercado com confiança.
- `Autonomy`: selector, eligibility, utility scoring, adaptive allocation e no-trade policy.
- `Portfolio`: combinação/netting e targets.
- `Risk`: limites e protections.
- `Execution`: algoritmos de execução e selection de tática.
- `Orders`: OMS/state machine/reconciliation.
- `Accounting`: ledger por ativo, balances, fees, cost basis, valuation e PnL.
- `Learning`: drift, model registry, champion-challenger e promotion policy.
- `Research`: datasets, experiments, validation.
- `Backtesting`: event simulation.
- `Analytics`: métricas, strategy health e attribution.
- `Persistence`: PostgreSQL/Parquet.
- `Operations`: health, alerts, safe mode, self-healing e kill switch.
- `Api`: control/read API.
- `Worker`: live trading process.
- `Web`: React frontend.

## Processos previstos

### Trading Worker
Hot path, conexões Binance, strategies, autonomy, portfolio, risk, execution e OMS.

### API
Control plane e queries para UI. Comandos live passam por autorização e audit.

### Web
SPA React servida estaticamente.

### Research/Learning Runner
Backtests, optimization, drift analysis, training e challenger evaluation isolados para não competir com live por CPU/RAM.

## Loops temporais

- **Fast loop:** market state, regime, strategy eligibility, allocation, risk e execution.
- **Medium loop:** rolling metrics, Strategy Health, drift e recalibração bounded.
- **Slow loop:** retraining, optimization, walk-forward, stress, challenger/promotion.

Essa separação impede que ruído de curto prazo provoque mutações grandes em produção.

## Mensageria interna

Começar com `System.Threading.Channels`, tipos imutáveis/readonly e backpressure explícito. Kafka/RabbitMQ só entram se processos realmente precisarem de broker distribuído.

### Durabilidade por classe de evento

| Classe | Exemplos | Política |
|---|---|---|
| Efêmera/coalescível | tick intermediário, book ticker substituído | pode coalescer/descartar apenas com métrica, freshness gate e política explícita |
| Recalculável | candle, feature, snapshot analítico | pode reconstruir da fonte versionada; persistência assíncrona controlada |
| Decisão auditável | signal, target, Strategy Health, seleção | journal append-only antes de acionar o próximo side effect crítico |
| Financeira crítica | order intent, risk decision, submit, ack, fill, cancel, reconciliation, manual intervention | nunca descartar; persistência durável, dedupe e recovery obrigatórios |

`Channel` não é armazenamento durável. Comandos financeiros usam persist-before-submit e transactional outbox/inbox ou garantia equivalente. A semântica assumida é at-least-once com consumidores idempotentes; o sistema não promete exactly-once distribuído.

Cada canal declara capacidade, política de overflow, ownership, ordering key, timeout, retry, poison handling e comportamento de shutdown. Saturação de canal financeiro bloqueia novas entradas e pode acionar `SafeMode`.

## Persistência

Não adotar Event Sourcing integral no MVP. Manter:
- tabelas transacionais para estado materializado;
- journal imutável de eventos críticos de trading/auditoria;
- decision traces do Autonomy Orchestrator;
- model/strategy registry;
- snapshots/reconciliação.

Isso fornece replay e auditabilidade sem transformar cada CRUD em evento.

O boundary transacional mínimo associa `OrderIntent`, `RiskDecision`, idempotency key e outbox record antes do envio à exchange. Crash entre persistência, publicação, submit e acknowledgement possui cenários explícitos de recuperação e reconciliação.

## Consistência

A exchange é autoridade externa para ordens/fills/saldos. Estado local é otimizado para decisão, mas divergências levam à reconciliação e, quando necessário, ao bloqueio de novas entradas ou `SafeMode`.

Autoridade externa não significa sobrescrever o histórico local: correções financeiras são lançamentos compensatórios auditáveis. A exchange não fornece uma posição Spot canônica; posição, cost basis e PnL são projeções internas derivadas de ledger, fills, fees e valuation.

## Paridade research/paper/live

Strategy, Portfolio, RiskPolicy, filtros, arredondamento, OMS e regras de execução devem reutilizar o mesmo código entre backtest, paper, shadow e live. Variam apenas clock, data source e exchange/execution adapter. Divergência intencional exige ADR, teste e attribution report.

## Resiliência

- supervised restart;
- reconnect/backoff/jitter;
- idempotência;
- circuit breakers;
- bounded queues/backpressure;
- startup/reconnect reconciliation;
- safe-mode fallback;
- isolamento entre trading e research;
- evolução futura para primary/standby com leader election/fencing.

## Deployment portability

Toda lógica de negócio roda inicialmente nesta máquina por containers Linux e Docker Compose. Imagens multi-arch preservam portabilidade, mas AWS/Oracle/Hostinger são apenas destinos futuros possíveis, não fazem parte do ambiente ativo nem são dependências do domínio.
