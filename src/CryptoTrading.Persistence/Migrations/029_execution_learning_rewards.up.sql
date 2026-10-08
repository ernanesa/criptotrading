-- One durable learning reward per execution fill, linked to the autonomy decision
-- whose outbox command produced the fill. Aggregates are updated in the same tx.
CREATE TABLE trading.execution_learning_rewards
(
    execution_id varchar(200) PRIMARY KEY REFERENCES trading.execution_fills(execution_id),
    decision_id varchar(128) NOT NULL REFERENCES trading.autonomy_decisions(decision_id),
    strategy_id varchar(128) NOT NULL,
    regime varchar(64) NOT NULL,
    reward numeric(38, 18) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT execution_learning_rewards_strategy_not_blank CHECK (length(btrim(strategy_id)) > 0)
);
CREATE INDEX execution_learning_rewards_decision_idx
    ON trading.execution_learning_rewards (decision_id);
CREATE TRIGGER execution_learning_rewards_append_only
    BEFORE UPDATE OR DELETE ON trading.execution_learning_rewards
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('029') ON CONFLICT (version) DO NOTHING;
