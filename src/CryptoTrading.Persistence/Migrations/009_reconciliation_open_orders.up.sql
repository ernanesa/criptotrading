CREATE TABLE trading.reconciliation_open_orders
(
    client_order_id varchar(36) PRIMARY KEY,
    symbol varchar(20) NOT NULL,
    order_id bigint NULL,
    status varchar(32) NOT NULL,
    updated_at timestamptz NOT NULL,
    active boolean NOT NULL DEFAULT TRUE,
    CONSTRAINT reconciliation_open_orders_symbol_format CHECK (symbol ~ '^[A-Z0-9]{2,20}$'),
    CONSTRAINT reconciliation_open_orders_client_not_blank CHECK (length(btrim(client_order_id)) > 0),
    CONSTRAINT reconciliation_open_orders_status_not_blank CHECK (length(btrim(status)) > 0),
    CONSTRAINT reconciliation_open_orders_order_id_non_negative CHECK (order_id IS NULL OR order_id >= 0)
);
CREATE INDEX reconciliation_open_orders_active_idx
    ON trading.reconciliation_open_orders (active, client_order_id);
INSERT INTO trading.schema_migrations (version) VALUES ('009') ON CONFLICT (version) DO NOTHING;
