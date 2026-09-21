ALTER TABLE trading.execution_fills
    ADD CONSTRAINT execution_fills_commission_non_negative CHECK (commission >= 0);

DELETE FROM trading.schema_migrations WHERE version = '020';
