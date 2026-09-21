DROP TRIGGER IF EXISTS order_lifecycle_events_append_only ON trading.order_lifecycle_events;
DROP INDEX IF EXISTS trading.order_lifecycle_events_client_occurred_idx;
DROP TABLE IF EXISTS trading.order_lifecycle_events;
DELETE FROM trading.schema_migrations WHERE version = '021';
