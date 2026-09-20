DROP TABLE IF EXISTS trading.position_cost_basis;
DROP TABLE IF EXISTS trading.execution_fills;
DELETE FROM trading.schema_migrations WHERE version = '013';
