# 13 - Observabilidade e Operações

## OpenTelemetry

Usar OTel para traces, metrics e logs estruturados. Context propagation deve atravessar sinal → risco → ordem → fill.

O journal financeiro/audit não é um log OTel: deve ser append-only, persistido com schema versionado e não pode ser perdido por sampling. Logs operacionais, traces e métricas têm retenção, ACL e redaction próprios.

Campos mínimos do audit event: `event_id`, `event_type`, `schema_version`, `occurred_at`, `ingested_at`, `environment`, `exchange`, `account_scope`, `actor`, `correlation_id`, `causation_id`, `idempotency_key_hash`, `aggregate_id`, `before/after` quando aplicável, motivo, policy/config version e payload hash.

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
- exchange request/ack/fill latency por estágio;
- rate-limit usage/retry-after e outbox/inbox lag;
- reconciliation age, unknown order, balance drift e audit write failure.

Métricas usam labels bounded; order IDs, clientOrderIds, correlation IDs e outros identificadores de alta cardinalidade ficam em logs/traces/audit, não em labels. Nomes e atributos adotam OpenTelemetry Semantic Conventions versionadas quando aplicáveis.

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

Cada SLO declara SLI, janela, fórmula, fonte, exclusions, error budget, owner e ação ao consumir budget. Disponibilidade do processo não compensa violação de correção financeira. SLIs prioritários: financial-state trust, market-data freshness, reconciliation age, order-intent durability, audit durability e alert delivery.

## Dashboards

1. System Health.
2. Trading Operations.
3. Risk.
4. Strategies/Regime.
5. Research comparison.
6. Costs/Execution attribution.

## Deploy

Blue/green ou rolling simples só depois de haver mais de uma instância segura. Em single-node, usar release versionada + backup + rollback rápido. Nunca reiniciar durante ordem crítica sem reconciliar no startup.

Exporters e alerting devem ser decididos por ambiente: OTLP Collector, Prometheus-compatible metrics, Loki/structured logs e Tempo/traces ou equivalentes. Alertas precisam de regra, janela, deduplicação, routing, ownership, escalation, ação sugerida e teste de entrega.

Synthetic probes verificam market-data freshness, endpoint read-only, persistência do journal, alert delivery e readiness sem enviar ordem live. Tail/adaptive sampling preserva traces falhos, lentos, retried e financeiramente relevantes; audit events e erros nunca dependem de sampling.

## Incident management

Incidentes P0/P1 exigem template, timeline, impacto, root cause, ações corretivas e prevenção. Não culpar operador; corrigir sistema/processo.
