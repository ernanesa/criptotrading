# 20 - Plano de Sprints

## Cadência

Sprints de 2 semanas. Como o projeto será desenvolvido um pouco por dia, cada Sprint deve produzir um incremento pequeno, demonstrável e Done. Datas abaixo são previsão inicial a partir de setembro/2026.

| Sprint | Objetivo |
|---|---|
| S00 | Planejamento, ADRs, backlog e repo governance |
| S01 | Solution .NET 11 + React/Vite skeleton, CI e containers |
| S02 | Domain primitives, clock, events, PostgreSQL e OTel |
| S03 | Binance exchangeInfo + REST foundation + rate limits |
| S04 | WebSocket trades/candles + reconnect/data health |
| S05 | Local order book + gap/resync + recorder |
| S06 | User Data Stream + account state |
| S07 | OMS state machine + idempotency + reconciliation |
| S08 | Risk Policy + sizing + first protections + kill switch |
| S09 | Paper exchange/simulator + analytics core |
| S10 | Historical datasets + deterministic backtest L1 |
| S11 | Trend/Momentum/Breakout specs e baselines |
| S12 | Validation: OOS, walk-forward, sensitivity, Monte Carlo |
| S13 | Mean Reversion + Adaptive Grid |
| S14 | Regime detector + portfolio netting/multi-strategy |
| S15 | Strategy Health + execution quality/edge filter |
| S16 | React operational dashboard + SignalR |
| S17 | Research/risk dashboards + UX hardening |
| S18 | AWS deployment laboratory + IaC + backup/restore |
| S19 | Shadow trading + chaos scenarios + security hardening |
| S20 | Canary readiness review; somente executar se gates passarem |
| S21+ | Canary observation, fixes, gradual promotion |

## Rastreabilidade dos Sprints

Cada Sprint deve listar explicitamente US, `TASK-US-XXX-NN`, AC, teste/comando e `EVIDENCE-<id>-<date>`. A ordem mínima é:

| Fase | Dependências obrigatórias |
|---|---|
| S00–S02 | US-001..012, journal, correlação, CI e rastreabilidade |
| S03–S07 | US-020..033, filtros, user-data, OMS e reconciliação |
| S08–S09 | US-040..043, SafeMode, kill switch, restore e RiskPolicy |
| S10–S12 | US-050..061, UniversePolicy, custos e baseline BTC/USDT |
| S13–S19 | US-070..111, somente após gates quantitativos/operacionais |
| S20+ | US-130..133, somente com matriz de evidências e aprovação explícita |

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
