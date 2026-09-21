-- Venue rebates are negative commissions and must remain explicit financial facts.
ALTER TABLE trading.execution_fills
    DROP CONSTRAINT IF EXISTS execution_fills_commission_non_negative;

INSERT INTO trading.schema_migrations (version) VALUES ('020') ON CONFLICT (version) DO NOTHING;
