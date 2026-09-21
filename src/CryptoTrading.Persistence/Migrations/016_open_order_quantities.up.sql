ALTER TABLE trading.reconciliation_open_orders
    ADD COLUMN original_quantity numeric(38, 18) NULL,
    ADD COLUMN cumulative_executed_quantity numeric(38, 18) NULL,
    ADD CONSTRAINT reconciliation_open_orders_original_quantity_positive
        CHECK (original_quantity IS NULL OR original_quantity > 0),
    ADD CONSTRAINT reconciliation_open_orders_cumulative_quantity_non_negative
        CHECK (cumulative_executed_quantity IS NULL OR cumulative_executed_quantity >= 0),
    ADD CONSTRAINT reconciliation_open_orders_cumulative_within_original
        CHECK (original_quantity IS NULL OR cumulative_executed_quantity IS NULL OR cumulative_executed_quantity <= original_quantity);

INSERT INTO trading.schema_migrations (version) VALUES ('016') ON CONFLICT (version) DO NOTHING;
