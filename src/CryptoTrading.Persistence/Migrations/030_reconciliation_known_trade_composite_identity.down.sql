-- Reverting requires no cross-symbol trade ID collisions.
ALTER TABLE trading.reconciliation_known_trades
    DROP CONSTRAINT reconciliation_known_trades_pkey;

ALTER TABLE trading.reconciliation_known_trades
    ADD PRIMARY KEY (trade_id);

DELETE FROM trading.schema_migrations WHERE version = '030';
