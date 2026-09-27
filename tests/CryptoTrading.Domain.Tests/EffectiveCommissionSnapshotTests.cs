using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class EffectiveCommissionSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EffectiveRatePreservesSideAndMakerComponents()
    {
        var snapshot = Snapshot(Now);

        Assert.True(snapshot.TryGetEffectiveRate(OrderSide.Buy, makerEligible: true, Now, TimeSpan.FromMinutes(5), out var maker, out _));
        Assert.True(snapshot.TryGetEffectiveRate(OrderSide.Sell, makerEligible: false, Now, TimeSpan.FromMinutes(5), out var taker, out _));

        Assert.Equal(0.00154m, maker);
        Assert.Equal(0.00286m, taker);
    }

    [Fact]
    public void StaleSnapshotBlocksSizing()
    {
        var snapshot = Snapshot(Now.AddMinutes(-6));

        Assert.False(snapshot.TryGetEffectiveRate(OrderSide.Buy, true, Now, TimeSpan.FromMinutes(5), out _, out var reason));
        Assert.Equal("COMMISSION_SNAPSHOT_STALE", reason);
    }

    [Fact]
    public void EnabledBnbDiscountRequiresSafeValuation()
    {
        var snapshot = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .002m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            true, true, "BNB", .75m, false, Now);

        Assert.False(snapshot.TryGetEffectiveRate(OrderSide.Buy, true, Now, TimeSpan.FromMinutes(5), out _, out var reason));
        Assert.Equal("COMMISSION_BNB_VALUATION_UNAVAILABLE", reason);
    }

    [Fact]
    public void EnabledBnbDiscountUsesAttributedQuoteEvidence()
    {
        var snapshot = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .002m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            true, true, "BNB", .25m, true, Now,
            discountAssetQuotePrice: 600m,
            discountAssetValuationAtUtc: Now,
            discountAssetValuationSource: "binance:GET:/api/v3/ticker/bookTicker");

        Assert.True(snapshot.TryGetEffectiveRate(OrderSide.Buy, true, Now, TimeSpan.FromMinutes(5), out var rate, out _));
        Assert.Equal(.00075m, rate);
        Assert.Equal(600m, snapshot.DiscountAssetQuotePrice);
        Assert.NotEqual(Snapshot(Now).CanonicalPayloadHash, snapshot.CanonicalPayloadHash);
    }

    [Fact]
    public void ZeroDiscountRateWithNullAssetDoesNotRequireValuation()
    {
        var snapshot = new EffectiveCommissionSnapshot(
            new Symbol("BTCUSDT"), "testnet", "account",
            new CommissionRateSet(.001m, .002m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            new CommissionRateSet(0m, 0m, 0m, 0m),
            true, true, null, 0m, false, Now);

        Assert.True(snapshot.TryGetEffectiveRate(OrderSide.Buy, true, Now, TimeSpan.FromMinutes(5), out var rate, out _));
        Assert.Equal(.001m, rate);
    }

    private static EffectiveCommissionSnapshot Snapshot(DateTimeOffset observedAt) => new(
        new Symbol("BTCUSDT"), "testnet", "account",
        new CommissionRateSet(.001m, .002m, .0001m, .0002m),
        new CommissionRateSet(.0001m, .0002m, .0003m, .0004m),
        new CommissionRateSet(.00001m, .00002m, .00003m, .00004m),
        false, false, null, null, false, observedAt);
}
