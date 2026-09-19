using System.Text.Json;

namespace CryptoTrading.Shadow;

/// <summary>Redacted, append-only summary of one local Shadow run.</summary>
public sealed record ShadowRunReport(
    string RunId,
    DateTimeOffset CompletedAtUtc,
    int ObservationCount,
    int DivergenceCount,
    int StaleCount,
    int RecoveryCount,
    string ExecutionMode = "PAPER_SHADOW");

/// <summary>Writes one JSON object per line and never records exchange payloads.</summary>
public sealed class ShadowReportWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ShadowReportWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new ArgumentException("The report directory cannot be a filesystem root.", nameof(directory));
        }

        this.directory = fullPath;
    }

    public string DirectoryPath => directory;

    public async ValueTask<string> AppendAsync(
        ShadowRunReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.CompletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Report completion time must be UTC.", nameof(report));
        }

        if (string.IsNullOrWhiteSpace(report.RunId))
        {
            throw new ArgumentException("Report RunId is required.", nameof(report));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"shadow-{report.CompletedAtUtc:yyyyMMdd}.jsonl");
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
