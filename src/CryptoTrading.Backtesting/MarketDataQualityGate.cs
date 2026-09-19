using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

/// <summary>
/// A closed bar plus the first UTC instant at which the complete bar was
/// available to the strategy. Keeping availability separate from event time
/// makes look-ahead contamination explicit at the replay boundary.
/// </summary>
public sealed record ClosedBarEnvelope(
    long Sequence,
    CloseBar Bar,
    DateTimeOffset AvailableAtUtc)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Sequence);
        ArgumentNullException.ThrowIfNull(Bar);
        if (AvailableAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Bar availability time must be UTC.", nameof(AvailableAtUtc));
        }

        if (Bar.CloseTimeUtc > AvailableAtUtc)
        {
            throw new InvalidDataException(
                $"Closed bar at {Bar.CloseTimeUtc:O} was used before availability at {AvailableAtUtc:O}.");
        }
    }
}

public sealed record MarketDataQualityResult(
    string DatasetId,
    string DatasetVersion,
    string DatasetContentHash,
    int BarCount,
    DateTimeOffset FirstBarUtc,
    DateTimeOffset LastBarUtc,
    string ReplayFingerprint);

/// <summary>
/// Fail-closed quality gate for externally supplied, already mapped close bars.
/// It does not fit parameters, select a strategy or contact an exchange.
/// </summary>
public static class MarketDataQualityGate
{
    public static MarketDataQualityResult ValidateClosedBars(
        DatasetMetadata dataset,
        IReadOnlyList<ClosedBarEnvelope> records,
        string expectedSymbol)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSymbol);

        var symbol = expectedSymbol.Trim().ToUpperInvariant();
        if (records.Count == 0)
        {
            throw new InvalidDataException("At least one closed bar is required.");
        }

        if (string.IsNullOrWhiteSpace(dataset.DatasetId) || string.IsNullOrWhiteSpace(dataset.Version) ||
            string.IsNullOrWhiteSpace(dataset.ContentHash))
        {
            throw new InvalidDataException("Dataset identity and content hash are required.");
        }

        long previousSequence = -1;
        DateTimeOffset? previousBarTime = null;
        foreach (var record in records)
        {
            record.Validate();
            if (record.Sequence <= previousSequence)
            {
                throw new InvalidDataException("Closed-bar sequences must be strictly increasing.");
            }

            if (!string.Equals(record.Bar.Symbol.Value, symbol, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Closed-bar symbols must match the expected dataset symbol.");
            }

            if (previousBarTime is { } prior && record.Bar.CloseTimeUtc <= prior)
            {
                throw new InvalidDataException("Closed-bar event times must be strictly increasing.");
            }

            previousSequence = record.Sequence;
            previousBarTime = record.Bar.CloseTimeUtc;
        }

        var canonical = string.Join(
            '\n',
            records.Select(record => string.Join(
                '|',
                record.Sequence.ToString(CultureInfo.InvariantCulture),
                record.Bar.Symbol.Value,
                record.Bar.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                record.Bar.ClosePrice.Value.ToString("G29", CultureInfo.InvariantCulture),
                record.AvailableAtUtc.ToString("O", CultureInfo.InvariantCulture))));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(
            dataset.DatasetId,
            dataset.Version,
            dataset.ContentHash,
            records.Count,
            records[0].Bar.CloseTimeUtc,
            records[^1].Bar.CloseTimeUtc,
            fingerprint);
    }
}
