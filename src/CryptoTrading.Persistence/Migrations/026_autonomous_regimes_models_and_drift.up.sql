-- CryptoTrading persistence schema, migration 026.
-- Autonomous Regimes, Strategy Model Registry, Drift Monitoring and Contextual Allocation.

CREATE TABLE trading.market_regime_snapshots
(
    snapshot_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    symbol varchar(32) NOT NULL,
    observed_at timestamptz NOT NULL,
    regime varchar(64) NOT NULL,
    confidence numeric NOT NULL CHECK (confidence >= 0 AND confidence <= 1),
    trend_slope numeric NOT NULL,
    volatility numeric NOT NULL CHECK (volatility >= 0),
    reason varchar(512) NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE INDEX market_regime_snapshots_symbol_observed_idx 
    ON trading.market_regime_snapshots (symbol, observed_at DESC);

CREATE TRIGGER market_regime_snapshots_append_only
    BEFORE UPDATE OR DELETE ON trading.market_regime_snapshots
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

CREATE TABLE trading.strategy_models
(
    model_id varchar(128) PRIMARY KEY,
    strategy_id varchar(128) NOT NULL,
    version varchar(64) NOT NULL,
    code_commit_sha varchar(64) NOT NULL,
    dataset_hash char(64) NOT NULL CHECK (dataset_hash ~ '^[A-Fa-f0-9]{64}$'),
    configuration_fingerprint varchar(500) NOT NULL,
    status varchar(32) NOT NULL CHECK (status IN ('Draft', 'Candidate', 'Active', 'Quarantined', 'Retired')),
    registered_at timestamptz NOT NULL,
    registered_by varchar(128) NOT NULL,
    promotion_justification varchar(2000) NULL,
    canonical_hash char(64) NOT NULL CHECK (canonical_hash ~ '^[A-Fa-f0-9]{64}$'),
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE INDEX strategy_models_strategy_status_idx 
    ON trading.strategy_models (strategy_id, status);

CREATE TABLE trading.strategy_model_transitions
(
    transition_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    model_id varchar(128) NOT NULL REFERENCES trading.strategy_models(model_id),
    previous_status varchar(32) NOT NULL,
    target_status varchar(32) NOT NULL,
    actor varchar(128) NOT NULL,
    justification varchar(2000) NOT NULL,
    transitioned_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE INDEX strategy_model_transitions_model_idx 
    ON trading.strategy_model_transitions (model_id, transitioned_at DESC);

CREATE TRIGGER strategy_model_transitions_append_only
    BEFORE UPDATE OR DELETE ON trading.strategy_model_transitions
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

CREATE TABLE trading.drift_observations
(
    observation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    strategy_id varchar(128) NOT NULL,
    drift_type varchar(64) NOT NULL,
    expected_value numeric NOT NULL,
    observed_value numeric NOT NULL,
    allowed_threshold numeric NOT NULL CHECK (allowed_threshold > 0),
    absolute_deviation numeric NOT NULL,
    relative_rate numeric NOT NULL,
    severity varchar(32) NOT NULL CHECK (severity IN ('WithinTolerance', 'ElevatedWarning', 'BreachedThreshold')),
    requires_quarantine boolean NOT NULL,
    message varchar(1000) NOT NULL,
    observed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE INDEX drift_observations_strategy_observed_idx 
    ON trading.drift_observations (strategy_id, observed_at DESC);

CREATE TRIGGER drift_observations_append_only
    BEFORE UPDATE OR DELETE ON trading.drift_observations
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

CREATE TABLE trading.strategy_regime_performances
(
    strategy_id varchar(128) NOT NULL,
    regime varchar(64) NOT NULL,
    observation_count integer NOT NULL CHECK (observation_count >= 0),
    cumulative_reward numeric NOT NULL,
    average_reward numeric NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (strategy_id, regime)
);

INSERT INTO trading.schema_migrations (version) VALUES ('026') ON CONFLICT (version) DO NOTHING;
