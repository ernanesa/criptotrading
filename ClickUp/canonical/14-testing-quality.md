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
Binance Testnet real, PostgreSQL, serialization, reconciliação. Mock server só em contract tests isolados; não é substituto da integração Testnet.

### Contract
Payloads Binance, OpenAPI backend/frontend, schema compatibility.

### Replay
Datasets gravados reproduzem comportamento determinístico.

### Parity
Mesmo replay/config produz sinais, targets, filtros, arredondamento e RiskDecisions equivalentes em backtest, paper e shadow dentro de tolerância definida.

### Chaos/fault injection
- disconnect durante partial fill;
- timeout após ordem aceita;
- 429;
- dados fora de ordem;
- restart com posição aberta;
- DB temporariamente indisponível;
- clock drift;
- spread/volatility shock.
- crash entre persistência e publicação de evento;
- outbox/inbox duplicate, lag e poison event;
- audit write failure, redaction failure e alert delivery failure;
- backup corrompido, PITR/restore e verificação de imagem/digest.
- fee em base/quote/BNB, dust, depósito/retirada/trade externo e stablecoin depeg;
- Channel overflow por classe, shutdown durante outbox publish e poison event;
- fill antes do ack, eventos duplicados/reordenados e cancel-replace parcial;
- kill switch com ordem em voo e UI/API indisponível;
- holdout contaminado e dataset corrigido depois do experimento.

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
- SBOM, provenance/signature verification, image digest e OIDC policy quando aplicável.

## Release gates

Live exige testnet/paper/shadow evidence, migration test, restore test, runbook e rollback.

Nenhum gate é considerado aprovado sem comando e saída observada. Cada AC crítico deve apontar para teste, boundary e evidência.

Canary exige `RiskPolicy`, `PromotionReadinessMatrix`, `CanaryPlan`, `ThreatModel`, `ReconciliationReport` e `ReleaseEvidence` completos, versionados, aprovados e vinculados no manifesto de rastreabilidade.
