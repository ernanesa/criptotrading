-- CryptoTrading persistence schema, migration 001.
-- Apply once, in a transaction, with a least-privilege local role.
CREATE SCHEMA IF NOT EXISTS trading;

CREATE TABLE trading.schema_migrations
(
    version varchar(32) PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE TABLE trading.journal_entries
(
    entry_id uuid PRIMARY KEY,
    correlation_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    reason varchar(200) NOT NULL,
    idempotency_key varchar(128) NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT journal_entries_reason_not_blank CHECK (length(btrim(reason)) > 0),
    CONSTRAINT journal_entries_idempotency_key_not_blank
        CHECK (idempotency_key IS NULL OR length(btrim(idempotency_key)) > 0)
);
CREATE UNIQUE INDEX journal_entries_idempotency_key_uq
    ON trading.journal_entries (idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX journal_entries_occurred_at_idx
    ON trading.journal_entries (occurred_at, entry_id);

CREATE TABLE trading.ledger_postings
(
    entry_id uuid NOT NULL REFERENCES trading.journal_entries (entry_id),
    posting_index smallint NOT NULL,
    account_id varchar(100) NOT NULL,
    asset varchar(20) NOT NULL,
    amount numeric(38, 18) NOT NULL,
    CONSTRAINT ledger_postings_pk PRIMARY KEY (entry_id, posting_index),
    CONSTRAINT ledger_postings_index_positive CHECK (posting_index >= 0),
    CONSTRAINT ledger_postings_account_not_blank CHECK (length(btrim(account_id)) > 0),
    CONSTRAINT ledger_postings_asset_format CHECK (asset ~ '^[A-Z0-9]{2,20}$'),
    CONSTRAINT ledger_postings_amount_non_zero CHECK (amount <> 0)
);
CREATE INDEX ledger_postings_account_asset_idx
    ON trading.ledger_postings (account_id, asset, entry_id);

-- Projection only; journal_entries and ledger_postings are the source of truth.
CREATE TABLE trading.ledger_balances
(
    account_id varchar(100) NOT NULL,
    asset varchar(20) NOT NULL,
    balance numeric(38, 18) NOT NULL DEFAULT 0,
    projection_version bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT ledger_balances_pk PRIMARY KEY (account_id, asset),
    CONSTRAINT ledger_balances_account_not_blank CHECK (length(btrim(account_id)) > 0),
    CONSTRAINT ledger_balances_asset_format CHECK (asset ~ '^[A-Z0-9]{2,20}$')
);

CREATE TABLE trading.outbox_messages
(
    message_id uuid PRIMARY KEY,
    aggregate_type varchar(100) NOT NULL,
    aggregate_id varchar(200) NOT NULL,
    event_type varchar(200) NOT NULL,
    payload jsonb NOT NULL,
    correlation_id uuid NOT NULL,
    idempotency_key varchar(128) NULL,
    occurred_at timestamptz NOT NULL,
    available_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    attempts integer NOT NULL DEFAULT 0,
    locked_until timestamptz NULL,
    processed_at timestamptz NULL,
    last_error text NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT outbox_aggregate_type_not_blank CHECK (length(btrim(aggregate_type)) > 0),
    CONSTRAINT outbox_event_type_not_blank CHECK (length(btrim(event_type)) > 0),
    CONSTRAINT outbox_attempts_non_negative CHECK (attempts >= 0)
);
CREATE UNIQUE INDEX outbox_idempotency_key_uq
    ON trading.outbox_messages (idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX outbox_dispatch_idx
    ON trading.outbox_messages (available_at, created_at) WHERE processed_at IS NULL;

CREATE TABLE trading.inbox_messages
(
    consumer_name varchar(150) NOT NULL,
    message_id uuid NOT NULL,
    received_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    processed_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0,
    last_error text NULL,
    CONSTRAINT inbox_messages_pk PRIMARY KEY (consumer_name, message_id),
    CONSTRAINT inbox_attempts_non_negative CHECK (attempts >= 0)
);
CREATE INDEX inbox_pending_idx
    ON trading.inbox_messages (consumer_name, received_at) WHERE processed_at IS NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('001') ON CONFLICT (version) DO NOTHING;
