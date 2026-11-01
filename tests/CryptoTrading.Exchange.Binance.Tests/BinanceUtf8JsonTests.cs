using System.Globalization;
using System.Text.Json;
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
}
