LOCK TABLE trading.order_lifecycle_events IN ACCESS EXCLUSIVE MODE;

DO $guard$
BEGIN
    IF EXISTS (
        SELECT 1 FROM trading.order_lifecycle_events
        GROUP BY client_order_id, occurred_at HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'LIFECYCLE_HISTORICAL_TIMESTAMP_TIES_REQUIRE_RECONCILIATION';
    END IF;
END $guard$;

ALTER TABLE trading.order_lifecycle_events
    ADD COLUMN journal_sequence bigint GENERATED ALWAYS AS IDENTITY;

CREATE UNIQUE INDEX order_lifecycle_events_sequence_idx
    ON trading.order_lifecycle_events (journal_sequence);
CREATE INDEX order_lifecycle_events_client_sequence_idx
    ON trading.order_lifecycle_events (client_order_id, occurred_at, journal_sequence);

INSERT INTO trading.schema_migrations (version) VALUES ('036') ON CONFLICT (version) DO NOTHING;
