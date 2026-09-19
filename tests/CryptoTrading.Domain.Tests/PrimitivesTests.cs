using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class PrimitivesTests
{
    [Fact]
    public void AssetAndSymbolNormalizeToUppercase()
    {
        Assert.Equal("BTC", new Asset(" btc ").Value);
        Assert.Equal("BTCUSDT", new Symbol("btcusdt").Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void PriceRejectsNonPositiveValues(decimal value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Price(value));
    }

    [Fact]
    public void MoneyRejectsArithmeticAcrossDifferentAssets()
    {
        var btc = new Money(1m, new Asset("BTC"));
        var usdt = new Money(1m, new Asset("USDT"));

        Assert.Throws<InvalidOperationException>(() => btc + usdt);
    }

    [Fact]
    public void QuantityRejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(-0.000001m));
    }

    [Fact]
    public void RiskAmountRejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskAmount(-1m, new Asset("USDT")));
    }

    [Fact]
    public void ClientOrderIdRejectsUnsafeCharactersAndExcessiveLength()
    {
        Assert.Throws<ArgumentException>(() => new ClientOrderId("order id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientOrderId(new string('x', ClientOrderId.MaxLength + 1)));
    }

    [Fact]
    public void GeneratedIdentifiersAreUniqueAndRoundTrip()
    {
        var first = CorrelationId.New();
        var second = CorrelationId.New();

        Assert.NotEqual(first, second);
        Assert.Equal(first, CorrelationId.Parse(first.ToString()));
        Assert.True(CorrelationId.TryParse(first.ToString(), out var parsed));
        Assert.Equal(first, parsed);
    }

    [Fact]
    public void TradingClockUsesInjectedTimeProvider()
    {
        var now = new DateTimeOffset(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);
        var clock = new TradingClock(new FixedTimeProvider(now));

        Assert.Equal(now, clock.UtcNow);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
