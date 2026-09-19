-- Rollback is destructive to account-stream reconciliation evidence; export it first.
DROP TRIGGER IF EXISTS account_stream_events_append_only ON trading.account_stream_events;
DROP INDEX IF EXISTS trading.account_stream_events_symbol_idx;
DROP INDEX IF EXISTS trading.account_stream_events_received_idx;
DROP TABLE IF EXISTS trading.account_stream_events;
DELETE FROM trading.schema_migrations WHERE version = '007';
