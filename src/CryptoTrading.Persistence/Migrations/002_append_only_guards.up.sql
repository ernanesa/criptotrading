-- CryptoTrading persistence schema, migration 002.
CREATE OR REPLACE FUNCTION trading.reject_journal_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'journal tables are append-only; operation % is not allowed', TG_OP
        USING ERRCODE = '55000';
END;
$$;

CREATE TRIGGER journal_entries_append_only
    BEFORE UPDATE OR DELETE ON trading.journal_entries
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
CREATE TRIGGER ledger_postings_append_only
    BEFORE UPDATE OR DELETE ON trading.ledger_postings
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

CREATE OR REPLACE FUNCTION trading.assert_journal_entry_balanced()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM trading.ledger_postings WHERE entry_id = NEW.entry_id) THEN
        RAISE EXCEPTION 'journal entry % must contain postings', NEW.entry_id USING ERRCODE = '23514';
    END IF;
    IF EXISTS (
        SELECT 1 FROM trading.ledger_postings WHERE entry_id = NEW.entry_id
        GROUP BY asset HAVING sum(amount) <> 0
    ) OR (SELECT count(*) FROM trading.ledger_postings WHERE entry_id = NEW.entry_id) < 2 THEN
        RAISE EXCEPTION 'journal entry % is not balanced', NEW.entry_id USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER journal_entries_balanced
    AFTER INSERT ON trading.journal_entries DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION trading.assert_journal_entry_balanced();
CREATE CONSTRAINT TRIGGER ledger_postings_balanced
    AFTER INSERT ON trading.ledger_postings DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION trading.assert_journal_entry_balanced();

INSERT INTO trading.schema_migrations (version) VALUES ('002') ON CONFLICT (version) DO NOTHING;
