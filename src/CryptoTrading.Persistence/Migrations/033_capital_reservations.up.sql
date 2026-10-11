CREATE TABLE IF NOT EXISTS trading.capital_reservation_fences (
    account_id text PRIMARY KEY,
    worker_instance_id text NOT NULL,
    fencing_token bigint NOT NULL CHECK (fencing_token > 0),
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS trading.capital_reservations (
    reservation_id text PRIMARY KEY,
    account_id text NOT NULL,
    symbol text NOT NULL,
    client_order_id text NOT NULL UNIQUE,
    asset text NOT NULL,
    reserved_amount numeric(38, 18) NOT NULL CHECK (reserved_amount > 0),
    consumed_amount numeric(38, 18) NOT NULL DEFAULT 0 CHECK (consumed_amount >= 0 AND consumed_amount <= reserved_amount),
    status text NOT NULL CHECK (status IN ('Reserved', 'CommittedToExchange', 'PartiallyFilled', 'Released', 'LockedUnknown')),
    worker_instance_id text NOT NULL,
    fencing_token bigint NOT NULL CHECK (fencing_token > 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    reason text NULL
);

CREATE INDEX IF NOT EXISTS ix_capital_reservations_account_status
    ON trading.capital_reservations (account_id, status);

INSERT INTO trading.schema_migrations (version) VALUES ('033') ON CONFLICT (version) DO NOTHING;
