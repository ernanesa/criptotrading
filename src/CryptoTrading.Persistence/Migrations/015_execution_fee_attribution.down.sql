-- Rollback removes immutable attribution evidence; export it before applying.
DROP INDEX IF EXISTS trading.execution_fills_fee_drift_idx;
DROP INDEX IF EXISTS trading.execution_fills_client_order_idx;
ALTER TABLE trading.execution_fills
    DROP CONSTRAINT IF EXISTS execution_fills_fee_validity,
    DROP CONSTRAINT IF EXISTS execution_fills_fee_attribution_complete,
    DROP CONSTRAINT IF EXISTS execution_fills_fee_status_valid,
    DROP CONSTRAINT IF EXISTS execution_fills_fee_rate_non_negative,
    DROP COLUMN IF EXISTS commission_valuation_at,
    DROP COLUMN IF EXISTS commission_valuation_source,
    DROP COLUMN IF EXISTS fee_comparison_reason,
    DROP COLUMN IF EXISTS fee_comparison_status,
    DROP COLUMN IF EXISTS fee_deviation_bps,
    DROP COLUMN IF EXISTS actual_commission_quote,
    DROP COLUMN IF EXISTS expected_commission_quote,
    DROP COLUMN IF EXISTS fee_valid_until,
    DROP COLUMN IF EXISTS fee_observed_at,
    DROP COLUMN IF EXISTS fee_source,
    DROP COLUMN IF EXISTS fee_snapshot_hash,
    DROP COLUMN IF EXISTS estimated_rate,
    DROP COLUMN IF EXISTS client_order_id;
DROP TRIGGER IF EXISTS execution_fee_expectations_append_only ON trading.execution_fee_expectations;
DROP TABLE IF EXISTS trading.execution_fee_expectations;
