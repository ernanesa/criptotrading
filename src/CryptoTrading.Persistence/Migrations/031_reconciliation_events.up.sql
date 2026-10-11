CREATE TABLE trading.reconciliation_events
(
    event_id uuid PRIMARY KEY,
    correlation_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    event_type varchar(64) NOT NULL,
    schema_version smallint NOT NULL,
    payload jsonb NOT NULL,
    payload_sha256 char(64) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT reconciliation_events_type_not_blank CHECK (length(btrim(event_type)) > 0),
    CONSTRAINT reconciliation_events_schema_version_positive CHECK (schema_version > 0),
    CONSTRAINT reconciliation_events_payload_object CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT reconciliation_events_payload_hash_format CHECK (payload_sha256 ~ '^[0-9A-F]{64}$')
);
CREATE INDEX reconciliation_events_correlation_occurred_idx
    ON trading.reconciliation_events (correlation_id, occurred_at, event_id);
CREATE TRIGGER reconciliation_events_append_only
    BEFORE UPDATE OR DELETE ON trading.reconciliation_events
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('031') ON CONFLICT (version) DO NOTHING;
