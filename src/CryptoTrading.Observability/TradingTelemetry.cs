using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

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
    private static long alertDeliveries;
    private static long alertDeliveryFailures;
    private static long alertDeduplicated;
    private static long reconciliationRuns;
    private static long reconciliationMismatches;
    private static long outboxDispatches;
    private static long outboxDispatchFailures;
    private static long outboxDeadLetters;
    private static long invalidMarketDataPayloads;

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
    public static readonly Counter<long> AlertDeliveries = Meter.CreateCounter<long>(
        "cryptotrading.alert.deliveries",
        unit: "{alert}",
        description: "Alerts delivered to the configured sink.");
    public static readonly Counter<long> AlertDeliveryFailures = Meter.CreateCounter<long>(
        "cryptotrading.alert.delivery_failures",
        unit: "{failure}",
        description: "Alert delivery attempts that failed.");
    public static readonly Counter<long> AlertDeduplicated = Meter.CreateCounter<long>(
        "cryptotrading.alert.deduplicated",
        unit: "{alert}",
        description: "Alerts suppressed by deduplication key.");
    public static readonly Counter<long> ReconciliationRuns = Meter.CreateCounter<long>(
        "cryptotrading.reconciliation.runs",
        unit: "{run}",
        description: "Completed account reconciliation runs.");
    public static readonly Counter<long> ReconciliationMismatches = Meter.CreateCounter<long>(
        "cryptotrading.reconciliation.mismatches",
        unit: "{mismatch}",
        description: "Reconciliation mismatches that entered the fail-closed path.");
    public static readonly Counter<long> OutboxDispatches = Meter.CreateCounter<long>(
        "cryptotrading.outbox.dispatches",
        unit: "{message}",
        description: "Outbox dispatch attempts by outcome.");
    public static readonly Histogram<double> OutboxQueueLag = Meter.CreateHistogram<double>(
        "cryptotrading.outbox.queue_lag",
        unit: "ms",
        description: "Elapsed time between an outbox message becoming available and being claimed.");
    public static readonly Counter<long> InvalidMarketDataPayloads = Meter.CreateCounter<long>(
        "cryptotrading.market_data.invalid_payloads",
        unit: "{payload}",
        description: "Market-data payloads rejected before entering strategy processing.");

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

    public static void RecordAlertDelivery(TradingAlertSeverity severity)
    {
        AlertDeliveries.Add(1, new KeyValuePair<string, object?>("severity", severity.ToString()));
        Interlocked.Increment(ref alertDeliveries);
    }

    public static void RecordAlertDeliveryFailure(TradingAlertSeverity severity)
    {
        AlertDeliveryFailures.Add(1, new KeyValuePair<string, object?>("severity", severity.ToString()));
        Interlocked.Increment(ref alertDeliveryFailures);
    }

    public static void RecordAlertDeduplicated()
    {
        AlertDeduplicated.Add(1);
        Interlocked.Increment(ref alertDeduplicated);
    }

    public static void RecordReconciliation(bool healthy, int mismatchCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mismatchCount);
        var outcome = healthy ? "healthy" : "mismatch";
        ReconciliationRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Interlocked.Increment(ref reconciliationRuns);
        if (mismatchCount > 0)
        {
            ReconciliationMismatches.Add(mismatchCount);
            Interlocked.Add(ref reconciliationMismatches, mismatchCount);
        }
    }

    public static void RecordOutboxQueueLag(TimeSpan lag)
    {
        if (lag < TimeSpan.Zero)
        {
            return;
        }

        OutboxQueueLag.Record(lag.TotalMilliseconds);
    }

    public static void RecordOutboxDispatch(bool succeeded, bool deadLettered = false)
    {
        var outcome = succeeded ? "processed" : deadLettered ? "dead_lettered" : "retry";
        OutboxDispatches.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Interlocked.Increment(ref outboxDispatches);
        if (!succeeded)
        {
            Interlocked.Increment(ref outboxDispatchFailures);
        }

        if (deadLettered)
        {
            Interlocked.Increment(ref outboxDeadLetters);
        }
    }

    public static void RecordInvalidMarketDataPayload()
    {
        InvalidMarketDataPayloads.Add(1);
        Interlocked.Increment(ref invalidMarketDataPayloads);
    }

    public static TradingMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref apiRequests),
        Interlocked.Read(ref apiErrors),
        Interlocked.Read(ref workerHeartbeats),
        Interlocked.Read(ref apiDurationMilliseconds),
        Interlocked.Read(ref alertDeliveries),
        Interlocked.Read(ref alertDeliveryFailures),
        Interlocked.Read(ref alertDeduplicated),
        Interlocked.Read(ref reconciliationRuns),
        Interlocked.Read(ref reconciliationMismatches),
        Interlocked.Read(ref outboxDispatches),
        Interlocked.Read(ref outboxDispatchFailures),
        Interlocked.Read(ref outboxDeadLetters),
        Interlocked.Read(ref invalidMarketDataPayloads));
}

public sealed record TradingMetricsSnapshot(
    long ApiRequests,
    long ApiErrors,
    long WorkerHeartbeats,
    long ApiDurationMilliseconds,
    long AlertDeliveries,
    long AlertDeliveryFailures,
    long AlertDeduplicated,
    long ReconciliationRuns,
    long ReconciliationMismatches,
    long OutboxDispatches,
    long OutboxDispatchFailures,
    long OutboxDeadLetters,
    long InvalidMarketDataPayloads);

public static class SecretRedactor
{
    private static readonly string[] SensitiveNameFragments = ["key", "secret", "token", "password", "credential", "authorization"];
    private static readonly Regex AuthorizationHeader = new(
        @"(?<prefix>\b(?:proxy-)?authorization\s*:\s*)(?<value>[^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationScheme = new(
        @"(?<prefix>\b(?:bearer|basic|token)\s+)(?<value>[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveAssignment = new(
        """(?<prefix>(?:(?:[?&;]\s*|(?:^|[\s,{]))(?:x-mbx-apikey|api[-_]?key|access[-_]?token|id[-_]?token|refresh[-_]?token|token|secret|password|credential)\s*(?:=|:)\s*["']?))(?<value>[^&#\s,;"'}]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UriCredentials = new(
        @"(?<prefix>\b[a-z][a-z0-9+.-]*://[^:/\s]+:)(?<value>[^@/\s]+)(?<suffix>@)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SensitiveNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static string Redact(string? value) => string.IsNullOrEmpty(value)
        ? "[empty]"
        : "[redacted]";

    /// <summary>Removes secret-bearing fragments from a textual diagnostic value.</summary>
    public static string RedactEmbeddedSecrets(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = AuthorizationHeader.Replace(value, "${prefix}[redacted]");
        redacted = AuthorizationScheme.Replace(redacted, "${prefix}[redacted]");
        redacted = SensitiveAssignment.Replace(redacted, "${prefix}[redacted]");
        return UriCredentials.Replace(redacted, "${prefix}[redacted]${suffix}");
    }

    public static object? Value(string? name, object? value) => IsSensitiveName(name)
        ? Redact(value?.ToString())
        : value switch
        {
            string text => RedactEmbeddedSecrets(text),
            Uri uri => RedactEmbeddedSecrets(uri.OriginalString),
            _ => value
        };
}
