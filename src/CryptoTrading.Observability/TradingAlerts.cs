using System.Collections.Concurrent;
using System.Text.Json;

namespace CryptoTrading.Observability;

public enum TradingAlertSeverity
{
    Warning,
    Critical
}

/// <summary>
/// Operational alert envelope. It deliberately contains context needed by an
/// operator and no exchange payload, credential or financial secret.
/// </summary>
public sealed record TradingAlert(
    string Environment,
    string Exchange,
    string Scope,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    TradingAlertSeverity Severity,
    string Owner,
    string Action,
    string Runbook,
    string DeduplicationKey)
{
    public void Validate()
    {
        Require(Environment, nameof(Environment));
        Require(Exchange, nameof(Exchange));
        Require(Scope, nameof(Scope));
        Require(CorrelationId, nameof(CorrelationId));
        Require(Owner, nameof(Owner));
        Require(Action, nameof(Action));
        Require(Runbook, nameof(Runbook));
        Require(DeduplicationKey, nameof(DeduplicationKey));

        if (OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("OccurredAtUtc must use UTC.", nameof(OccurredAtUtc));
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }
}

public interface ITradingAlertSink
{
    ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local sink used by deterministic tests and local drills. It is not
/// an external alert channel and must not be treated as delivery evidence for
/// production/on-call routing.
/// </summary>
public sealed class InMemoryTradingAlertSink : ITradingAlertSink
{
    private readonly ConcurrentQueue<TradingAlert> alerts = new();

    public IReadOnlyList<TradingAlert> Alerts => alerts.ToArray();

    public ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        alerts.Enqueue(alert);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Local append-only JSONL alert sink. The envelope contains no exchange
/// payloads or credentials and is suitable for offline operational evidence.
/// </summary>
public sealed class JsonlTradingAlertSink : ITradingAlertSink, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directoryPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonlTradingAlertSink(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath.Trim());
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new ArgumentException("Alert directory cannot be a filesystem root.", nameof(directoryPath));
        }

        Directory.CreateDirectory(fullPath);
        this.directoryPath = fullPath;
    }

    public async ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = Path.Combine(directoryPath, $"alerts-{alert.OccurredAtUtc:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(alert, JsonOptions) + Environment.NewLine;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            RestrictFilePermissions(filePath);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose() => gate.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Delivers alerts once per deduplication key during the process lifetime.
/// Failed delivery is not remembered, allowing a caller to retry safely.
/// </summary>
public sealed class TradingAlertDispatcher : IDisposable
{
    private readonly ITradingAlertSink sink;
    private readonly HashSet<string> deliveredKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    public TradingAlertDispatcher(ITradingAlertSink sink)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async ValueTask<bool> PublishAsync(TradingAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Validate();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (deliveredKeys.Contains(alert.DeduplicationKey))
            {
                TradingTelemetry.RecordAlertDeduplicated();
                return false;
            }

            try
            {
                await sink.DeliverAsync(alert, cancellationToken).ConfigureAwait(false);
                deliveredKeys.Add(alert.DeduplicationKey);
                TradingTelemetry.RecordAlertDelivery(alert.Severity);
                return true;
            }
            catch
            {
                TradingTelemetry.RecordAlertDeliveryFailure(alert.Severity);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
