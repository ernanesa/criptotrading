using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class PersistenceMigrationsTests
{
    [Fact]
    public void MigrationsAreEmbeddedAndOrdered()
    {
        var migrations = PersistenceMigrations.ReadAll();

        Assert.Equal(
            [
                "001_initial_schema.down.sql", "001_initial_schema.up.sql",
                "002_append_only_guards.down.sql", "002_append_only_guards.up.sql",
                "003_outbox_lease_owner.down.sql", "003_outbox_lease_owner.up.sql",
                "004_outbox_dead_letter.down.sql", "004_outbox_dead_letter.up.sql",
                "005_reconciliation_known_trades.down.sql", "005_reconciliation_known_trades.up.sql",
                "006_risk_policy_registry.down.sql", "006_risk_policy_registry.up.sql",
                "007_account_stream_events.down.sql", "007_account_stream_events.up.sql",
                "008_account_stream_session_identity.down.sql", "008_account_stream_session_identity.up.sql",
                "009_reconciliation_open_orders.down.sql", "009_reconciliation_open_orders.up.sql",
                "010_operational_control_audit.down.sql", "010_operational_control_audit.up.sql",
                "011_experiment_governance.down.sql", "011_experiment_governance.up.sql",
                "012_autonomy_checkpoints.down.sql", "012_autonomy_checkpoints.up.sql",
                "013_execution_accounting.down.sql", "013_execution_accounting.up.sql",
                "014_autonomy_equity_high_water_marks.down.sql", "014_autonomy_equity_high_water_marks.up.sql",
                "015_execution_fee_attribution.down.sql", "015_execution_fee_attribution.up.sql",
                "016_open_order_quantities.down.sql", "016_open_order_quantities.up.sql",
                "017_execution_quality_expectations.down.sql", "017_execution_quality_expectations.up.sql",
                "018_execution_fill_quality.down.sql", "018_execution_fill_quality.up.sql",
                "019_execution_submission_context.down.sql", "019_execution_submission_context.up.sql",
                "020_execution_commission_rebates.down.sql", "020_execution_commission_rebates.up.sql",
                "021_order_lifecycle_events.down.sql", "021_order_lifecycle_events.up.sql",
                "022_expired_cancellation_scan.down.sql", "022_expired_cancellation_scan.up.sql",
                "023_execution_replan_candidates.down.sql", "023_execution_replan_candidates.up.sql",
                "024_runtime_state_persistence.down.sql", "024_runtime_state_persistence.up.sql",
                "025_autonomy_decision_commits.down.sql", "025_autonomy_decision_commits.up.sql",
            ],
            migrations.Select(static migration => migration.FileName));
        Assert.All(migrations, static migration => Assert.NotEmpty(migration.Sql));
        Assert.Equal(
            ["001", "001", "002", "002", "003", "003", "004", "004", "005", "005", "006", "006", "007", "007", "008", "008", "009", "009", "010", "010", "011", "011", "012", "012", "013", "013", "014", "014", "015", "015", "016", "016", "017", "017", "018", "018", "019", "019", "020", "020", "021", "021", "022", "022", "023", "023", "024", "024", "025", "025"],
            migrations.Select(static migration => migration.Version));
    }

    [Fact]
    public void UpMigrationsDeclareExpectedDurableBoundaries()
    {
        var upMigrations = PersistenceMigrations.ReadAll()
            .Where(static migration => migration.Direction == "up")
            .ToArray();

        Assert.Contains("journal_entries", upMigrations[0].Sql, StringComparison.Ordinal);
        Assert.Contains("ledger_postings", upMigrations[0].Sql, StringComparison.Ordinal);
        Assert.Contains("outbox_messages", upMigrations[0].Sql, StringComparison.Ordinal);
        Assert.Contains("inbox_messages", upMigrations[0].Sql, StringComparison.Ordinal);
        Assert.Contains("append-only", upMigrations[1].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", upMigrations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("lease_owner", upMigrations[2].Sql, StringComparison.Ordinal);
        Assert.Contains("dead_lettered_at", upMigrations[3].Sql, StringComparison.Ordinal);
        Assert.Contains("reconciliation_known_trades", upMigrations[4].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[4].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("risk_policies", upMigrations[5].Sql, StringComparison.Ordinal);
        Assert.Contains("risk_policy_events", upMigrations[5].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[5].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("account_stream_events", upMigrations[6].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[6].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session_id", upMigrations[7].Sql, StringComparison.Ordinal);
        Assert.Contains("session_generation", upMigrations[7].Sql, StringComparison.Ordinal);
        Assert.Contains("subscription_id", upMigrations[7].Sql, StringComparison.Ordinal);
        Assert.Contains("reconciliation_open_orders", upMigrations[8].Sql, StringComparison.Ordinal);
        Assert.Contains("operational_control_audit_events", upMigrations[9].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[9].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("experiment_preregistrations", upMigrations[10].Sql, StringComparison.Ordinal);
        Assert.Contains("experiment_trials", upMigrations[10].Sql, StringComparison.Ordinal);
        Assert.Contains("holdout_access_audits", upMigrations[10].Sql, StringComparison.Ordinal);
        Assert.Contains("holdout_contamination_events", upMigrations[10].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[10].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autonomy_checkpoints", upMigrations[11].Sql, StringComparison.Ordinal);
        Assert.Contains("execution_fills", upMigrations[12].Sql, StringComparison.Ordinal);
        Assert.Contains("position_cost_basis", upMigrations[12].Sql, StringComparison.Ordinal);
        Assert.Contains("autonomy_equity_high_water_marks", upMigrations[13].Sql, StringComparison.Ordinal);
        Assert.Contains("autonomy_equity_observations", upMigrations[13].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[13].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution_fee_expectations", upMigrations[14].Sql, StringComparison.Ordinal);
        Assert.Contains("fee_comparison_status", upMigrations[14].Sql, StringComparison.Ordinal);
        Assert.Contains("cumulative_executed_quantity", upMigrations[15].Sql, StringComparison.Ordinal);
        Assert.Contains("reference_price", upMigrations[16].Sql, StringComparison.Ordinal);
        Assert.Contains("implementation_shortfall_quote", upMigrations[17].Sql, StringComparison.Ordinal);
        Assert.Contains("execution_submission_context", upMigrations[18].Sql, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT IF EXISTS execution_fills_commission_non_negative", upMigrations[19].Sql, StringComparison.Ordinal);
        Assert.Contains("order_lifecycle_events", upMigrations[20].Sql, StringComparison.Ordinal);
        Assert.Contains("execution_submission_context_expiry_scan_idx", upMigrations[21].Sql, StringComparison.Ordinal);
        Assert.Contains("execution_replan_candidates", upMigrations[22].Sql, StringComparison.Ordinal);
        Assert.Contains("runtime_state_transitions", upMigrations[23].Sql, StringComparison.Ordinal);
        Assert.Contains("autonomy_decisions", upMigrations[24].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[24].Sql, StringComparison.OrdinalIgnoreCase);
    }
}
