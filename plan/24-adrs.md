# 24 - Architecture Decision Records (ADR)

## ADR-001 - Backend .NET 11 / C# 15
**Status:** Accepted.  
**Razão:** alvo moderno, performance/runtime async e horizonte de lançamento posterior ao GA. Pré-GA usa preview pinado; live só após GA estável.

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
**Razão:** AWS learning, Oracle ARM e Hostinger x86 devem ser destinos intercambiáveis.

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

Novas decisões devem usar `templates/adr.md` e podem futuramente ser separadas em `plan/adrs/NNNN-title.md`.
