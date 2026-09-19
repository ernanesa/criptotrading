DROP TRIGGER IF EXISTS ledger_postings_balanced ON trading.ledger_postings;
DROP TRIGGER IF EXISTS journal_entries_balanced ON trading.journal_entries;
DROP TRIGGER IF EXISTS ledger_postings_append_only ON trading.ledger_postings;
DROP TRIGGER IF EXISTS journal_entries_append_only ON trading.journal_entries;
DROP FUNCTION IF EXISTS trading.assert_journal_entry_balanced();
DROP FUNCTION IF EXISTS trading.reject_journal_mutation();
DELETE FROM trading.schema_migrations WHERE version = '002';
