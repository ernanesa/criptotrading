-- Trade IDs are scoped by Binance symbol. Preserve all recorded reconciliation evidence
-- while replacing the accidental global identity with the exchange identity.
ALTER TABLE trading.reconciliation_known_trades
    DROP CONSTRAINT reconciliation_known_trades_pkey;

ALTER TABLE trading.reconciliation_known_trades
    ADD PRIMARY KEY (symbol, trade_id);

INSERT INTO trading.schema_migrations (version)
VALUES ('030')
ON CONFLICT (version) DO NOTHING;
