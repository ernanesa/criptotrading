CREATE TABLE trading.order_lifecycle_events
(
    event_id uuid PRIMARY KEY,
    client_order_id varchar(36) NOT NULL,
    from_status varchar(32) NOT NULL,
    to_status varchar(32) NOT NULL,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    occurred_at timestamptz NOT NULL,
    exchange_order_id varchar(100) NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT order_lifecycle_events_client_order_not_blank CHECK (length(btrim(client_order_id)) > 0),
    CONSTRAINT order_lifecycle_events_status_valid CHECK (from_status IN ('Created','Validated','Submitted','Acknowledged','PartiallyFilled','Unknown','CancelPending','Filled','Cancelled','Rejected','Expired') AND to_status IN ('Created','Validated','Submitted','Acknowledged','PartiallyFilled','Unknown','CancelPending','Filled','Cancelled','Rejected','Expired'))
);
CREATE INDEX order_lifecycle_events_client_occurred_idx ON trading.order_lifecycle_events (client_order_id, occurred_at, event_id);
CREATE TRIGGER order_lifecycle_events_append_only BEFORE UPDATE OR DELETE ON trading.order_lifecycle_events FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('021') ON CONFLICT (version) DO NOTHING;
