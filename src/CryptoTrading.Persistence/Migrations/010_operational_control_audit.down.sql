-- Rollback is destructive to KillSwitch/SafeMode evidence; export it first.
DROP TRIGGER IF EXISTS operational_control_audit_append_only ON trading.operational_control_audit_events;
DROP INDEX IF EXISTS trading.operational_control_audit_occurred_idx;
DROP TABLE IF EXISTS trading.operational_control_audit_events;
DELETE FROM trading.schema_migrations WHERE version = '010';
