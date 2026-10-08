CREATE TABLE trading.execution_submission_starts (
    message_id uuid PRIMARY KEY REFERENCES trading.outbox_messages(message_id),
    payload_hash char(64) NOT NULL,
    lease_owner varchar(150) NOT NULL,
    started_at timestamptz NOT NULL
);
CREATE TRIGGER execution_submission_starts_append_only
    BEFORE UPDATE OR DELETE ON trading.execution_submission_starts
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
INSERT INTO trading.schema_migrations(version) VALUES ('027') ON CONFLICT (version) DO NOTHING;
