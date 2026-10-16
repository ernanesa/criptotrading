ALTER TABLE trading.risk_policies
    DROP COLUMN IF EXISTS budget_policy_json;

DELETE FROM trading.schema_migrations WHERE version = '034';
