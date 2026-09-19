# 24 - Architecture Decision Records (ADR)

## ADR-001 - Backend .NET 11 / C# 15
**Status:** Accepted.
**Razão:** stack obrigatória desde o primeiro commit de implementação, visando runtime moderno, performance/runtime async e horizonte de lançamento posterior ao GA. Pré-GA usa RC/go-live oficial pinado; incompatibilidade de biblioteca deve ser resolvida por alternativa, isolamento ou adiamento da feature, nunca por fallback para .NET 10. Live com capital só após GA estável.

## ADR-002 - React + TypeScript + Vite
**Status:** Accepted.
**Razão:** SPA administrativa realtime; React 19.2 atual e Vite 8/Rolldown oferecem ecossistema e build moderno.

## ADR-003 - Modular Monolith first
**Status:** Accepted.
**Razão:** menor complexidade operacional, excelente para projeto individual, boundaries podem virar processos futuramente.

## ADR-004 - Event-driven in-process via Channels
**Status:** Accepted.
**Razão:** baixa overhead, backpressure e tipagem; broker externo somente com necessidade distribuída.

## ADR-005 - Native Binance adapter
**Status:** Accepted.
**Razão:** controle de recursos específicos, performance e ciclo de API. CCXT é referência, não core dependency.

## ADR-006 - Spot first
**Status:** Accepted.
**Razão:** reduzir risco e complexidade. Futures adiciona liquidation, leverage, funding e margin.

## ADR-007 - Strategy cannot place orders
**Status:** Accepted.
**Razão:** separação de responsabilidades e proteção central de capital.

## ADR-008 - PostgreSQL + Parquet + DuckDB
**Status:** Accepted.
**Razão:** transactional reliability + formato analítico portátil + pesquisa eficiente sem cluster.

## ADR-009 - Docker multi-arch/provider agnostic
**Status:** Accepted.
**Razão:** Docker Compose é o contrato do ambiente local, e imagens multi-arch evitam acoplamento ao hardware atual. Um hosting externo pode ser avaliado futuramente sem transformar qualquer provedor em requisito presente.

## ADR-010 - OpenTelemetry
**Status:** Accepted.
**Razão:** traces/metrics/logs vendor-neutral e suporte .NET maduro.

## ADR-011 - No full Event Sourcing initially
**Status:** Accepted.
**Razão:** journal crítico + state tables oferecem auditabilidade suficiente com menor complexidade.

## ADR-012 - Research before ML
**Status:** Accepted.
**Razão:** baseline simples e validação rigorosa são necessários para provar valor incremental de modelos complexos.

## ADR-013 - No Timescale/Redis/Kafka by default
**Status:** Accepted.
**Razão:** dependências adicionais só após métricas demonstrarem gargalo/caso de uso.

## ADR-014 - Two-week Sprints
**Status:** Accepted initially.
**Razão:** adequado a desenvolvimento diário parcial; retrospectiva pode mudar a cadência.

## ADR-015 - R1.0 com uma única baseline
**Status:** Accepted.

**Razão:** o R1.0 prova a cadeia completa com Trend Following BTC/USDT. Multi-strategy, regime adaptativo e continuous learning ficam posteriores para reduzir variáveis, risco e prazo.

## ADR-016 - At-least-once, outbox/inbox e no-drop financeiro
**Status:** Accepted.

**Razão:** Channels não são duráveis. Eventos financeiros exigem persist-before-submit, dedupe e recovery; exactly-once distribuído não será prometido.

## ADR-017 - Ledger append-only por ativo
**Status:** Accepted.

**Razão:** Binance Spot fornece balances/trades/ordens, não posição e PnL canônicos. Ledger, fee asset, valuation e compensating entries tornam estado reconstruível e auditável.

## ADR-018 - Gates externos à configuração da estratégia
**Status:** Accepted.

**Razão:** RiskPolicy, PromotionReadinessMatrix e CanaryPlan versionados definem limites, duração, aprovação, abort e rollback; código adaptativo não altera esses hard boundaries.

## ADR-019 - Ambiente inicial exclusivamente local
**Status:** Accepted.

**Razão:** desenvolvimento, banco, dados, observabilidade, paper e Testnet executam inicialmente nesta máquina com Docker Compose. Nenhum recurso AWS/Oracle/VPS será criado ou exigido pelo roadmap ativo. Cloud só entra por nova decisão explícita, ADR, análise de custo/segurança e plano de migração; portabilidade continua sendo uma qualidade arquitetural, não um deployment atual.

Novas decisões devem usar `templates/adr.md` e podem futuramente ser separadas em `plan/adrs/NNNN-title.md`.
