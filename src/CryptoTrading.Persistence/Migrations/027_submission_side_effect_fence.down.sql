-- Rollback requires the runtime to be stopped and reconciled; removal loses submit fencing evidence.
DROP TABLE IF EXISTS trading.execution_submission_starts;
DELETE FROM trading.schema_migrations WHERE version = '027';
