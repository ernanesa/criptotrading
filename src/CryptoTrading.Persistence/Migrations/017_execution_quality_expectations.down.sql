ALTER TABLE trading.execution_fee_expectations
    DROP CONSTRAINT IF EXISTS execution_fee_expectations_authorized_quantity_positive,
    DROP CONSTRAINT IF EXISTS execution_fee_expectations_reference_price_positive,
    DROP COLUMN IF EXISTS decision_at,
    DROP COLUMN IF EXISTS authorized_quantity,
    DROP COLUMN IF EXISTS reference_price;

DELETE FROM trading.schema_migrations WHERE version = '017';
