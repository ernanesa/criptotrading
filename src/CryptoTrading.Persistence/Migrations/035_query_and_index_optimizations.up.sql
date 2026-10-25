-- Optimize outbox partial indexes to exclude dead-lettered messages and add covering B-Tree indexes for hot queries.
DROP INDEX IF EXISTS trading.outbox_dispatch_idx;
CREATE INDEX outbox_dispatch_idx
    ON trading.outbox_messages (available_at, created_at)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

DROP INDEX IF EXISTS trading.outbox_lease_expiry_idx;
CREATE INDEX outbox_lease_expiry_idx
    ON trading.outbox_messages (locked_until, available_at)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

CREATE INDEX IF NOT EXISTS portfolio_nav_snapshots_quote_observed_idx
    ON trading.portfolio_nav_snapshots (quote_asset, observed_at DESC);

CREATE INDEX IF NOT EXISTS autonomy_decisions_symbol_close_idx
    ON trading.autonomy_decisions (symbol, close_time DESC);

CREATE INDEX IF NOT EXISTS execution_replan_candidates_created_idx
    ON trading.execution_replan_candidates (created_at, candidate_id);

CREATE INDEX IF NOT EXISTS reconciliation_events_occurred_idx
    ON trading.reconciliation_events (occurred_at DESC, event_id);

INSERT INTO trading.schema_migrations (version) VALUES ('035') ON CONFLICT (version) DO NOTHING;
