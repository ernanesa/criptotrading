DROP TABLE IF EXISTS trading.autonomy_equity_observations;
DROP TABLE IF EXISTS trading.autonomy_equity_high_water_marks;
DELETE FROM trading.schema_migrations WHERE version = '014';
