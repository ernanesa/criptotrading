-- CryptoTrading persistence schema, migration 006.
-- Policy content is immutable; lifecycle changes are append-only events.
CREATE TABLE trading.risk_policies
(
    policy_version varchar(100) PRIMARY KEY,
    max_order_notional numeric(38,18) NOT NULL CHECK (max_order_notional > 0),
    max_exposure_notional numeric(38,18) NOT NULL CHECK (max_exposure_notional > 0),
    max_daily_loss numeric(38,18) NOT NULL CHECK (max_daily_loss > 0),
    max_drawdown numeric(38,18) NOT NULL CHECK (max_drawdown > 0),
    max_market_data_age_ticks bigint NOT NULL CHECK (max_market_data_age_ticks > 0),
    withdrawals_enabled boolean NOT NULL DEFAULT false CHECK (withdrawals_enabled = false),
    max_slippage_bps numeric(38,18) NULL CHECK (max_slippage_bps IS NULL OR max_slippage_bps >= 0),
    max_volatility_bps numeric(38,18) NULL CHECK (max_volatility_bps IS NULL OR max_volatility_bps >= 0),
    min_liquidity_notional numeric(38,18) NULL CHECK (min_liquidity_notional IS NULL OR min_liquidity_notional >= 0),
    effective_from timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    document_hash char(64) NOT NULL CHECK (document_hash ~ '^[A-Fa-f0-9]{64}$'),
    created_by varchar(200) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT risk_policies_valid_window CHECK (expires_at > effective_from)
);

CREATE TABLE trading.risk_policy_events
(
    event_id uuid PRIMARY KEY,
    policy_version varchar(100) NOT NULL REFERENCES trading.risk_policies(policy_version),
    lifecycle varchar(20) NOT NULL CHECK (lifecycle IN ('Draft', 'Approved', 'Active', 'Superseded', 'Revoked')),
    actor varchar(200) NOT NULL,
    approval_id varchar(200) NULL,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT risk_policy_event_approval_data CHECK (
        (lifecycle IN ('Approved', 'Active') AND approval_id IS NOT NULL)
        OR lifecycle IN ('Draft', 'Superseded', 'Revoked')
    )
);

CREATE INDEX risk_policy_events_latest_idx
    ON trading.risk_policy_events (policy_version, occurred_at DESC, event_id DESC);

CREATE INDEX risk_policy_events_active_idx
    ON trading.risk_policy_events (lifecycle, occurred_at DESC)
    WHERE lifecycle = 'Active';

CREATE TRIGGER risk_policies_append_only
    BEFORE UPDATE OR DELETE ON trading.risk_policies
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

CREATE TRIGGER risk_policy_events_append_only
    BEFORE UPDATE OR DELETE ON trading.risk_policy_events
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('006') ON CONFLICT (version) DO NOTHING;
