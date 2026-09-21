-- Links a deterministic execution plan/attempt to its durable outbox command.
CREATE TABLE trading.execution_submission_context
(
    client_order_id varchar(36) PRIMARY KEY,
    application_order_id uuid NOT NULL,
    correlation_id uuid NOT NULL,
    outbox_message_id uuid NOT NULL UNIQUE REFERENCES trading.outbox_messages(message_id),
    symbol varchar(32) NOT NULL,
    side varchar(4) NOT NULL,
    order_type varchar(16) NOT NULL,
    authorized_quantity numeric(38, 18) NOT NULL,
    reference_price numeric(38, 18) NOT NULL,
    authorized_notional numeric(38, 18) NOT NULL,
    execution_attempt integer NOT NULL,
    planned_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT execution_submission_context_symbol_format CHECK (symbol ~ '^[A-Z0-9_]{2,32}$'),
    CONSTRAINT execution_submission_context_side_valid CHECK (side IN ('BUY', 'SELL')),
    CONSTRAINT execution_submission_context_type_valid CHECK (order_type IN ('MARKET', 'LIMIT')),
    CONSTRAINT execution_submission_context_quantity_positive CHECK (authorized_quantity > 0),
    CONSTRAINT execution_submission_context_price_positive CHECK (reference_price > 0),
    CONSTRAINT execution_submission_context_notional_positive CHECK (authorized_notional > 0),
    CONSTRAINT execution_submission_context_attempt_valid CHECK (execution_attempt >= 0 AND execution_attempt <= 1000),
    CONSTRAINT execution_submission_context_expiry_ordered CHECK (expires_at IS NULL OR expires_at >= planned_at)
);
CREATE INDEX execution_submission_context_correlation_idx
    ON trading.execution_submission_context (correlation_id, execution_attempt, client_order_id);

INSERT INTO trading.schema_migrations (version) VALUES ('019') ON CONFLICT (version) DO NOTHING;
