-- Durable watermark for deterministic, closed-bar autonomy cycles.
CREATE TABLE trading.autonomy_checkpoints
(
    scope varchar(200) PRIMARY KEY,
    last_processed_close timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT autonomy_checkpoints_scope_not_blank CHECK (length(btrim(scope)) > 0)
);

INSERT INTO trading.schema_migrations (version) VALUES ('012') ON CONFLICT (version) DO NOTHING;
