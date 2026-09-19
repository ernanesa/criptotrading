-- CryptoTrading persistence schema, migration 008.
-- Preserve the WebSocket connection identity required for reconnect recovery.
ALTER TABLE trading.account_stream_events
    ADD COLUMN session_id uuid NULL,
    ADD COLUMN session_generation bigint NULL,
    ADD COLUMN subscription_id bigint NULL;

ALTER TABLE trading.account_stream_events
    ADD CONSTRAINT account_stream_events_session_generation_check
        CHECK (session_generation IS NULL OR session_generation > 0),
    ADD CONSTRAINT account_stream_events_subscription_id_check
        CHECK (subscription_id IS NULL OR subscription_id >= 0),
    ADD CONSTRAINT account_stream_events_session_identity_check
        CHECK ((session_id IS NULL AND session_generation IS NULL)
            OR (session_id IS NOT NULL AND session_generation IS NOT NULL));

CREATE INDEX account_stream_events_session_idx
    ON trading.account_stream_events
       (session_id, session_generation, subscription_id, event_time_ms, event_id)
    WHERE session_id IS NOT NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('008') ON CONFLICT (version) DO NOTHING;
