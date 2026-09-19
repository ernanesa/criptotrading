using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Backtesting;

/// <summary>Immutable identity and lineage metadata for a replay input.</summary>
public sealed record DatasetMetadata
{
    public DatasetMetadata(
        string datasetId,
        string version,
        string source,
        string schemaVersion,
        string contentHash,
        DateTimeOffset ingestedAtUtc,
        string timeZoneId)
    {
        DatasetId = Require(datasetId, nameof(datasetId));
        Version = Require(version, nameof(version));
        Source = Require(source, nameof(source));
        SchemaVersion = Require(schemaVersion, nameof(schemaVersion));
        ContentHash = NormalizeSha256(contentHash);
        if (ingestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Dataset ingestion time must be UTC.", nameof(ingestedAtUtc));
        }

        IngestedAtUtc = ingestedAtUtc;
        TimeZoneId = Require(timeZoneId, nameof(timeZoneId));
    }

    public string DatasetId { get; }
    public string Version { get; }
    public string Source { get; }
    public string SchemaVersion { get; }
    public string ContentHash { get; }
    public DateTimeOffset IngestedAtUtc { get; }
    public string TimeZoneId { get; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static string NormalizeSha256(string value)
    {
        var normalized = Require(value, nameof(value)).ToUpperInvariant();
        try
        {
            if (Convert.FromHexString(normalized).Length != SHA256.HashSizeInBytes)
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException("ContentHash must be a SHA-256 hexadecimal string.", nameof(value));
        }

        return normalized;
    }
}

/// <summary>All non-market inputs that affect a replay result.</summary>
public sealed record ReplayConfiguration
{
    public ReplayConfiguration(long seed, string timeZoneId, string engineVersion = "1")
    {
        Seed = seed;
        TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId)
            ? throw new ArgumentException("Time zone is required.", nameof(timeZoneId))
            : timeZoneId.Trim();
        EngineVersion = string.IsNullOrWhiteSpace(engineVersion)
            ? throw new ArgumentException("Engine version is required.", nameof(engineVersion))
            : engineVersion.Trim();
    }

    public long Seed { get; }
    public string TimeZoneId { get; }
    public string EngineVersion { get; }
}

/// <summary>Transport-neutral event envelope used by controlled replay.</summary>
public sealed record ReplayEventEnvelope<TPayload>
{
    public ReplayEventEnvelope(long sequence, DateTimeOffset eventTimeUtc, string eventType, TPayload payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (eventTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Event time must be UTC.", nameof(eventTimeUtc));
        }

        Sequence = sequence;
        EventTimeUtc = eventTimeUtc;
        EventType = string.IsNullOrWhiteSpace(eventType)
            ? throw new ArgumentException("Event type is required.", nameof(eventType))
            : eventType.Trim();
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public long Sequence { get; }
    public DateTimeOffset EventTimeUtc { get; }
    public string EventType { get; }
    public TPayload Payload { get; }
}

public sealed record ReplayRunRequest<TPayload>
{
    public ReplayRunRequest(
        DatasetMetadata dataset,
        ReplayConfiguration configuration,
        IReadOnlyList<ReplayEventEnvelope<TPayload>> events)
    {
        Dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public DatasetMetadata Dataset { get; }
    public ReplayConfiguration Configuration { get; }
    public IReadOnlyList<ReplayEventEnvelope<TPayload>> Events { get; }
}

public sealed record ReplayRunResult(
    string DatasetId,
    string DatasetHash,
    long Seed,
    string TimeZoneId,
    int EventCount,
    string Fingerprint);

public interface IReplayRunner<TPayload>
{
    ReplayRunResult Run(ReplayRunRequest<TPayload> request);
}

/// <summary>
/// Validates ordering and produces a deterministic run fingerprint.
/// Strategy, portfolio, risk and execution logic are deliberately outside this primitive.
/// </summary>
public sealed class DeterministicReplayRunner<TPayload> : IReplayRunner<TPayload>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ReplayRunResult Run(ReplayRunRequest<TPayload> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOrder(request.Events);

        var canonical = JsonSerializer.Serialize(new
        {
            dataset = request.Dataset,
            configuration = request.Configuration,
            events = request.Events
        }, JsonOptions);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return new ReplayRunResult(
            request.Dataset.DatasetId,
            request.Dataset.ContentHash,
            request.Configuration.Seed,
            request.Configuration.TimeZoneId,
            request.Events.Count,
            fingerprint);
    }

    private static void ValidateOrder(IReadOnlyList<ReplayEventEnvelope<TPayload>> events)
    {
        long previousSequence = -1;
        DateTimeOffset previousTime = DateTimeOffset.MinValue;
        foreach (var replayEvent in events)
        {
            if (replayEvent.Sequence <= previousSequence)
            {
                throw new InvalidOperationException("Replay events must have strictly increasing sequence numbers.");
            }

            if (replayEvent.EventTimeUtc < previousTime)
            {
                throw new InvalidOperationException("Replay events must be ordered by event time.");
            }

            previousSequence = replayEvent.Sequence;
            previousTime = replayEvent.EventTimeUtc;
        }
    }
}
