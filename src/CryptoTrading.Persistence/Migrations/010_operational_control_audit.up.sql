-- CryptoTrading persistence schema, migration 010.
-- KillSwitch/SafeMode transitions are immutable operational evidence.
CREATE TABLE trading.operational_control_audit_events
(
    event_id uuid PRIMARY KEY,
    schema_version integer NOT NULL CHECK (schema_version = 1),
    event_type varchar(64) NOT NULL CHECK (event_type = 'OPERATIONAL_CONTROL_TRANSITION'),
    from_mode varchar(20) NOT NULL CHECK (from_mode IN ('Normal', 'SafeMode', 'KillSwitch')),
    to_mode varchar(20) NOT NULL CHECK (to_mode IN ('Normal', 'SafeMode', 'KillSwitch')),
    actor varchar(200) NOT NULL CHECK (length(btrim(actor)) > 0),
    reason varchar(500) NOT NULL CHECK (length(btrim(reason)) > 0),
    idempotency_key varchar(128) NOT NULL CHECK (length(btrim(idempotency_key)) > 0),
    occurred_at timestamptz NOT NULL,
    -- PostgreSQL timestamptz stores microseconds; retain the original UTC tick
    -- value so the canonical payload hash survives a read/restart round-trip.
    occurred_at_ticks bigint NOT NULL CHECK (occurred_at_ticks > 0),
    approval_id varchar(200) NULL,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[A-Fa-f0-9]{64}$'),
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT operational_control_audit_idempotency_unique UNIQUE (idempotency_key)
);

CREATE INDEX operational_control_audit_occurred_idx
    ON trading.operational_control_audit_events (occurred_at, event_id);

CREATE TRIGGER operational_control_audit_append_only
    BEFORE UPDATE OR DELETE ON trading.operational_control_audit_events
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('010') ON CONFLICT (version) DO NOTHING;
