-- CryptoTrading persistence schema, migration 014.
-- The current projection is updated transactionally; observations are immutable
-- evidence for restart/reconciliation and are never used as an order authority.
CREATE TABLE trading.autonomy_equity_high_water_marks
(
    scope varchar(200) PRIMARY KEY,
    equity_notional numeric(38, 18) NOT NULL,
    high_water_mark_notional numeric(38, 18) NOT NULL,
    drawdown_notional numeric(38, 18) NOT NULL,
    last_valuation_at timestamptz NOT NULL,
    observation_count bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT autonomy_hwm_scope_not_blank CHECK (length(btrim(scope)) > 0),
    CONSTRAINT autonomy_hwm_equity_non_negative CHECK (equity_notional >= 0),
    CONSTRAINT autonomy_hwm_mark_non_negative CHECK (high_water_mark_notional >= 0),
    CONSTRAINT autonomy_hwm_drawdown_non_negative CHECK (drawdown_notional >= 0),
    CONSTRAINT autonomy_hwm_mark_not_below_equity CHECK (high_water_mark_notional >= equity_notional),
    CONSTRAINT autonomy_hwm_observation_count_positive CHECK (observation_count > 0)
);

CREATE TABLE trading.autonomy_equity_observations
(
    observation_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope varchar(200) NOT NULL REFERENCES trading.autonomy_equity_high_water_marks(scope),
    equity_notional numeric(38, 18) NOT NULL,
    high_water_mark_notional numeric(38, 18) NOT NULL,
    drawdown_notional numeric(38, 18) NOT NULL,
    valuation_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT autonomy_equity_observation_scope_not_blank CHECK (length(btrim(scope)) > 0),
    CONSTRAINT autonomy_equity_observation_equity_non_negative CHECK (equity_notional >= 0),
    CONSTRAINT autonomy_equity_observation_mark_non_negative CHECK (high_water_mark_notional >= 0),
    CONSTRAINT autonomy_equity_observation_drawdown_non_negative CHECK (drawdown_notional >= 0),
    CONSTRAINT autonomy_equity_observation_mark_not_below_equity CHECK (high_water_mark_notional >= equity_notional),
    CONSTRAINT autonomy_equity_observation_unique_time UNIQUE (scope, valuation_at)
);

CREATE INDEX autonomy_equity_observations_scope_time_idx
    ON trading.autonomy_equity_observations (scope, valuation_at, observation_id);

CREATE TRIGGER autonomy_equity_observations_append_only
    BEFORE UPDATE OR DELETE ON trading.autonomy_equity_observations
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('014') ON CONFLICT (version) DO NOTHING;
