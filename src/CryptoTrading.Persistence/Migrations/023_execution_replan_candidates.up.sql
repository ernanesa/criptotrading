-- A residual is evidence for a future decision cycle, never an executable order.
CREATE TABLE trading.execution_replan_candidates
(
    candidate_id uuid PRIMARY KEY,
    origin_order_id uuid NOT NULL,
    origin_client_order_id varchar(36) NOT NULL,
    cancel_message_id uuid NOT NULL UNIQUE,
    terminal_lifecycle_event_id uuid NOT NULL UNIQUE REFERENCES trading.order_lifecycle_events(event_id),
    symbol varchar(32) NOT NULL,
    side varchar(4) NOT NULL,
    order_type varchar(16) NOT NULL,
    residual_quantity numeric(38, 18) NOT NULL,
    next_attempt integer NOT NULL,
    correlation_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT execution_replan_candidate_residual_positive CHECK (residual_quantity > 0),
    CONSTRAINT execution_replan_candidate_attempt_valid CHECK (next_attempt BETWEEN 1 AND 1000),
    CONSTRAINT execution_replan_candidate_side_valid CHECK (side IN ('BUY', 'SELL')),
    CONSTRAINT execution_replan_candidate_type_valid CHECK (order_type IN ('MARKET', 'LIMIT'))
);
CREATE INDEX execution_replan_candidates_origin_idx ON trading.execution_replan_candidates (origin_client_order_id, created_at);
CREATE TRIGGER execution_replan_candidates_append_only BEFORE UPDATE OR DELETE ON trading.execution_replan_candidates FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('023') ON CONFLICT (version) DO NOTHING;
