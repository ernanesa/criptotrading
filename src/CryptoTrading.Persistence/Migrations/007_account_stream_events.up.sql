-- CryptoTrading persistence schema, migration 007.
-- Account events are append-only reconciliation evidence.
CREATE TABLE trading.account_stream_events
(
    event_id uuid PRIMARY KEY,
    event_type varchar(64) NOT NULL CHECK (event_type <> ''),
    dedupe_key varchar(200) NOT NULL,
    event_time_ms bigint NOT NULL CHECK (event_time_ms >= 0),
    received_at timestamptz NOT NULL,
    symbol varchar(20) NULL CHECK (symbol IS NULL OR symbol ~ '^[A-Z0-9]{2,20}$'),
    payload jsonb NOT NULL,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[A-Fa-f0-9]{64}$'),
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT account_stream_events_dedupe_unique UNIQUE (event_type, dedupe_key)
);

CREATE INDEX account_stream_events_received_idx
    ON trading.account_stream_events (received_at, event_id);

CREATE INDEX account_stream_events_symbol_idx
    ON trading.account_stream_events (symbol, event_time_ms, event_id)
    WHERE symbol IS NOT NULL;

CREATE TRIGGER account_stream_events_append_only
    BEFORE UPDATE OR DELETE ON trading.account_stream_events
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('007') ON CONFLICT (version) DO NOTHING;
