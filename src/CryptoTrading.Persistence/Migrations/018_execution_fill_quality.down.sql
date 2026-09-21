ALTER TABLE trading.execution_fills
    DROP CONSTRAINT IF EXISTS execution_fills_execution_quality_status_valid,
    DROP CONSTRAINT IF EXISTS execution_fills_decision_to_fill_latency_non_negative,
    DROP CONSTRAINT IF EXISTS execution_fills_cumulative_fill_ratio_valid,
    DROP CONSTRAINT IF EXISTS execution_fills_quality_benchmark_complete,
    DROP COLUMN IF EXISTS execution_quality_reason,
    DROP COLUMN IF EXISTS execution_quality_status,
    DROP COLUMN IF EXISTS decision_to_fill_latency_ms,
    DROP COLUMN IF EXISTS implementation_shortfall_quote,
    DROP COLUMN IF EXISTS cumulative_fill_ratio,
    DROP COLUMN IF EXISTS quality_decision_at,
    DROP COLUMN IF EXISTS quality_authorized_quantity,
    DROP COLUMN IF EXISTS quality_reference_price;

DELETE FROM trading.schema_migrations WHERE version = '018';
