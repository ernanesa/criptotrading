-- CryptoTrading persistence schema, migration 004.
ALTER TABLE trading.outbox_messages
    ADD COLUMN IF NOT EXISTS dead_lettered_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS outbox_dead_letter_idx
    ON trading.outbox_messages (dead_lettered_at, available_at)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('004') ON CONFLICT (version) DO NOTHING;
