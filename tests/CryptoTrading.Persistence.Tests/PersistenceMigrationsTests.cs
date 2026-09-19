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
            ],
            migrations.Select(static migration => migration.FileName));
        Assert.All(migrations, static migration => Assert.NotEmpty(migration.Sql));
        Assert.Equal(
            ["001", "001", "002", "002", "003", "003", "004", "004", "005", "005", "006", "006", "007", "007", "008", "008", "009", "009"],
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
    }
}
