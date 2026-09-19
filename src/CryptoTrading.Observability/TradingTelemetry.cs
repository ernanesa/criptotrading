using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CryptoTrading.Observability;

/// <summary>
/// Low-cardinality financial runtime instruments. Exporters can subscribe to this meter later.
/// </summary>
public static class TradingTelemetry
{
    public const string ServiceName = "CryptoTrading";
    public const string MeterName = "CryptoTrading.Runtime";
    public const string ActivitySourceName = "CryptoTrading.Runtime";

    private static long apiRequests;
    private static long apiErrors;
    private static long workerHeartbeats;
    private static long apiDurationMilliseconds;

    public static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Counter<long> ApiRequests = Meter.CreateCounter<long>(
        "cryptotrading.api.requests",
        unit: "{request}",
        description: "Completed API requests.");
    public static readonly Counter<long> ApiErrors = Meter.CreateCounter<long>(
        "cryptotrading.api.errors",
        unit: "{error}",
        description: "API requests completed with a 5xx status.");
    public static readonly Histogram<double> ApiDuration = Meter.CreateHistogram<double>(
        "cryptotrading.api.duration",
        unit: "ms",
        description: "API request duration in milliseconds.");
    public static readonly Counter<long> WorkerHeartbeats = Meter.CreateCounter<long>(
        "cryptotrading.worker.heartbeats",
        unit: "{heartbeat}",
        description: "Worker heartbeat cycles.");

    public static void RecordApiRequest(int statusCode, double durationMilliseconds)
    {
        var statusClass = $"{statusCode / 100}xx";
        ApiRequests.Add(1, new KeyValuePair<string, object?>("http.status_class", statusClass));
        ApiDuration.Record(durationMilliseconds);
        Interlocked.Increment(ref apiRequests);
        Interlocked.Add(ref apiDurationMilliseconds, (long)Math.Round(durationMilliseconds, MidpointRounding.AwayFromZero));

        if (statusCode >= 500)
        {
            ApiErrors.Add(1, new KeyValuePair<string, object?>("http.status_class", statusClass));
            Interlocked.Increment(ref apiErrors);
        }
    }

    public static void RecordWorkerHeartbeat()
    {
        WorkerHeartbeats.Add(1);
        Interlocked.Increment(ref workerHeartbeats);
    }

    public static TradingMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref apiRequests),
        Interlocked.Read(ref apiErrors),
        Interlocked.Read(ref workerHeartbeats),
        Interlocked.Read(ref apiDurationMilliseconds));
}

public sealed record TradingMetricsSnapshot(
    long ApiRequests,
    long ApiErrors,
    long WorkerHeartbeats,
    long ApiDurationMilliseconds);

public static class SecretRedactor
{
    private static readonly string[] SensitiveNameFragments = ["key", "secret", "token", "password", "credential", "authorization"];

    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SensitiveNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static string Redact(string? value) => string.IsNullOrEmpty(value)
        ? "[empty]"
        : "[redacted]";

    public static object? Value(string? name, object? value) => IsSensitiveName(name) ? Redact(value?.ToString()) : value;
}
