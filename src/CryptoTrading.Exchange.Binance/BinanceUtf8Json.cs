using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

internal static class BinanceUtf8Json
{
    public static decimal ReadDecimal(JsonElement element)
    {
        var reader = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(element));
        if (!reader.Read() || reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number) || reader.ValueIsEscaped)
            throw new FormatException("Expected an unescaped invariant decimal scalar.");
        var text = reader.ValueSpan;
        if (!ValidPlainNumber(text) || !Utf8Parser.TryParse(text, out decimal value, out var consumed) || consumed != text.Length)
            throw new FormatException("Invalid or overflowing decimal scalar.");
        Span<byte> formatted = stackalloc byte[64];
        if (!Utf8Formatter.TryFormat(value, formatted, out var written) ||
            !Magnitude(text).SequenceEqual(Magnitude(formatted[..written])) || reader.Read())
            throw new FormatException("Decimal scalar cannot be represented exactly.");
        return value;
    }

    private static bool ValidPlainNumber(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return false;
        if (text[0] == '-') text = text[1..];
        if (text.IsEmpty || text[0] == '.' || text[^1] == '.') return false;
        var dot = false;
        foreach (var value in text)
        {
            if (value == '.' && !dot) { dot = true; continue; }
            if (value is < (byte)'0' or > (byte)'9') return false;
        }
        return true;
    }

    private static ReadOnlySpan<byte> Magnitude(ReadOnlySpan<byte> text)
    {
        if (text[0] == '-') text = text[1..];
        while (text.Length > 1 && text[0] == '0' && text[1] != '.') text = text[1..];
        if (text.Contains((byte)'.'))
        {
            while (text[^1] == '0') text = text[..^1];
            if (text[^1] == '.') text = text[..^1];
        }
        return text;
    }
}
