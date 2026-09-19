-- CryptoTrading persistence schema, migration 011.
-- Research governance is append-only: preregistration, every trial and every
-- holdout access/contamination event remain auditable and independently hashed.
CREATE TABLE trading.experiment_preregistrations
(
    experiment_id uuid PRIMARY KEY,
    experiment_family_id varchar(200) NOT NULL,
    hypothesis varchar(4000) NOT NULL,
    search_space varchar(16000) NOT NULL,
    planned_variants jsonb NOT NULL,
    trial_budget integer NOT NULL CHECK (trial_budget > 0),
    primary_metric varchar(500) NOT NULL,
    rejection_rule varchar(2000) NOT NULL,
    benchmark varchar(1000) NOT NULL,
    preregistered_at timestamptz NOT NULL,
    preregistered_by varchar(200) NOT NULL,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    strategy_version varchar(200) NOT NULL,
    code_commit varchar(200) NOT NULL,
    dataset_version varchar(200) NOT NULL,
    dataset_hash char(64) NOT NULL CHECK (dataset_hash ~ '^[A-Fa-f0-9]{64}$'),
    holdout_version varchar(200) NOT NULL,
    metric_definition_version varchar(200) NOT NULL,
    environment_fingerprint varchar(500) NOT NULL,
    dependency_lock_hash char(64) NOT NULL CHECK (dependency_lock_hash ~ '^[A-Fa-f0-9]{64}$'),
    seed integer NOT NULL,
    period_start timestamptz NOT NULL,
    period_end timestamptz NOT NULL CHECK (period_end > period_start),
    preregistration_sha256 char(64) NOT NULL CHECK (preregistration_sha256 ~ '^[A-Fa-f0-9]{64}$'),
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);

CREATE TABLE trading.experiment_trials
(
    experiment_id uuid NOT NULL REFERENCES trading.experiment_preregistrations(experiment_id),
    trial_number integer NOT NULL CHECK (trial_number > 0),
    variant_id varchar(200) NOT NULL,
    parameters_canonical varchar(16000) NOT NULL,
    result_artifact_hash char(64) NOT NULL CHECK (result_artifact_hash ~ '^[A-Fa-f0-9]{64}$'),
    decision varchar(32) NOT NULL CHECK (decision IN ('Rejected', 'Iterate', 'PaperEligible', 'ShadowEligible', 'CanaryEligible')),
    recorded_at timestamptz NOT NULL,
    recorded_by varchar(200) NOT NULL,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    trial_sha256 char(64) NOT NULL CHECK (trial_sha256 ~ '^[A-Fa-f0-9]{64}$'),
    PRIMARY KEY (experiment_id, trial_number)
);

CREATE TABLE trading.holdout_access_audits
(
    event_id uuid PRIMARY KEY,
    experiment_id uuid NOT NULL REFERENCES trading.experiment_preregistrations(experiment_id),
    holdout_version varchar(200) NOT NULL,
    actor varchar(200) NOT NULL,
    purpose varchar(1000) NOT NULL,
    occurred_at timestamptz NOT NULL,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    approved_by_policy boolean NOT NULL,
    approval_reference varchar(200) NULL,
    outcome varchar(32) NOT NULL CHECK (outcome IN ('Granted', 'Contaminated', 'RejectedAfterContamination')),
    contamination_reason varchar(2000) NULL,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[A-Fa-f0-9]{64}$')
);

CREATE TABLE trading.holdout_contamination_events
(
    event_id uuid PRIMARY KEY,
    experiment_id uuid NOT NULL REFERENCES trading.experiment_preregistrations(experiment_id),
    actor varchar(200) NOT NULL,
    reason varchar(2000) NOT NULL,
    replacement_holdout_version varchar(200) NOT NULL,
    occurred_at timestamptz NOT NULL,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[A-Fa-f0-9]{64}$')
);

CREATE INDEX experiment_trials_order_idx ON trading.experiment_trials (experiment_id, trial_number);
CREATE INDEX holdout_access_audits_order_idx ON trading.holdout_access_audits (experiment_id, occurred_at, event_id);
CREATE INDEX holdout_contamination_events_order_idx ON trading.holdout_contamination_events (experiment_id, occurred_at, event_id);

CREATE TRIGGER experiment_preregistrations_append_only
    BEFORE UPDATE OR DELETE ON trading.experiment_preregistrations
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
CREATE TRIGGER experiment_trials_append_only
    BEFORE UPDATE OR DELETE ON trading.experiment_trials
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
CREATE TRIGGER holdout_access_audits_append_only
    BEFORE UPDATE OR DELETE ON trading.holdout_access_audits
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();
CREATE TRIGGER holdout_contamination_events_append_only
    BEFORE UPDATE OR DELETE ON trading.holdout_contamination_events
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('011') ON CONFLICT (version) DO NOTHING;
