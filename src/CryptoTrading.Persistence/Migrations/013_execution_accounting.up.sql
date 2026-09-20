-- CryptoTrading persistence schema, migration 013.
-- Immutable execution facts plus an average-cost position projection.
CREATE TABLE trading.execution_fills
(
    execution_id varchar(200) PRIMARY KEY,
    trade_id bigint NULL UNIQUE,
    order_id bigint NOT NULL,
    symbol varchar(32) NOT NULL,
    base_asset varchar(20) NOT NULL,
    quote_asset varchar(20) NOT NULL,
    side varchar(4) NOT NULL,
    quantity numeric(38, 18) NOT NULL,
    price numeric(38, 18) NOT NULL,
    commission numeric(38, 18) NOT NULL,
    commission_asset varchar(20) NOT NULL,
    occurred_at timestamptz NOT NULL,
    ledger_entry_id uuid NOT NULL UNIQUE REFERENCES trading.journal_entries(entry_id),
    realized_pnl_quote numeric(38, 18) NULL,
    average_cost_quote numeric(38, 18) NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT execution_fills_trade_id_non_negative CHECK (trade_id IS NULL OR trade_id >= 0),
    CONSTRAINT execution_fills_order_id_non_negative CHECK (order_id >= 0),
    CONSTRAINT execution_fills_symbol_format CHECK (symbol ~ '^[A-Z0-9_]{2,32}$'),
    CONSTRAINT execution_fills_asset_format CHECK (base_asset ~ '^[A-Z0-9]{2,20}$' AND quote_asset ~ '^[A-Z0-9]{2,20}$' AND commission_asset ~ '^[A-Z0-9]{2,20}$'),
    CONSTRAINT execution_fills_side_valid CHECK (side IN ('BUY', 'SELL')),
    CONSTRAINT execution_fills_quantity_positive CHECK (quantity > 0),
    CONSTRAINT execution_fills_price_positive CHECK (price > 0),
    CONSTRAINT execution_fills_commission_non_negative CHECK (commission >= 0)
);
CREATE INDEX execution_fills_symbol_occurred_idx
    ON trading.execution_fills (symbol, occurred_at, execution_id);

CREATE TABLE trading.position_cost_basis
(
    symbol varchar(32) PRIMARY KEY,
    base_asset varchar(20) NOT NULL,
    quote_asset varchar(20) NOT NULL,
    quantity numeric(38, 18) NOT NULL,
    cost_quote numeric(38, 18) NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT position_cost_basis_symbol_format CHECK (symbol ~ '^[A-Z0-9_]{2,32}$'),
    CONSTRAINT position_cost_basis_asset_format CHECK (base_asset ~ '^[A-Z0-9]{2,20}$' AND quote_asset ~ '^[A-Z0-9]{2,20}$'),
    CONSTRAINT position_cost_basis_quantity_non_negative CHECK (quantity >= 0),
    CONSTRAINT position_cost_basis_cost_non_negative CHECK (cost_quote >= 0)
);

CREATE TRIGGER execution_fills_append_only
    BEFORE UPDATE OR DELETE ON trading.execution_fills
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
CREATE TRIGGER position_cost_basis_append_only
    BEFORE DELETE ON trading.position_cost_basis
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('013') ON CONFLICT (version) DO NOTHING;
