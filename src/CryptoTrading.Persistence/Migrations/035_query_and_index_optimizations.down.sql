DROP INDEX IF EXISTS trading.reconciliation_events_occurred_idx;
DROP INDEX IF EXISTS trading.execution_replan_candidates_created_idx;
DROP INDEX IF EXISTS trading.autonomy_decisions_symbol_close_idx;
DROP INDEX IF EXISTS trading.portfolio_nav_snapshots_quote_observed_idx;

DROP INDEX IF EXISTS trading.outbox_lease_expiry_idx;
CREATE INDEX outbox_lease_expiry_idx
    ON trading.outbox_messages (locked_until, available_at)
    WHERE processed_at IS NULL;

DROP INDEX IF EXISTS trading.outbox_dispatch_idx;
CREATE INDEX outbox_dispatch_idx
    ON trading.outbox_messages (available_at, created_at)
    WHERE processed_at IS NULL;

DELETE FROM trading.schema_migrations WHERE version = '035';
