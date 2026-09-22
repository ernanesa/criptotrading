-- Append-only decision evidence is committed with the watermark and optional
-- outbox so a restart can neither lose NO_TRADE reasoning nor dispatch an
-- order without its feature/risk lineage.
CREATE TABLE trading.autonomy_decisions
(
    decision_id varchar(128) PRIMARY KEY,
    environment varchar(32) NOT NULL,
    account_scope varchar(200) NOT NULL,
    checkpoint_scope varchar(200) NOT NULL,
    symbol varchar(32) NOT NULL,
    strategy_id varchar(128) NOT NULL,
    strategy_version varchar(128) NOT NULL,
    close_time timestamptz NOT NULL,
    available_at timestamptz NOT NULL,
    feature_schema_version varchar(128) NOT NULL,
    feature_configuration_fingerprint varchar(128) NOT NULL,
    feature_fingerprint varchar(128) NOT NULL,
    signal varchar(64) NOT NULL,
    strategy_reason varchar(128) NOT NULL,
    risk_status varchar(64) NULL,
    risk_policy_version varchar(128) NULL,
    risk_requested_notional numeric NULL,
    risk_authorized_notional numeric NULL,
    risk_reason_codes jsonb NOT NULL,
    outcome varchar(32) NOT NULL,
    no_trade_reason varchar(512) NULL,
    trace jsonb NOT NULL,
    outbox_message_id uuid NULL UNIQUE REFERENCES trading.outbox_messages(message_id) DEFERRABLE INITIALLY DEFERRED,
    payload jsonb NOT NULL,
    committed_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT autonomy_decisions_times_ordered CHECK (available_at >= close_time),
    CONSTRAINT autonomy_decisions_scope_not_blank CHECK (length(btrim(account_scope)) > 0 AND length(btrim(checkpoint_scope)) > 0),
    CONSTRAINT autonomy_decisions_dispatchable_outbox CHECK ((outcome = 'DISPATCHABLE') = (outbox_message_id IS NOT NULL)),
    CONSTRAINT autonomy_decisions_risk_numbers_non_negative CHECK
        ((risk_requested_notional IS NULL OR risk_requested_notional >= 0) AND
         (risk_authorized_notional IS NULL OR risk_authorized_notional >= 0)),
    CONSTRAINT autonomy_decisions_natural_identity UNIQUE
        (environment, account_scope, symbol, strategy_id, strategy_version, close_time)
);
CREATE INDEX autonomy_decisions_checkpoint_close_idx ON trading.autonomy_decisions (checkpoint_scope, close_time DESC);
CREATE TRIGGER autonomy_decisions_append_only BEFORE UPDATE OR DELETE ON trading.autonomy_decisions FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('025') ON CONFLICT (version) DO NOTHING;
