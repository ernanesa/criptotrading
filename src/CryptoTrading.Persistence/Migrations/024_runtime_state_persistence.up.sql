CREATE TABLE trading.runtime_state_transitions
(
    revision bigint PRIMARY KEY,
    from_state varchar(32) NOT NULL,
    to_state varchar(32) NOT NULL,
    reason varchar(256) NOT NULL,
    occurred_at timestamptz NOT NULL,
    correlation_id varchar(128) NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);
CREATE TABLE trading.runtime_state_checkpoints
(
    scope varchar(32) PRIMARY KEY,
    state varchar(32) NOT NULL,
    revision bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT runtime_state_checkpoint_revision_non_negative CHECK (revision >= 0)
);
CREATE TRIGGER runtime_state_transitions_append_only BEFORE UPDATE OR DELETE ON trading.runtime_state_transitions FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
INSERT INTO trading.schema_migrations (version) VALUES ('024') ON CONFLICT (version) DO NOTHING;
