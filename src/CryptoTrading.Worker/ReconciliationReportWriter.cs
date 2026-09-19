using System.Text.Json;
using CryptoTrading.Reconciliation;

namespace CryptoTrading.Worker;

/// <summary>Redacted append-only summary of one authenticated reconciliation run.</summary>
internal sealed record ReconciliationRunReport(
    string RunId,
    DateTimeOffset ObservedAtUtc,
    bool IsHealthy,
    int EventCount,
    int MismatchCount,
    int UnknownTradeCount,
    string ExecutionMode = "READ_ONLY_RECONCILIATION");

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
        if (report.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Report observation time must be UTC.", nameof(report));
        }

        if (string.IsNullOrWhiteSpace(report.RunId))
        {
            throw new ArgumentException("Report RunId is required.", nameof(report));
        }

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
