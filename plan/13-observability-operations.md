# 13 - Observabilidade e Operações

## OpenTelemetry

Usar OTel para traces, metrics e logs estruturados. Context propagation deve atravessar sinal → risco → ordem → fill.

## Métricas de sistema

- CPU/RAM/GC;
- thread-pool starvation;
- channel depth/backpressure;
- request latency;
- websocket disconnect/reconnect;
- DB pool/latency;
- error rate.

## Métricas trading

- market data age;
- sequence gaps;
- decision latency;
- orders submitted/accepted/rejected;
- partial-fill duration;
- slippage expected/actual;
- spread/liquidity;
- exposure;
- PnL/DD;
- circuit breaker state;
- reconciliation mismatch count;
- Strategy Health.

## Alert severities

- `INFO`: operação normal relevante.
- `WARN`: degradação sem risco imediato.
- `ERROR`: funcionalidade afetada.
- `CRITICAL`: capital/estado/risk controls potencialmente comprometidos.

## SLOs iniciais

- Trading Worker availability: 99,5% após live.
- Market data stale: zero novas entradas enquanto acima de threshold.
- Reconciliation mismatch não explicado: zero operação nova.
- Critical alert delivery: alvo < 1 min quando canal permitir.

## Dashboards

1. System Health.
2. Trading Operations.
3. Risk.
4. Strategies/Regime.
5. Research comparison.
6. Costs/Execution attribution.

## Deploy

Blue/green ou rolling simples só depois de haver mais de uma instância segura. Em single-node, usar release versionada + backup + rollback rápido. Nunca reiniciar durante ordem crítica sem reconciliar no startup.

## Incident management

Incidentes P0/P1 exigem template, timeline, impacto, root cause, ações corretivas e prevenção. Não culpar operador; corrigir sistema/processo.
