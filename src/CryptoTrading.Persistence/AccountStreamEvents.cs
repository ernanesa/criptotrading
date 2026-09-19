using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Persistence;

/// <summary>Validated, redaction-safe envelope for a Binance account-stream event.</summary>
public sealed record AccountStreamEventRecord
{
    public AccountStreamEventRecord(
        Guid eventId,
        string eventType,
        string dedupeKey,
        long eventTimeMilliseconds,
        DateTimeOffset receivedAtUtc,
        string payloadJson,
        string? symbol = null)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Account stream event IDs cannot be empty.", nameof(eventId));
        }

        EventType = ValidateText(eventType, nameof(eventType), 64);
        DedupeKey = ValidateText(dedupeKey, nameof(dedupeKey), 200);
        ArgumentOutOfRangeException.ThrowIfNegative(eventTimeMilliseconds);

        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Account stream timestamps must be UTC.", nameof(receivedAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Account stream payload must be a JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Account stream payload must be valid JSON.", nameof(payloadJson), exception);
        }

        EventId = eventId;
        EventTimeMilliseconds = eventTimeMilliseconds;
        ReceivedAtUtc = receivedAtUtc;
        PayloadJson = CanonicalizeJson(payloadJson);
        PayloadSha256 = ComputeHash(PayloadJson);
        Symbol = symbol is null ? null : NormalizeSymbol(symbol);
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public string DedupeKey { get; }
    public long EventTimeMilliseconds { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public string PayloadJson { get; }
    public string PayloadSha256 { get; }
    public string? Symbol { get; }

    public static string ComputeHash(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeJson(payloadJson))));
    }

    private static string CanonicalizeJson(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string ValidateText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = ValidateText(symbol, nameof(symbol), 20).ToUpperInvariant();
        if (normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Symbols must contain only ASCII letters and digits.", nameof(symbol));
        }

        return normalized;
    }
}
