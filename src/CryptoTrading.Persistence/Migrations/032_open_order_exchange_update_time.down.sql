ALTER TABLE trading.reconciliation_open_orders
    DROP COLUMN IF EXISTS exchange_updated_at;

DELETE FROM trading.schema_migrations WHERE version = '032';
