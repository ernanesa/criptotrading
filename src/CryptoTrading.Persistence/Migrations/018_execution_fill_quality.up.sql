-- Immutable per-fill execution-quality evidence. Missing historical benchmarks
-- remain explicit rather than being represented as a synthetic zero.
ALTER TABLE trading.execution_fills
    ADD COLUMN quality_reference_price numeric(38, 18) NULL,
    ADD COLUMN quality_authorized_quantity numeric(38, 18) NULL,
    ADD COLUMN quality_decision_at timestamptz NULL,
    ADD COLUMN cumulative_fill_ratio numeric(38, 18) NULL,
    ADD COLUMN implementation_shortfall_quote numeric(38, 18) NULL,
    ADD COLUMN decision_to_fill_latency_ms bigint NULL,
    ADD COLUMN execution_quality_status varchar(32) NOT NULL DEFAULT 'NOT_COMPARABLE',
    ADD COLUMN execution_quality_reason varchar(128) NOT NULL DEFAULT 'EXECUTION_BENCHMARK_UNAVAILABLE',
    ADD CONSTRAINT execution_fills_quality_benchmark_complete CHECK
    (
        (quality_reference_price IS NULL AND quality_authorized_quantity IS NULL AND quality_decision_at IS NULL)
        OR
        (quality_reference_price > 0 AND quality_authorized_quantity > 0 AND quality_decision_at IS NOT NULL)
    ),
    ADD CONSTRAINT execution_fills_cumulative_fill_ratio_valid CHECK
    (cumulative_fill_ratio IS NULL OR (cumulative_fill_ratio >= 0 AND cumulative_fill_ratio <= 1)),
    ADD CONSTRAINT execution_fills_decision_to_fill_latency_non_negative CHECK
    (decision_to_fill_latency_ms IS NULL OR decision_to_fill_latency_ms >= 0),
    ADD CONSTRAINT execution_fills_execution_quality_status_valid CHECK
    (execution_quality_status IN ('COMPARABLE', 'NOT_COMPARABLE'));

INSERT INTO trading.schema_migrations (version) VALUES ('018') ON CONFLICT (version) DO NOTHING;
