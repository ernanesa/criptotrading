-- Destructive rollback: export all research evidence before executing.
DROP TRIGGER IF EXISTS holdout_contamination_events_append_only ON trading.holdout_contamination_events;
DROP TRIGGER IF EXISTS holdout_access_audits_append_only ON trading.holdout_access_audits;
DROP TRIGGER IF EXISTS experiment_trials_append_only ON trading.experiment_trials;
DROP TRIGGER IF EXISTS experiment_preregistrations_append_only ON trading.experiment_preregistrations;
DROP TABLE IF EXISTS trading.holdout_contamination_events;
DROP TABLE IF EXISTS trading.holdout_access_audits;
DROP TABLE IF EXISTS trading.experiment_trials;
DROP TABLE IF EXISTS trading.experiment_preregistrations;
DELETE FROM trading.schema_migrations WHERE version = '011';
