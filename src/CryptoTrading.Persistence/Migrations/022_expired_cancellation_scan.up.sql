-- Keeps the bounded TTL cancellation scan from walking historical submissions.
CREATE INDEX execution_submission_context_expiry_scan_idx
    ON trading.execution_submission_context (expires_at, client_order_id)
    WHERE expires_at IS NOT NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('022') ON CONFLICT (version) DO NOTHING;
