# Trading observability signals

## Correlation fields

`trace_id`, `span_id`, `correlation_id`, `request_id`, `operation_id`, `order_ref`, `transaction_ref`, `idempotency_key_hash`, `service.name`, `service.version`, `deployment.environment`, `exchange`, and `account_scope`.

## Suggested audit events

`OrderIntentCreated`, `OrderValidated`, `OrderRejected`, `OrderSubmitted`, `ExchangeAcknowledged`, `OrderPartiallyFilled`, `OrderFilled`, `OrderCancelled`, `OrderExpired`, `SettlementRecorded`, `BalanceReconciled`, `ReconciliationMismatch`, and `ManualInterventionRecorded`.

## Suggested metrics

- `trading_order_total{exchange,side,type,outcome}`
- `trading_order_latency_seconds{exchange,operation}`
- `trading_exchange_request_total{exchange,operation,outcome}`
- `trading_exchange_request_duration_seconds{exchange,operation}`
- `trading_reconciliation_mismatch_total{exchange,asset}`
- `trading_queue_lag_seconds{queue}`
- `trading_retry_total{service,operation,reason}`
- `trading_balance_drift_total{account_scope,asset}`

Keep labels bounded. Use logs and traces for high-cardinality identifiers.
