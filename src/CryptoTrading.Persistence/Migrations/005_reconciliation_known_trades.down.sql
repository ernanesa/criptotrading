-- Rollback is destructive to reconciliation evidence; export it first.
DROP TRIGGER IF EXISTS reconciliation_known_trades_append_only ON trading.reconciliation_known_trades;
DROP INDEX IF EXISTS trading.reconciliation_known_trades_symbol_idx;
DROP TABLE IF EXISTS trading.reconciliation_known_trades;
DELETE FROM trading.schema_migrations WHERE version = '005';
