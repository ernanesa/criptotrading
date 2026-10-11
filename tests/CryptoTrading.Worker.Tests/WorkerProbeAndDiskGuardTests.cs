using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class WorkerProbeAndDiskGuardTests
{
    [Fact]
    public void EvaluateLivenessReturnsHealthyWhenProcessAliveAndFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: true,
            UpdatedAtUtc: now.AddSeconds(-2),
            RuntimeState: "Ready",
            ExecutionMode: "PAPER",
            NoTrade: true,
            PersistenceHealthy: true,
            DiskSpaceHealthy: true,
            FinancialReady: false);

        var result = WorkerHealthProbe.EvaluateLiveness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void EvaluateLivenessReturnsUnhealthyWhenProcessNotAlive()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: false,
            UpdatedAtUtc: now,
            RuntimeState: "Stopped",
            ExecutionMode: "PAPER",
            NoTrade: true,
            PersistenceHealthy: true,
            DiskSpaceHealthy: true,
            FinancialReady: false);

        var result = WorkerHealthProbe.EvaluateLiveness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not alive", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateLivenessReturnsUnhealthyWhenStale()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: true,
            UpdatedAtUtc: now.AddSeconds(-30),
            RuntimeState: "Ready",
            ExecutionMode: "PAPER",
            NoTrade: true,
            PersistenceHealthy: true,
            DiskSpaceHealthy: true,
            FinancialReady: false);

        var result = WorkerHealthProbe.EvaluateLiveness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("stale", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateFinancialReadinessReturnsDegradedInNoTradeOrSafeMode()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: true,
            UpdatedAtUtc: now,
            RuntimeState: "SafeMode",
            ExecutionMode: "TESTNET",
            NoTrade: true,
            PersistenceHealthy: true,
            DiskSpaceHealthy: true,
            FinancialReady: false);

        var result = WorkerHealthProbe.EvaluateFinancialReadiness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("safe/non-trading mode", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateFinancialReadinessReturnsUnhealthyWhenDiskSpaceLow()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: true,
            UpdatedAtUtc: now,
            RuntimeState: "Ready",
            ExecutionMode: "TESTNET",
            NoTrade: false,
            PersistenceHealthy: true,
            DiskSpaceHealthy: false,
            FinancialReady: false);

        var result = WorkerHealthProbe.EvaluateFinancialReadiness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("disk space", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateFinancialReadinessReturnsHealthyWhenActiveAndSafe()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WorkerProbeSnapshot(
            ProcessAlive: true,
            UpdatedAtUtc: now,
            RuntimeState: "Ready",
            ExecutionMode: "TESTNET",
            NoTrade: false,
            PersistenceHealthy: true,
            DiskSpaceHealthy: true,
            FinancialReady: true);

        var result = WorkerHealthProbe.EvaluateFinancialReadiness(snapshot, now, TimeSpan.FromSeconds(10));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task WriteProbeSnapshotCreatesAndAtomicallyUpdatesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"worker-probe-test-{Guid.NewGuid():N}.json");
        try
        {
            var snapshot = new WorkerProbeSnapshot(
                ProcessAlive: true,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                RuntimeState: "Ready",
                ExecutionMode: "PAPER",
                NoTrade: true,
                PersistenceHealthy: true,
                DiskSpaceHealthy: true,
                FinancialReady: false);

            await WorkerHealthProbe.WriteProbeSnapshotAsync(tempFile, snapshot, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllTextAsync(tempFile, TestContext.Current.CancellationToken);
            Assert.Contains("\"processAlive\":true", content, StringComparison.Ordinal);
            Assert.Contains("\"executionMode\":\"PAPER\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void DiskSpaceSafetyGuardLatchesSafeModeWhenThresholdExceeded()
    {
        var opControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        Assert.Equal(OperationalMode.Normal, opControl.Snapshot.Mode);

        // Setting a huge minimum free disk byte requirement (e.g. 100 TB) guarantees failure
        var guard = new DiskSpaceSafetyGuard(
            minFreeDiskBytes: 100_000_000_000_000L,
            minFreeDiskRatio: 0.99m,
            targetPath: "/",
            operationalControl: opControl,
            logger: NullLogger<DiskSpaceSafetyGuard>.Instance);

        var checkResult = guard.CheckDiskSpace();

        Assert.False(checkResult.IsHealthy);
        Assert.Equal(OperationalMode.SafeMode, opControl.Snapshot.Mode);
        Assert.Contains("LOW_DISK_SPACE_FAIL_CLOSED", opControl.Snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskSpaceSafetyGuardPassesWhenThresholdsAreMet()
    {
        var opControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        Assert.Equal(OperationalMode.Normal, opControl.Snapshot.Mode);

        // Setting small minimums (1 KB, 0.0001 ratio) ensures it passes on any working machine
        var guard = new DiskSpaceSafetyGuard(
            minFreeDiskBytes: 1024L,
            minFreeDiskRatio: 0.00001m,
            targetPath: "/",
            operationalControl: opControl,
            logger: NullLogger<DiskSpaceSafetyGuard>.Instance);

        var checkResult = guard.CheckDiskSpace();

        Assert.True(checkResult.IsHealthy);
        Assert.Equal(OperationalMode.Normal, opControl.Snapshot.Mode);
    }

    private sealed class AllowAllResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(
            OperationalControlSnapshot current,
            OperationalControlCommand resetCommand,
            OperationalControlResetAuthorization authorization) => true;
    }
}
