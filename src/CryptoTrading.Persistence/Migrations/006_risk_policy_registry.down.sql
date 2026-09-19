-- Rollback is destructive to approved risk-policy evidence; export it first.
DROP TRIGGER IF EXISTS risk_policy_events_append_only ON trading.risk_policy_events;
DROP TRIGGER IF EXISTS risk_policies_append_only ON trading.risk_policies;
DROP INDEX IF EXISTS trading.risk_policy_events_active_idx;
DROP INDEX IF EXISTS trading.risk_policy_events_latest_idx;
DROP TABLE IF EXISTS trading.risk_policy_events;
DROP TABLE IF EXISTS trading.risk_policies;
DELETE FROM trading.schema_migrations WHERE version = '006';
