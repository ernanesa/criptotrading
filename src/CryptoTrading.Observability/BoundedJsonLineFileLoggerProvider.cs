using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CryptoTrading.Observability;

/// <summary>
/// Opt-in JSONL file logging for diagnostics and incident reconstruction.
/// It is intentionally separate from the durable financial audit trail: the
/// channel is bounded, records can be dropped under extreme pressure, and the
/// financial audit/outbox remains the authoritative source of truth.
/// </summary>
public sealed class BoundedJsonLineFileLoggerProvider : ILoggerProvider
{
    private const int DefaultCapacity = 4_096;
    private const long MinimumFileSize = 1_048_576L;

    private readonly string filePath;
    private readonly long maxFileBytes;
    private readonly int retainedFiles;
    private readonly Channel<LogRecord> channel;
    private readonly CancellationTokenSource writerCancellation = new();
    private readonly Task writerTask;
    private long droppedRecords;
    private int disposed;

    public BoundedJsonLineFileLoggerProvider(
        string filePath,
        long maxFileBytes = 50_000_000L,
        int retainedFiles = 3)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A log file path is required.", nameof(filePath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, MinimumFileSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retainedFiles, 20);

        this.filePath = Path.GetFullPath(filePath);
        this.maxFileBytes = maxFileBytes;
        this.retainedFiles = retainedFiles;

        var directory = Path.GetDirectoryName(this.filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        }, _ => Interlocked.Increment(ref droppedRecords));

        writerTask = Task.Run(WriteLoopAsync);
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool TryWrite(LogRecord record)
    {
        if (channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref droppedRecords);
        return false;
    }

#pragma warning disable CA1031 // File logging must never crash the trading host.
#pragma warning disable CA2000 // Ownership of the writer is transferred to the finally block.
    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(writerCancellation.Token).ConfigureAwait(false))
            {
                writer ??= OpenWriter();
                var line = JsonSerializer.Serialize(record);
                var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (writer.BaseStream.Length + lineBytes > maxFileBytes)
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                    writer = null;
                    Rotate();
                    writer = OpenWriter();
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (writerCancellation.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            // Logging must never crash the trading host. The durable financial
            // audit trail remains authoritative if file logging fails.
            await Console.Error.WriteLineAsync(
                $"JSONL file logger stopped: {exception.GetType().Name}").ConfigureAwait(false);
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
#pragma warning restore CA2000
#pragma warning restore CA1031

    private StreamWriter OpenWriter()
    {
        var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
    }

    private void Rotate()
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{filePath}.{index}";
            var destination = $"{filePath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(filePath, $"{filePath}.1", overwrite: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        channel.Writer.TryComplete();
        try
        {
            if (!writerTask.Wait(TimeSpan.FromSeconds(5)))
            {
                writerCancellation.Cancel();
            }
        }
        catch (AggregateException)
        {
            writerCancellation.Cancel();
        }
        finally
        {
            writerCancellation.Dispose();
        }
    }

    internal sealed record LogRecord(
        DateTimeOffset TimestampUtc,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage,
        string? ExceptionStackTrace);

    private sealed class FileLogger(BoundedJsonLineFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = SecretRedactor.RedactEmbeddedSecrets(formatter(state, exception));
            _ = provider.TryWrite(new LogRecord(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                category,
                eventId.Id,
                eventId.Name,
                message,
                exception?.GetType().FullName,
                exception?.Message is { } exceptionMessage ? SecretRedactor.RedactEmbeddedSecrets(exceptionMessage) : null,
                logLevel >= LogLevel.Error && exception?.StackTrace is { } stack ? SecretRedactor.RedactEmbeddedSecrets(stack) : null));
        }
    }
}
