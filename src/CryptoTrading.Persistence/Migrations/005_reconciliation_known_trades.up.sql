-- CryptoTrading persistence schema, migration 005.
-- External trade IDs are immutable reconciliation evidence. They are only
-- added after the corresponding journal event has been durably recorded.
CREATE TABLE trading.reconciliation_known_trades
(
    trade_id bigint PRIMARY KEY,
    symbol varchar(20) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT reconciliation_known_trades_trade_id_non_negative CHECK (trade_id >= 0),
    CONSTRAINT reconciliation_known_trades_symbol_format CHECK (symbol ~ '^[A-Z0-9]{2,20}$')
);

CREATE INDEX reconciliation_known_trades_symbol_idx
    ON trading.reconciliation_known_trades (symbol, trade_id);

CREATE TRIGGER reconciliation_known_trades_append_only
    BEFORE UPDATE OR DELETE ON trading.reconciliation_known_trades
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('005') ON CONFLICT (version) DO NOTHING;
