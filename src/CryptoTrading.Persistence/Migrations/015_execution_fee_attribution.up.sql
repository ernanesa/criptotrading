-- Immutable expected-fee attribution and realized comparison for each fill.
CREATE TABLE trading.execution_fee_expectations
(
    client_order_id varchar(200) PRIMARY KEY,
    order_id uuid NOT NULL,
    symbol varchar(32) NOT NULL,
    side varchar(4) NOT NULL,
    estimated_rate numeric(38, 18) NOT NULL,
    snapshot_hash varchar(128) NOT NULL,
    source varchar(200) NOT NULL,
    observed_at timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT execution_fee_expectations_client_order_not_blank CHECK (length(btrim(client_order_id)) > 0),
    CONSTRAINT execution_fee_expectations_symbol_format CHECK (symbol ~ '^[A-Z0-9_]{2,32}$'),
    CONSTRAINT execution_fee_expectations_side_valid CHECK (side IN ('BUY', 'SELL')),
    CONSTRAINT execution_fee_expectations_rate_non_negative CHECK (estimated_rate >= 0),
    CONSTRAINT execution_fee_expectations_hash_not_blank CHECK (length(btrim(snapshot_hash)) > 0),
    CONSTRAINT execution_fee_expectations_source_not_blank CHECK (length(btrim(source)) > 0),
    CONSTRAINT execution_fee_expectations_validity CHECK (valid_until >= observed_at)
);
CREATE TRIGGER execution_fee_expectations_append_only
    BEFORE UPDATE OR DELETE ON trading.execution_fee_expectations
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

ALTER TABLE trading.execution_fills
    ADD COLUMN client_order_id varchar(200) NULL,
    ADD COLUMN estimated_rate numeric(38, 18) NULL,
    ADD COLUMN fee_snapshot_hash varchar(128) NULL,
    ADD COLUMN fee_source varchar(200) NULL,
    ADD COLUMN fee_observed_at timestamptz NULL,
    ADD COLUMN fee_valid_until timestamptz NULL,
    ADD COLUMN expected_commission_quote numeric(38, 18) NULL,
    ADD COLUMN actual_commission_quote numeric(38, 18) NULL,
    ADD COLUMN fee_deviation_bps numeric(38, 18) NULL,
    ADD COLUMN fee_comparison_status varchar(32) NOT NULL DEFAULT 'NOT_COMPARABLE',
    ADD COLUMN fee_comparison_reason varchar(100) NOT NULL DEFAULT 'FEE_ESTIMATE_UNAVAILABLE',
    ADD COLUMN commission_valuation_source varchar(100) NULL,
    ADD COLUMN commission_valuation_at timestamptz NULL;

ALTER TABLE trading.execution_fills
    ADD CONSTRAINT execution_fills_fee_rate_non_negative CHECK (estimated_rate IS NULL OR estimated_rate >= 0),
    ADD CONSTRAINT execution_fills_fee_status_valid CHECK (fee_comparison_status IN ('NOT_COMPARABLE', 'WITHIN_TOLERANCE', 'DRIFT')),
    ADD CONSTRAINT execution_fills_fee_attribution_complete CHECK
        (estimated_rate IS NULL OR (fee_snapshot_hash IS NOT NULL AND fee_source IS NOT NULL AND fee_observed_at IS NOT NULL AND fee_valid_until IS NOT NULL)),
    ADD CONSTRAINT execution_fills_fee_validity CHECK (fee_valid_until IS NULL OR fee_observed_at IS NULL OR fee_valid_until >= fee_observed_at);
CREATE INDEX execution_fills_client_order_idx
    ON trading.execution_fills (client_order_id) WHERE client_order_id IS NOT NULL;
CREATE INDEX execution_fills_fee_drift_idx
    ON trading.execution_fills (symbol, occurred_at) WHERE fee_comparison_status = 'DRIFT';

INSERT INTO trading.schema_migrations (version) VALUES ('015') ON CONFLICT (version) DO NOTHING;
