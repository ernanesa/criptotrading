DROP TABLE IF EXISTS trading.strategy_regime_performances;
DROP TABLE IF EXISTS trading.drift_observations;
DROP TABLE IF EXISTS trading.strategy_model_transitions;
DROP TABLE IF EXISTS trading.strategy_models;
DROP TABLE IF EXISTS trading.market_regime_snapshots;

DELETE FROM trading.schema_migrations WHERE version = '026';
