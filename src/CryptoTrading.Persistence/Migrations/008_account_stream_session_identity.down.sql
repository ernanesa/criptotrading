-- Rollback is destructive to reconnect evidence; export it first.
DROP INDEX IF EXISTS trading.account_stream_events_session_idx;
ALTER TABLE trading.account_stream_events
    DROP CONSTRAINT IF EXISTS account_stream_events_session_identity_check,
    DROP CONSTRAINT IF EXISTS account_stream_events_subscription_id_check,
    DROP CONSTRAINT IF EXISTS account_stream_events_session_generation_check,
    DROP COLUMN IF EXISTS subscription_id,
    DROP COLUMN IF EXISTS session_generation,
    DROP COLUMN IF EXISTS session_id;
DELETE FROM trading.schema_migrations WHERE version = '008';
