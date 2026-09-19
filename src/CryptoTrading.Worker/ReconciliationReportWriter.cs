using System.Text.Json;

namespace CryptoTrading.Worker;

/// <summary>Redacted append-only summary of one authenticated reconciliation run.</summary>
internal sealed record ReconciliationRunReport(
    string RunId,
    DateTimeOffset ObservedAtUtc,
    bool IsHealthy,
    int EventCount,
    int MismatchCount,
    int UnknownTradeCount,
    int OpenOrderCount = 0,
    string ExecutionMode = "READ_ONLY_RECONCILIATION")
{
    /// <summary>Validates the redacted post-reconciliation evidence contract before it is persisted.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId) ||
            RunId.Length > 128 ||
            RunId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Report RunId must be a bounded opaque identifier.", nameof(RunId));
        }

        if (ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Report observation time must be UTC.", nameof(ObservedAtUtc));
        }

        if (!string.Equals(ExecutionMode, "READ_ONLY_RECONCILIATION", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reconciliation evidence must declare READ_ONLY_RECONCILIATION mode.");
        }

        if (EventCount < 0 || MismatchCount < 0 || UnknownTradeCount < 0 || OpenOrderCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EventCount), "Reconciliation report counts cannot be negative.");
        }

        if (MismatchCount > EventCount)
        {
            throw new InvalidOperationException("Reconciliation mismatch count cannot exceed event count.");
        }

        if (IsHealthy && (MismatchCount > 0 || UnknownTradeCount > 0))
        {
            throw new InvalidOperationException("A healthy reconciliation cannot contain mismatches or unknown trades.");
        }

        if (!IsHealthy && EventCount == 0)
        {
            throw new InvalidOperationException("An unhealthy reconciliation must retain at least one event for auditability.");
        }
    }
}

/// <summary>Persists reconciliation evidence without exchange payloads or balances.</summary>
internal sealed class ReconciliationReportWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ReconciliationReportWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new ArgumentException("The report directory cannot be a filesystem root.", nameof(directory));
        }

        this.directory = fullPath;
    }

    public async ValueTask<string> AppendAsync(
        ReconciliationRunReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.Validate();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"reconciliation-{report.ObservedAtUtc:yyyyMMdd}.jsonl");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4_096, useAsync: true);
            using var writer = new StreamWriter(stream, leaveOpen: false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
