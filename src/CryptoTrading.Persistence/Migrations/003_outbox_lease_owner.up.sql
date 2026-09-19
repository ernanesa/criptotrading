-- CryptoTrading persistence schema, migration 003.
ALTER TABLE trading.outbox_messages
    ADD COLUMN IF NOT EXISTS lease_owner varchar(150) NULL;

CREATE INDEX IF NOT EXISTS outbox_lease_expiry_idx
    ON trading.outbox_messages (locked_until, available_at)
    WHERE processed_at IS NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('003') ON CONFLICT (version) DO NOTHING;
