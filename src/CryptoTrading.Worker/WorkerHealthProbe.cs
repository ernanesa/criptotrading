#pragma warning disable CA1515 // Public contract enables host-level safety tests.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CryptoTrading.Worker;

public sealed record WorkerProbeSnapshot(
    [property: JsonPropertyName("processAlive")] bool ProcessAlive,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("runtimeState")] string RuntimeState,
    [property: JsonPropertyName("executionMode")] string ExecutionMode,
    [property: JsonPropertyName("noTrade")] bool NoTrade,
    [property: JsonPropertyName("persistenceHealthy")] bool PersistenceHealthy,
    [property: JsonPropertyName("diskSpaceHealthy")] bool DiskSpaceHealthy,
    [property: JsonPropertyName("financialReady")] bool FinancialReady);

public static class WorkerHealthProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static async Task WriteProbeSnapshotAsync(
        string? probeFilePath,
        WorkerProbeSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(probeFilePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(probeFilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".probe.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Ignore best-effort cleanup failure
                }
            }
        }
    }

    public static async Task<WorkerProbeSnapshot?> ReadProbeSnapshotAsync(
        string? probeFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(probeFilePath) || !File.Exists(probeFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(probeFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkerProbeSnapshot>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static HealthCheckResult EvaluateLiveness(
        WorkerProbeSnapshot? snapshot,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (snapshot is null)
        {
            return HealthCheckResult.Unhealthy("Worker probe snapshot is missing.");
        }

        if (!snapshot.ProcessAlive)
        {
            return HealthCheckResult.Unhealthy("Worker process is not alive.");
        }

        var age = now - snapshot.UpdatedAtUtc;
        if (age < TimeSpan.Zero || age > maxAge)
        {
            return HealthCheckResult.Unhealthy($"Worker probe snapshot is stale ({age.TotalSeconds:F1}s old).");
        }

        return HealthCheckResult.Healthy("Worker process is alive and reporting heartbeats.");
    }

    public static HealthCheckResult EvaluateFinancialReadiness(
        WorkerProbeSnapshot? snapshot,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (snapshot is null)
        {
            return HealthCheckResult.Unhealthy("Worker probe snapshot is missing.");
        }

        var age = now - snapshot.UpdatedAtUtc;
        if (age < TimeSpan.Zero || age > maxAge)
        {
            return HealthCheckResult.Unhealthy($"Worker probe snapshot is stale ({age.TotalSeconds:F1}s old).");
        }

        if (!snapshot.ProcessAlive)
        {
            return HealthCheckResult.Unhealthy("Worker process is not alive.");
        }

        if (!snapshot.DiskSpaceHealthy)
        {
            return HealthCheckResult.Unhealthy("Worker disk space is below fail-closed safety threshold.");
        }

        if (!snapshot.PersistenceHealthy)
        {
            return HealthCheckResult.Unhealthy("Worker persistence is unhealthy.");
        }

        if (snapshot.NoTrade ||
            string.Equals(snapshot.RuntimeState, "SafeMode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.RuntimeState, "KillSwitch", StringComparison.OrdinalIgnoreCase) ||
            !snapshot.FinancialReady)
        {
            return HealthCheckResult.Degraded(
                $"Worker is in safe/non-trading mode (NoTrade={snapshot.NoTrade}, RuntimeState={snapshot.RuntimeState}, FinancialReady={snapshot.FinancialReady}). Process is healthy but not ready for financial execution.");
        }

        return HealthCheckResult.Healthy("Worker is financially ready for active execution.");
    }
}
