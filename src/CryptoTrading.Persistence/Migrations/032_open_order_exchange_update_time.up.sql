ALTER TABLE trading.reconciliation_open_orders
    ADD COLUMN exchange_updated_at timestamptz NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('032') ON CONFLICT (version) DO NOTHING;
