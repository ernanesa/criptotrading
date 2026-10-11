using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class PersistenceMigrationsTests
{
    [Fact]
    public void MigrationsAreEmbeddedAndOrdered()
    {
        var migrations = PersistenceMigrations.ReadAll();
        PersistenceMigrations.ValidateInventory(migrations);

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
                "026_autonomous_regimes_models_and_drift.down.sql", "026_autonomous_regimes_models_and_drift.up.sql",
                "027_submission_side_effect_fence.down.sql", "027_submission_side_effect_fence.up.sql",
                "028_portfolio_performance_snapshots.down.sql", "028_portfolio_performance_snapshots.up.sql",
 "029_execution_learning_rewards.down.sql", "029_execution_learning_rewards.up.sql",
 "030_reconciliation_known_trade_composite_identity.down.sql", "030_reconciliation_known_trade_composite_identity.up.sql",
 "031_reconciliation_events.down.sql", "031_reconciliation_events.up.sql",
 "032_open_order_exchange_update_time.down.sql", "032_open_order_exchange_update_time.up.sql",
            ],
            migrations.Select(static migration => migration.FileName));
        Assert.All(migrations, static migration => Assert.NotEmpty(migration.Sql));
        Assert.Equal(
    ["001", "001", "002", "002", "003", "003", "004", "004", "005", "005", "006", "006", "007", "007", "008", "008", "009", "009", "010", "010", "011", "011", "012", "012", "013", "013", "014", "014", "015", "015", "016", "016", "017", "017", "018", "018", "019", "019", "020", "020", "021", "021", "022", "022", "023", "023", "024", "024", "025", "025", "026", "026", "027", "027", "028", "028", "029", "029", "030", "030", "031", "031", "032", "032"],
            migrations.Select(static migration => migration.Version));
    }

    [Fact]
    public void MigrationInventoryRejectsDuplicatesGapsAndOutOfOrderVersions()
    {
        var versionOne = Pair("001");
        var versionTwo = Pair("002");
        var versionThree = Pair("003");

        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory(
            [.. versionOne, versionOne[0]]));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory(
            [versionOne[0], versionOne[1], versionThree[0], versionThree[1]]));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory(
            [.. versionTwo, .. versionOne]));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory(
            [versionOne[0]]));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory([]));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateInventory(Pair("1")));
    }

    [Fact]
    public void AppliedMigrationVersionsMustBeAnExactContiguousKnownPrefix()
    {
        MigrationScript[] inventory = [.. Pair("001"), .. Pair("002"), .. Pair("003")];

        PersistenceMigrations.ValidateAppliedVersions([], inventory);
        PersistenceMigrations.ValidateAppliedVersions(["001"], inventory);
        PersistenceMigrations.ValidateAppliedVersions(["001", "002", "003"], inventory);

        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateAppliedVersions(
            ["001", "003"], inventory));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateAppliedVersions(
            ["001", "002", "004"], inventory));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateAppliedVersions(
            ["001", "002", "003", "004"], inventory));
    }

    [Fact]
    public void SourceIntegrityAcceptsMatchingRawMigrationBytes()
    {
        var source = Encoding.UTF8.GetBytes("-- stable migration source\n");
        MigrationScript[] migrations =
        [
            new("001", "up", "001_test.up.sql", Encoding.UTF8.GetString(source), source),
        ];
        IReadOnlyDictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Migrations/001_test.up.sql"] = Convert.ToHexString(SHA256.HashData(source)),
        };

        PersistenceMigrations.ValidateSourceIntegrity(migrations, hashes);
    }

    [Fact]
    public void SourceIntegrityRejectsMissingDuplicateAndTamperedManifestEntries()
    {
        var source = Encoding.UTF8.GetBytes("-- original migration source\n");
        MigrationScript migration = new("001", "up", "001_test.up.sql", Encoding.UTF8.GetString(source), source);
        IReadOnlyDictionary<string, string> matchingHash = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Migrations/001_test.up.sql"] = Convert.ToHexString(SHA256.HashData(source)),
        };
        IReadOnlyDictionary<string, string> wrongHash = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Migrations/001_test.up.sql"] = new string('0', 64),
        };

        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateSourceIntegrity([migration], new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateSourceIntegrity([migration, migration], matchingHash));
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateSourceIntegrity([migration], wrongHash));

        var changedSource = Encoding.UTF8.GetBytes("-- changed migration source\n");
        var changedMigration = migration with
        {
            Sql = Encoding.UTF8.GetString(changedSource),
            RawSql = changedSource,
        };
        Assert.Throws<InvalidOperationException>(() => PersistenceMigrations.ValidateSourceIntegrity([changedMigration], matchingHash));
    }

    private static MigrationScript[] Pair(string version) =>
    [
        new(version, "down", $"{version}_test.down.sql", "-- down"),
        new(version, "up", $"{version}_test.up.sql", "-- up"),
    ];

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
        Assert.Contains("market_regime_snapshots", upMigrations[25].Sql, StringComparison.Ordinal);
        Assert.Contains("strategy_models", upMigrations[25].Sql, StringComparison.Ordinal);
        Assert.Contains("drift_observations", upMigrations[25].Sql, StringComparison.Ordinal);
        Assert.Contains("strategy_regime_performances", upMigrations[25].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[25].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution_submission_starts", upMigrations[26].Sql, StringComparison.Ordinal);
        Assert.Contains("portfolio_nav_snapshots", upMigrations[27].Sql, StringComparison.Ordinal);
        Assert.Contains("external_cash_flow_quote", upMigrations[27].Sql, StringComparison.Ordinal);
        Assert.Contains("reconciliation_known_trades", upMigrations[29].Sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (symbol, trade_id)", upMigrations[29].Sql, StringComparison.Ordinal);
        Assert.Contains("reconciliation_events", upMigrations[30].Sql, StringComparison.Ordinal);
        Assert.Contains("append_only", upMigrations[30].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exchange_updated_at", upMigrations[31].Sql, StringComparison.Ordinal);
    }
}
