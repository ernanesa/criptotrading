DROP INDEX IF EXISTS trading.execution_submission_context_correlation_idx;
DROP TABLE IF EXISTS trading.execution_submission_context;

DELETE FROM trading.schema_migrations WHERE version = '019';
