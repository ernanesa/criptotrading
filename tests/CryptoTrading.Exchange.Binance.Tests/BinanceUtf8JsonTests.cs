using System.Globalization;
using System.Text.Json;
using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceUtf8JsonTests
{
    [Theory]
    [InlineData("\"0.00000001\"", "0.00000001")]
    [InlineData("\"0001.23000\"", "1.23")]
    [InlineData("123.4500", "123.45")]
    [InlineData("\"-12.50\"", "-12.5")]
    public void Utf8ScalarPreservesExactDecimalValues(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), BinanceUtf8Json.ReadDecimal(document.RootElement));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1,000\"")]
    [InlineData("\"1.00000000000000000000000000001\"")]
    [InlineData("\"79228162514264337593543950336\"")]
    [InlineData("\"1\\u002e0\"")]
    public void InvalidOrInexactScalarsAreNotCoercedToZeroOrRounded(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<FormatException>(() => BinanceUtf8Json.ReadDecimal(document.RootElement));
    }

    [Fact]
    public void ReadingExistingDomScalarDoesNotAllocateNumericStrings()
    {
        using var document = JsonDocument.Parse("\"1234.56789012\"");
        for (var i = 0; i < 32; i++) _ = BinanceUtf8Json.ReadDecimal(document.RootElement);
        var before = GC.GetAllocatedBytesForCurrentThread();
        decimal sum = 0m;
        for (var i = 0; i < 1000; i++) sum += BinanceUtf8Json.ReadDecimal(document.RootElement);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(sum);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("1.25")]
    [InlineData("\"1.25\"")]
    public void PresentKlineVolumeScalarsAreNotSilentlyConvertedToZero(string scalar)
    {
        var bytes = Encoding.UTF8.GetBytes(Kline(scalar));
        var parsed = Assert.IsType<BinanceKlineEvent>(BinanceMarketDataParser.TryParse((ReadOnlyMemory<byte>)bytes, DateTimeOffset.UnixEpoch));
        Assert.Equal(1.25m, parsed.QuoteVolume);
        Assert.Equal(1.25m, parsed.TakerBuyBaseVolume);
        Assert.Equal(1.25m, parsed.TakerBuyQuoteVolume);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1.00000000000000000000000000001")]
    public void InvalidPresentKlineVolumesRejectTheEventInsteadOfPretendingTheyAreAbsent(string scalar)
    {
        Assert.Null(BinanceMarketDataParser.TryParse((ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(Kline(scalar)), DateTimeOffset.UnixEpoch));
    }

    private static string Kline(string scalar) =>
        """{"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1000,"k":{"s":"BTCUSDT","t":0,"T":999,"i":"1m","o":"1","c":"1","h":"1","l":"1","v":"10","x":true,"q":SCALAR,"V":SCALAR,"Q":SCALAR}}} """.Replace("SCALAR", scalar, StringComparison.Ordinal);
}
