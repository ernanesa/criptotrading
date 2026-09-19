using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Backtesting;

/// <summary>
/// A versioned, transport-neutral record persisted by the local market-data journal.
/// The payload is intentionally JSON so public exchange adapters do not leak into
/// the replay/persistence boundary.
/// </summary>
public sealed record MarketDataRecord
{
    public const int CurrentSchemaVersion = 1;

    internal MarketDataRecord(
        int schemaVersion,
        long sequence,
        DateTimeOffset eventTimeUtc,
        DateTimeOffset receivedAtUtc,
        string eventType,
        string stream,
        string symbol,
        JsonElement payload,
        string checksum)
    {
        SchemaVersion = schemaVersion;
        Sequence = sequence;
        EventTimeUtc = eventTimeUtc;
        ReceivedAtUtc = receivedAtUtc;
        EventType = eventType;
        Stream = stream;
        Symbol = symbol;
        Payload = payload.Clone();
        Checksum = checksum;
    }

    public int SchemaVersion { get; }
    public long Sequence { get; }
    public DateTimeOffset EventTimeUtc { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public string EventType { get; }
    public string Stream { get; }
    public string Symbol { get; }
    public JsonElement Payload { get; }
    public string Checksum { get; }
}

/// <summary>Controls local journal cleanup. Retention is never implicit.</summary>
public sealed record MarketDataRetentionPolicy
{
    public MarketDataRetentionPolicy(TimeSpan maximumAge, long maximumBytes)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Maximum age must be positive.");
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Maximum bytes must be positive.");
        }

        MaximumAge = maximumAge;
        MaximumBytes = maximumBytes;
    }

    public TimeSpan MaximumAge { get; }
    public long MaximumBytes { get; }
}

public sealed record MarketDataRetentionResult(int FilesDeleted, long BytesDeleted);

public sealed record MarketDataReplay(
    IReadOnlyList<MarketDataRecord> Records,
    string DatasetChecksum);

/// <summary>
/// Local append-only JSONL recorder for public market data.
/// A single recorder instance is the writer for a directory. Files are partitioned
/// by UTC event date, which makes retention and operational inspection bounded.
/// </summary>
public sealed class MarketDataRecorder : IDisposable
{
    private const string FilePrefix = "market-data-";
    private const string FileSuffix = ".jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string directoryPath;
    private long nextSequence;

    public MarketDataRecorder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("A journal directory is required.", nameof(directoryPath));
        }

        this.directoryPath = Path.GetFullPath(directoryPath.Trim());
        Directory.CreateDirectory(this.directoryPath);
        nextSequence = FindNextSequence(this.directoryPath);
    }

    /// <summary>Appends one record and returns the persisted, checksummed envelope.</summary>
    public async Task<MarketDataRecord> AppendAsync(
        DateTimeOffset eventTimeUtc,
        DateTimeOffset receivedAtUtc,
        string eventType,
        string stream,
        string symbol,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(eventTimeUtc, nameof(eventTimeUtc));
        ValidateUtc(receivedAtUtc, nameof(receivedAtUtc));
        var normalizedEventType = Require(eventType, nameof(eventType));
        var normalizedStream = Require(stream, nameof(stream));
        var normalizedSymbol = Require(symbol, nameof(symbol)).ToUpperInvariant();
        ArgumentNullException.ThrowIfNull(payload);

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = checked(nextSequence++);
            var payloadElement = JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);
            var recordWithoutChecksum = new MarketDataRecordWire(
                MarketDataRecord.CurrentSchemaVersion,
                sequence,
                eventTimeUtc,
                receivedAtUtc,
                normalizedEventType,
                normalizedStream,
                normalizedSymbol,
                payloadElement,
                string.Empty);
            var checksum = ComputeChecksum(recordWithoutChecksum);
            var record = new MarketDataRecord(
                recordWithoutChecksum.SchemaVersion,
                recordWithoutChecksum.Sequence,
                recordWithoutChecksum.EventTimeUtc,
                recordWithoutChecksum.ReceivedAtUtc,
                recordWithoutChecksum.EventType,
                recordWithoutChecksum.Stream,
                recordWithoutChecksum.Symbol,
                recordWithoutChecksum.Payload,
                checksum);

            var line = Serialize(record);
            var path = GetFilePath(eventTimeUtc);
            using var streamWriter = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await streamWriter.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await streamWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// Reads and verifies records, then orders them by event time and sequence.
    /// The sequence tie-breaker makes equal timestamps deterministic.
    /// </summary>
    public async Task<MarketDataReplay> ReadReplayAsync(
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? throughUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (fromUtc is { } from)
        {
            ValidateUtc(from, nameof(fromUtc));
        }

        if (throughUtc is { } through)
        {
            ValidateUtc(through, nameof(throughUtc));
        }

        if (fromUtc is { } lower && throughUtc is { } upper && lower > upper)
        {
            throw new ArgumentException("Replay lower bound cannot exceed upper bound.");
        }

        var records = new List<MarketDataRecord>();
        foreach (var path in EnumerateJournalFiles())
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = ParseAndVerify(line, path);
                if (fromUtc is { } fromTime && record.EventTimeUtc < fromTime ||
                    throughUtc is { } throughTime && record.EventTimeUtc > throughTime)
                {
                    continue;
                }

                records.Add(record);
            }
        }

        var ordered = records
            .OrderBy(record => record.EventTimeUtc)
            .ThenBy(record => record.Sequence)
            .ToArray();
        ValidateUniqueSequences(ordered);
        var fingerprint = ComputeReplayChecksum(ordered);
        return new MarketDataReplay(ordered, fingerprint);
    }

    /// <summary>
    /// Deletes records older than the policy and then oldest files until the byte
    /// budget is satisfied. Callers must explicitly provide the policy.
    /// </summary>
    public Task<MarketDataRetentionResult> ApplyRetentionAsync(
        MarketDataRetentionPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateUtc(nowUtc, nameof(nowUtc));

        var files = EnumerateJournalFiles()
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var filesToDelete = files
            .Where(file => nowUtc.UtcDateTime - file.LastWriteTimeUtc > policy.MaximumAge)
            .ToList();
        var remaining = files.Except(filesToDelete).ToList();
        var remainingBytes = remaining.Sum(file => file.Length);
        foreach (var file in remaining)
        {
            if (remainingBytes <= policy.MaximumBytes)
            {
                break;
            }

            filesToDelete.Add(file);
            remainingBytes -= file.Length;
        }

        long bytesDeleted = 0;
        foreach (var file in filesToDelete.DistinctBy(file => file.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesDeleted = checked(bytesDeleted + file.Length);
            file.Delete();
        }

        return Task.FromResult(new MarketDataRetentionResult(filesToDelete.Count, bytesDeleted));
    }

    private static long FindNextSequence(string directory)
    {
        long maximum = -1;
        foreach (var path in EnumerateJournalFiles(directory))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = ParseAndVerify(line, path);
                maximum = Math.Max(maximum, record.Sequence);
            }
        }

        return checked(maximum + 1);
    }

    private static IEnumerable<string> EnumerateJournalFiles(string directory) =>
        Directory.EnumerateFiles(directory, $"{FilePrefix}*{FileSuffix}", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal);

    private IEnumerable<string> EnumerateJournalFiles() => EnumerateJournalFiles(directoryPath);

    private string GetFilePath(DateTimeOffset eventTimeUtc) =>
        Path.Combine(directoryPath, $"{FilePrefix}{eventTimeUtc:yyyyMMdd}{FileSuffix}");

    private static byte[] Serialize(MarketDataRecord record)
    {
        var json = JsonSerializer.Serialize(new MarketDataRecordWire(
            record.SchemaVersion,
            record.Sequence,
            record.EventTimeUtc,
            record.ReceivedAtUtc,
            record.EventType,
            record.Stream,
            record.Symbol,
            record.Payload,
            record.Checksum), JsonOptions);
        return Encoding.UTF8.GetBytes(json + Environment.NewLine);
    }

    private static MarketDataRecord ParseAndVerify(string line, string path)
    {
        MarketDataRecordWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<MarketDataRecordWire>(line, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid market-data JSONL in '{path}'.", exception);
        }

        if (wire is null || wire.SchemaVersion != MarketDataRecord.CurrentSchemaVersion || wire.Sequence < 0 ||
            wire.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            string.IsNullOrWhiteSpace(wire.EventType) || string.IsNullOrWhiteSpace(wire.Stream) ||
            string.IsNullOrWhiteSpace(wire.Symbol) || string.IsNullOrWhiteSpace(wire.Checksum))
        {
            throw new InvalidDataException($"Invalid market-data envelope in '{path}'.");
        }

        ValidateUtc(wire.EventTimeUtc, nameof(wire.EventTimeUtc));
        ValidateUtc(wire.ReceivedAtUtc, nameof(wire.ReceivedAtUtc));
        var expected = ComputeChecksum(wire with { Checksum = string.Empty });
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(wire.Checksum.ToUpperInvariant())))
        {
            throw new InvalidDataException($"Market-data checksum mismatch in '{path}' at sequence {wire.Sequence}.");
        }

        return new MarketDataRecord(
            wire.SchemaVersion,
            wire.Sequence,
            wire.EventTimeUtc,
            wire.ReceivedAtUtc,
            wire.EventType.Trim(),
            wire.Stream.Trim(),
            wire.Symbol.Trim().ToUpperInvariant(),
            wire.Payload,
            wire.Checksum.ToUpperInvariant());
    }

    private static string ComputeChecksum(MarketDataRecordWire wire)
    {
        var canonical = JsonSerializer.Serialize(wire with { Checksum = string.Empty }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeReplayChecksum(IReadOnlyList<MarketDataRecord> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in records)
        {
            hash.AppendData(Serialize(record));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateUniqueSequences(IEnumerable<MarketDataRecord> records)
    {
        long? previous = null;
        foreach (var record in records.OrderBy(record => record.Sequence))
        {
            if (previous == record.Sequence)
            {
                throw new InvalidDataException($"Duplicate market-data sequence {record.Sequence}.");
            }

            previous = record.Sequence;
        }
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    public void Dispose() => writeGate.Dispose();

    private static void ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", name);
        }
    }

    private sealed record MarketDataRecordWire(
        int SchemaVersion,
        long Sequence,
        DateTimeOffset EventTimeUtc,
        DateTimeOffset ReceivedAtUtc,
        string EventType,
        string Stream,
        string Symbol,
        JsonElement Payload,
        string Checksum);
}
