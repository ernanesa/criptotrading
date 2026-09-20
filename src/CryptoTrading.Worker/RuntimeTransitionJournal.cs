using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Runtime;

#pragma warning disable CA2007 // FileStream disposal is configured by the async using pattern at scope exit.

namespace CryptoTrading.Worker;

/// <summary>
/// Local append-only runtime journal. Each line carries a hash of its canonical
/// transition plus the previous line hash, making truncation/tampering visible
/// during an incident review. It never stores credentials or order payloads.
/// </summary>
internal sealed class RuntimeTransitionJournal : IRuntimeTransitionSink, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string previousHash = string.Empty;

    public RuntimeTransitionJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path.Trim());
    }

    public async ValueTask AppendAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var canonical = JsonSerializer.Serialize(transition, JsonOptions);
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var chainHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + payloadHash)));
            var line = JsonSerializer.Serialize(new RuntimeTransitionJournalEntry(
                transition,
                previousHash,
                payloadHash,
                chainHash), JsonOptions) + Environment.NewLine;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4_096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Lifecycle evidence must be flushed to the local disk before publication.
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            previousHash = chainHash;
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => gate.Dispose();

    private sealed record RuntimeTransitionJournalEntry(
        RuntimeStateTransition Transition,
        string PreviousHash,
        string PayloadSha256,
        string ChainSha256);
}
