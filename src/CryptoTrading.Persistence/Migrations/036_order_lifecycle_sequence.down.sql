DROP INDEX IF EXISTS trading.order_lifecycle_events_client_sequence_idx;
DROP INDEX IF EXISTS trading.order_lifecycle_events_sequence_idx;
ALTER TABLE trading.order_lifecycle_events DROP COLUMN IF EXISTS journal_sequence;
DELETE FROM trading.schema_migrations WHERE version = '036';
