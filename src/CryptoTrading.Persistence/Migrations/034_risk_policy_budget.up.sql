ALTER TABLE trading.risk_policies
    ADD COLUMN IF NOT EXISTS budget_policy_json text NULL;

INSERT INTO trading.schema_migrations (version) VALUES ('034') ON CONFLICT (version) DO NOTHING;
