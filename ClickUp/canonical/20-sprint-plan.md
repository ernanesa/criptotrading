# 20 - Plano de Sprints

## Cadência

Sprints de 2 semanas. Como o projeto será desenvolvido um pouco por dia, cada Sprint deve produzir um incremento pequeno, demonstrável e Done. Datas abaixo são previsão inicial a partir de setembro/2026.

| Sprint | Objetivo |
|---|---|
| S00 | Planejamento, ADRs, backlog e repo governance |
| S01 | Solution .NET 11/C# 15 + React/Vite skeleton, SDK pinado, CI e Compose local |
| S02 | Domain primitives, clock, journal, ledger, PostgreSQL e OTel |
| S03 | Binance exchangeInfo + REST foundation + rate limits |
| S04 | WebSocket trades/candles + reconnect/data health |
| S05 | Local order book + gap/resync + recorder |
| S06 | User Data Stream + account state |
| S07 | OMS state machine + idempotency + reconciliation |
| S08 | RiskPolicy + PromotionReadinessMatrix + sizing + protections + kill switch |
| S09 | Paper exchange/simulator + analytics core |
| S10 | Historical datasets + deterministic backtest L1 |
| S11 | Trend Following BTC/USDT: spec, baseline e testes |
| S12 | Validation: OOS, walk-forward, sensitivity, Monte Carlo |
| S13 | Paper/shadow da baseline + Strategy Health + NO_TRADE |
| S14 | Execution quality determinística + attribution |
| S15 | React operational/research/risk dashboard + SignalR |
| S16 | Threat model, admin security, observability e runbooks |
| S17 | Backup/restore, chaos e shadow soak |
| S18 | Canary readiness review e CanaryPlan; sem capital |
| S19+ | Canary observation, fixes e gradual promotion somente após aprovação explícita |

AWS, Oracle/VPS, Momentum/Breakout, Mean Reversion/Grid, Regime Detection e multi-strategy não possuem Sprint reservado antes do R1.0. Cloud/hosting só poderá ser planejado como iniciativa separada após decisão explícita; não é consequência automática de o runtime local ficar pronto.

## Rastreabilidade dos Sprints

Cada Sprint deve listar explicitamente US, `TASK-US-XXX-NN`, AC, teste/comando e `EVIDENCE-<id>-<date>`. A ordem mínima é:

| Fase | Dependências obrigatórias |
|---|---|
| S00–S02 | US-001, US-002, US-003, US-010, US-011, US-012, US-013, US-014, US-015 e US-124 |
| S03–S07 | US-020, US-021, US-022, US-023, US-024, US-030, US-031, US-032 e US-033 |
| S08–S09 | US-040, US-041, US-042, US-043, US-044, US-090 e US-180 |
| S10–S12 | US-050, US-051, US-052, US-053, US-054, US-055, US-060 e US-061 |
| S13–S17 | US-073, US-080, US-081, US-082, US-091, US-093, US-094, US-100, US-101, US-102, US-103, US-110, US-111, US-112, US-113, US-114, US-181 e US-182 |
| S18+ | US-092, US-130, US-131, US-132, US-133 e US-134, somente com matriz de evidências e aprovação explícita |

## Sprint Planning policy

Cada Sprint seleciona histórias apenas até 80% da capacidade percebida; 20% fica para bugs, aprendizado e incerteza. Se não houver folga real, reduzir escopo.

## Daily micro-session suggestion

1. 5 min: revisar Sprint Goal/último estado.
2. 45–90 min: uma tarefa focada.
3. 5 min: testes/notas/commit.
4. Atualizar impedimento e próximo passo.

Não é obrigação de tempo; é uma rotina sugerida para preservar continuidade.

## Review evidence

Cada Sprint deve terminar com pelo menos um dos seguintes: teste demonstrável, endpoint funcional, replay, dashboard, relatório de experimento ou deployment reproduzível.
