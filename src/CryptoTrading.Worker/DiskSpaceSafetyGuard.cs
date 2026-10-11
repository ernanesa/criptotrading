#pragma warning disable CA1515 // Public contract enables host-level safety tests.
#pragma warning disable CA1031 // Disk space safety guard must fail closed on any probe exception.

using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Worker;

public sealed record DiskSpaceCheckResult(
    bool IsHealthy,
    long AvailableFreeBytes,
    long TotalBytes,
    decimal FreeRatio,
    string? FailureReason);

public sealed class DiskSpaceSafetyGuard
{
    private readonly long minFreeDiskBytes;
    private readonly decimal minFreeDiskRatio;
    private readonly string targetPath;
    private readonly IOperationalControl operationalControl;
    private readonly ILogger<DiskSpaceSafetyGuard> logger;
    private readonly Func<string, (long AvailableFreeBytes, long TotalBytes)> spaceProvider;

    public DiskSpaceSafetyGuard(
        long minFreeDiskBytes,
        decimal minFreeDiskRatio,
        string targetPath,
        IOperationalControl operationalControl,
        ILogger<DiskSpaceSafetyGuard> logger,
        Func<string, (long AvailableFreeBytes, long TotalBytes)>? spaceProvider = null)
    {
        this.minFreeDiskBytes = minFreeDiskBytes > 0 ? minFreeDiskBytes : 2_147_483_648L; // 2 GB
        this.minFreeDiskRatio = minFreeDiskRatio > 0m && minFreeDiskRatio < 1m ? minFreeDiskRatio : 0.10m; // 10%
        this.targetPath = !string.IsNullOrWhiteSpace(targetPath) ? targetPath : "/";
        this.operationalControl = operationalControl ?? throw new ArgumentNullException(nameof(operationalControl));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.spaceProvider = spaceProvider ?? DefaultSpaceProvider;
    }

    private static (long AvailableFreeBytes, long TotalBytes) DefaultSpaceProvider(string path)
    {
        var pathToCheck = Directory.Exists(path) ? path : (Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/");
        if (!Directory.Exists(pathToCheck))
        {
            pathToCheck = "/";
        }

        var driveInfo = new DriveInfo(pathToCheck);
        return (driveInfo.AvailableFreeSpace, driveInfo.TotalSize);
    }

    public DiskSpaceCheckResult CheckDiskSpace()
    {
        try
        {
            var (availableBytes, totalBytes) = spaceProvider(targetPath);
            var ratio = totalBytes > 0 ? (decimal)availableBytes / totalBytes : 0m;

            if (availableBytes < minFreeDiskBytes)
            {
                var reason = $"Available disk space {availableBytes} bytes is below threshold {minFreeDiskBytes} bytes.";
                TriggerSafeMode(reason);
                return new DiskSpaceCheckResult(false, availableBytes, totalBytes, ratio, reason);
            }

            if (ratio < minFreeDiskRatio)
            {
                var reason = $"Available disk space ratio {ratio:P2} is below threshold {minFreeDiskRatio:P2}.";
                TriggerSafeMode(reason);
                return new DiskSpaceCheckResult(false, availableBytes, totalBytes, ratio, reason);
            }

            return new DiskSpaceCheckResult(true, availableBytes, totalBytes, ratio, null);
        }
        catch (Exception ex)
        {
            var reason = $"Disk space check failed with error: {ex.Message}";
            DiskSpaceGuardLog.CheckFailed(logger, targetPath, ex);
            TriggerSafeMode(reason);
            return new DiskSpaceCheckResult(false, 0, 0, 0m, reason);
        }
    }

    private void TriggerSafeMode(string reason)
    {
        if (operationalControl.Snapshot.Mode == OperationalMode.Normal)
        {
            DiskSpaceGuardLog.LowDiskTriggered(logger, reason);
            operationalControl.EnterSafeMode(new OperationalControlCommand(
                "DiskSpaceSafetyGuard",
                $"LOW_DISK_SPACE_FAIL_CLOSED: {reason}",
                IdempotencyKey.New(),
                DateTimeOffset.UtcNow));
        }
    }
}

internal static partial class DiskSpaceGuardLog
{
    [LoggerMessage(LogLevel.Error, "Disk space safety guard encountered an exception checking {TargetPath}")]
    public static partial void CheckFailed(ILogger logger, string targetPath, Exception exception);

    [LoggerMessage(LogLevel.Critical, "LOW DISK SPACE DETECTED: {Reason}. Latching operational control to SafeMode (LOW_DISK_SPACE_FAIL_CLOSED).")]
    public static partial void LowDiskTriggered(ILogger logger, string reason);
}
