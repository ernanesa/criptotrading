-- Explicit BTCUSDT/USDT portfolio valuations. Null external flow/unrealized values mean unknown, never zero.
CREATE TABLE trading.portfolio_nav_snapshots
(
    symbol varchar(32) NOT NULL,
    quote_asset varchar(20) NOT NULL,
    observed_at timestamptz NOT NULL,
    nav_quote numeric(38, 18) NOT NULL,
    unrealized_pnl_quote numeric(38, 18) NULL,
    external_cash_flow_quote numeric(38, 18) NULL,
    source varchar(200) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    PRIMARY KEY (symbol, quote_asset, observed_at),
    CONSTRAINT portfolio_nav_snapshots_symbol_format CHECK (symbol ~ '^[A-Z0-9_]{2,32}$'),
    CONSTRAINT portfolio_nav_snapshots_quote_asset_format CHECK (quote_asset ~ '^[A-Z0-9]{2,20}$'),
    CONSTRAINT portfolio_nav_snapshots_nav_non_negative CHECK (nav_quote >= 0),
    CONSTRAINT portfolio_nav_snapshots_source_not_blank CHECK (length(btrim(source)) > 0)
);

CREATE INDEX portfolio_nav_snapshots_scope_observed_idx
    ON trading.portfolio_nav_snapshots (symbol, quote_asset, observed_at DESC);

CREATE TRIGGER portfolio_nav_snapshots_append_only
    BEFORE UPDATE OR DELETE ON trading.portfolio_nav_snapshots
    FOR EACH ROW EXECUTE FUNCTION trading.reject_journal_mutation();

INSERT INTO trading.schema_migrations (version) VALUES ('028') ON CONFLICT (version) DO NOTHING;
