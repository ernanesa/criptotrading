# 14 - Testes e Qualidade

## Pirâmide adaptada ao domínio

### Unit
Value objects, indicators, sizing, risk rules, state machines.

### Property/Invariants
- tamanho nunca negativo;
- filtros de exchange sempre respeitados;
- Risk nunca aumenta target sem regra explícita;
- filled quantity nunca excede order quantity;
- idempotency invariants.

### Integration
Binance Testnet/mock server, PostgreSQL, serialization, reconciliação.

### Contract
Payloads Binance, OpenAPI backend/frontend, schema compatibility.

### Replay
Datasets gravados reproduzem comportamento determinístico.

### Chaos/fault injection
- disconnect durante partial fill;
- timeout após ordem aceita;
- 429;
- dados fora de ordem;
- restart com posição aberta;
- DB temporariamente indisponível;
- clock drift;
- spread/volatility shock.

### Backtest validation
Lookahead analysis, data leakage tests, cost model tests, known synthetic datasets.

### Frontend
Vitest/Testing Library + Playwright para jornadas críticas e kill-switch UX.

### Performance
BenchmarkDotNet e load tests em paths críticos.

## Coverage

Coverage é indicador, não meta isolada. Código financeiro/risco/OMS deve ter cobertura de cenário e invariantes muito alta. PR não pode usar cobertura elevada para compensar ausência de testes de falha.

## CI gates

- restore/build;
- format/analyzers;
- unit/property tests;
- integration selecionada;
- frontend typecheck/lint/test/build;
- dependency/security scan;
- container build multi-arch ou validação periódica;
- benchmark gate para mudanças marcadas como hot-path.

## Release gates

Live exige testnet/paper/shadow evidence, migration test, restore test, runbook e rollback.
