-- Rollback is destructive to dead-letter state; export evidence before applying.
DROP INDEX IF EXISTS trading.outbox_dead_letter_idx;
ALTER TABLE trading.outbox_messages DROP COLUMN IF EXISTS dead_lettered_at;
DELETE FROM trading.schema_migrations WHERE version = '004';
