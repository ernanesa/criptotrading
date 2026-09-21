ALTER TABLE trading.execution_fee_expectations
    ADD COLUMN reference_price numeric(38, 18) NOT NULL DEFAULT 0,
    ADD COLUMN authorized_quantity numeric(38, 18) NOT NULL DEFAULT 0,
    ADD COLUMN decision_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    ADD CONSTRAINT execution_fee_expectations_reference_price_positive CHECK (reference_price > 0),
    ADD CONSTRAINT execution_fee_expectations_authorized_quantity_positive CHECK (authorized_quantity > 0);

INSERT INTO trading.schema_migrations (version) VALUES ('017') ON CONFLICT (version) DO NOTHING;
