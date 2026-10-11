DROP INDEX IF EXISTS trading.ix_capital_reservations_account_status;
DROP TABLE IF EXISTS trading.capital_reservations;
DROP TABLE IF EXISTS trading.capital_reservation_fences;

DELETE FROM trading.schema_migrations WHERE version = '033';
