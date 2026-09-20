---
name: financial-observability
description: Design, instrument, review, and investigate production observability for financial and trading software using OpenTelemetry, structured logs, metrics, traces, audit events, and latency analysis.
metadata:
  short-description: Observable and auditable trading systems
---

# Financial Observability

Use this skill for trading, payments, portfolios, orders, balances, market data, or other workflows where traceability, latency, correctness, and auditability matter.

## Core signals

- **Structured logs**: JSON logs with event name, severity, service, environment, version, trace ID, span ID, correlation ID, idempotency key hash, order/transaction reference, outcome, and duration.
- **Metrics**: request rate, error rate, latency percentiles, exchange/API latency, order acceptance/rejection, fills, reconciliation gaps, queue lag, retries, rate limits, balance drift, and resource saturation.
- **Traces**: follow an operation across API, domain, database, queue, exchange, and reconciliation boundaries.
- **Audit events**: append-only business events for order intent, validation, submission, acknowledgement, fill, cancellation, settlement, reconciliation, and manual intervention.

Use OpenTelemetry as the instrumentation and transport standard. Keep the audit trail separate from operational logs; audit events must not be lost because a trace was sampled.

## Safety rules

Never log API keys, secrets, private credentials, full personal data, raw authentication tokens, or unnecessary exchange payloads. Hash or tokenize identifiers when correlation is needed. Do not log full order payloads if they expose sensitive strategy or account data. Define retention, access control, clock synchronization, immutable storage, and redaction tests.

## Analysis workflow

1. Identify the incident, operation, time window, environment, and correlation IDs.
2. Start with metrics and latency percentiles, then locate representative traces.
3. Pivot from trace IDs to structured logs and audit events.
4. Compare expected state transitions with observed events.
5. Separate application latency, database latency, network/exchange latency, queue delay, retries, and clock skew.
6. Form a hypothesis, gather evidence, and record confidence.
7. Produce root cause, impact, detection gap, fix, prevention, and validation plan.

## Sampling and retention

Keep all financial audit events and errors. Use tail-based or adaptive sampling for high-volume traces, preserving slow, failed, retried, and financially significant operations. Keep metrics aggregated and low-cardinality; never use order IDs or transaction IDs as unbounded metric labels.

## Tooling baseline

Prefer OpenTelemetry .NET for instrumentation, Prometheus-compatible metrics, Grafana dashboards, Loki for structured operational logs, Tempo for traces, and Grafana Alloy or an OpenTelemetry Collector for routing. Choose storage and retention based on volume, compliance, and recovery requirements.

Read [references/trading-signals.md](references/trading-signals.md) for event and metric names.
