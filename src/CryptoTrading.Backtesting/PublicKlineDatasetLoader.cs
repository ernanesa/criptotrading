using System.Globalization;
using System.Text.Json;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting;

public sealed record LoadedKlineDataset(
    DatasetMetadata Metadata,
    IReadOnlyList<ClosedBarEnvelope> Records,
    MarketDataQualityResult Quality);

/// <summary>Maps the public kline journal into quality-gated close bars for research.</summary>
public static class PublicKlineDatasetLoader
{
    public static LoadedKlineDataset Load(
        MarketDataReplay replay,
        string datasetId,
        string version,
        string source,
        string expectedSymbol,
        DateTimeOffset ingestedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSymbol);
        if (ingestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Dataset ingestion time must be UTC.", nameof(ingestedAtUtc));
        }

        var symbol = expectedSymbol.Trim().ToUpperInvariant();
        var records = replay.Records
            .Where(record => string.Equals(record.EventType, "kline-rest", StringComparison.Ordinal))
            .Select(record => Map(record, symbol))
            .ToArray();
        if (records.Length == 0)
        {
            throw new InvalidDataException("Replay contains no public kline records.");
        }

        var quality = MarketDataQualityGate.ValidateClosedBars(
            new DatasetMetadata(datasetId, version, source, "public-klines/v1", replay.DatasetChecksum, ingestedAtUtc, "UTC"),
            records,
            symbol);
        return new(
            new DatasetMetadata(datasetId, version, source, "public-klines/v1", replay.DatasetChecksum, ingestedAtUtc, "UTC"),
            records,
            quality);
    }

    private static ClosedBarEnvelope Map(MarketDataRecord record, string expectedSymbol)
    {
        if (!string.Equals(record.Symbol, expectedSymbol, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Kline journal symbol does not match the requested dataset symbol.");
        }

        var payload = record.Payload;
        var closeTime = ReadTimestamp(payload, "closeTimeUtc");
        if (closeTime != record.EventTimeUtc)
        {
            throw new InvalidDataException("Kline event time must equal the payload close time.");
        }

        var close = ReadDecimal(payload, "close");
        return new ClosedBarEnvelope(
            record.Sequence,
            new CloseBar(new Symbol(expectedSymbol), closeTime, close),
            record.ReceivedAtUtc);
    }

    private static DateTimeOffset ReadTimestamp(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || !value.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Kline payload field '{name}' must be a UTC timestamp.");
        }

        return timestamp;
    }

    private static decimal ReadDecimal(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"Kline payload field '{name}' is missing.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text))
        {
            return text;
        }

        throw new InvalidDataException($"Kline payload field '{name}' must be a decimal.");
    }
}
