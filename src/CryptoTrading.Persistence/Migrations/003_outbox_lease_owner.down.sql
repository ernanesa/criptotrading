DROP INDEX IF EXISTS trading.outbox_lease_expiry_idx;
ALTER TABLE trading.outbox_messages DROP COLUMN IF EXISTS lease_owner;
DELETE FROM trading.schema_migrations WHERE version = '003';
