ALTER TABLE trading.reconciliation_open_orders
    DROP CONSTRAINT IF EXISTS reconciliation_open_orders_cumulative_within_original,
    DROP CONSTRAINT IF EXISTS reconciliation_open_orders_cumulative_quantity_non_negative,
    DROP CONSTRAINT IF EXISTS reconciliation_open_orders_original_quantity_positive,
    DROP COLUMN IF EXISTS cumulative_executed_quantity,
    DROP COLUMN IF EXISTS original_quantity;

DELETE FROM trading.schema_migrations WHERE version = '016';
