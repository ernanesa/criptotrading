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
            ],
            migrations.Select(static migration => migration.FileName));
        Assert.All(migrations, static migration => Assert.NotEmpty(migration.Sql));
        Assert.Equal(
            ["001", "001", "002", "002", "003", "003", "004", "004", "005", "005"],
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
    }
}
