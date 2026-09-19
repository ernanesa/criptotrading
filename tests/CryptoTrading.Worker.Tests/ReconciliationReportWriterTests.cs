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
        AssertUserOnlyFileMode(path);
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

    [Fact]
    public async Task RejectsHealthyReportThatContainsUnresolvedMismatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-reconciliation-invalid-{Guid.NewGuid():N}");
        using var writer = new ReconciliationReportWriter(directory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(new ReconciliationRunReport(
            "run-healthy-with-drift",
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero),
            true,
            1,
            1,
            0)).AsTask());
    }

    [Fact]
    public async Task RejectsNonReadOnlyExecutionModeBeforeWritingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-reconciliation-mode-{Guid.NewGuid():N}");
        using var writer = new ReconciliationReportWriter(directory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(new ReconciliationRunReport(
            "run-mutating-mode",
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero),
            false,
            1,
            1,
            0,
            ExecutionMode: "LIVE")).AsTask());
    }

    [Fact]
    public async Task RejectsUnhealthyReportWithoutAnAuditEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-reconciliation-no-event-{Guid.NewGuid():N}");
        using var writer = new ReconciliationReportWriter(directory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(new ReconciliationRunReport(
            "run-no-event",
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero),
            false,
            0,
            0,
            0)).AsTask());
    }

    private static void AssertUserOnlyFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }
}
