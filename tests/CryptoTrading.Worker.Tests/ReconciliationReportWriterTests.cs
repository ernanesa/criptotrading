using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ReconciliationReportWriterTests
{
    [Fact]
    public async Task AppendsRedactedJsonLineWithoutBalancesOrPayloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-reconciliation-{Guid.NewGuid():N}");
        using var writer = new ReconciliationReportWriter(directory);
        var path = await writer.AppendAsync(new ReconciliationRunReport(
            "run-1",
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero),
            false,
            2,
            1,
            3,
            4));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
        Assert.Contains("READ_ONLY_RECONCILIATION", lines[0], StringComparison.Ordinal);
        Assert.Contains("unknownTradeCount", lines[0], StringComparison.Ordinal);
        Assert.Contains("openOrderCount", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("balance", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsNonUtcObservationTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-reconciliation-utc-{Guid.NewGuid():N}");
        using var writer = new ReconciliationReportWriter(directory);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(new ReconciliationRunReport(
            "run-1",
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.FromHours(-3)),
            true,
            1,
            0,
            0)).AsTask());
    }
}
