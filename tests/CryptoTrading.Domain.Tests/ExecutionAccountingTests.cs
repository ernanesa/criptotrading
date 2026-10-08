using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExecutionAccountingTests
{
    [Fact]
    public void NegativeQuoteCommissionIsRecordedAsVenueRebate()
    {
        var command = Command(OrderSide.Buy, new Money(-.1m, Usdt));

        var entry = ExecutionAccounting.CreateLedgerEntry(command);
        var update = ExecutionAccounting.ApplyAverageCost(command, 0m, 0m);

        Assert.Contains(entry.Postings, posting => posting.Account == Wallet && posting.Amount == new Money(.1m, Usdt));
        Assert.Contains(entry.Postings, posting => posting.Account == Fees && posting.Amount == new Money(-.1m, Usdt));
        Assert.Equal(99.9m, update.CostQuote);
    }

    private static readonly Asset Btc = new("BTC");
    private static readonly Asset Usdt = new("USDT");
    private static readonly AccountId Wallet = new("exchange-wallet");
    private static readonly AccountId Fees = new("trading-fees-expense");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuyWithQuoteFeeProducesBalancedBaseAndQuotePostings()
    {
        var entry = ExecutionAccounting.CreateLedgerEntry(Command(OrderSide.Buy, new Money(1m, Usdt)));

        Assert.Equal(0m, entry.Postings.Where(x => x.Amount.Asset == Usdt).Sum(x => x.Amount.Amount));
        Assert.Equal(0m, entry.Postings.Where(x => x.Amount.Asset == Btc).Sum(x => x.Amount.Amount));
        Assert.Equal(6, entry.Postings.Count);
    }

    [Fact]
    public void SellUsesAverageCostAndQuoteFeeForRealizedPnl()
    {
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(1m, Usdt), price: 120m),
            currentQuantity: 2m,
            currentCostQuote: 200m);

        Assert.Equal(1m, result.Quantity);
        Assert.Equal(100m, result.CostQuote);
        Assert.Equal(19m, result.RealizedPnlQuote);
    }

    [Fact]
    public void SellWithBaseFeeChargesAllDisposedInventoryToRealizedPnl()
    {
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(.01m, Btc), price: 120m), 2m, 200m);
        Assert.Equal(.99m, result.Quantity);
        Assert.Equal(99m, result.CostQuote);
        Assert.Equal(19m, result.RealizedPnlQuote);
    }

    [Fact]
    public void BuyWithValuedThirdAssetFeeAddsFeeToQuoteCostBasis()
    {
        var valuation = FeeValuation(new Asset("BNB"), 2m, Now);
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Buy, new Money(.01m, new Asset("BNB")), feeAssetValuation: valuation), 0m, 0m);

        Assert.Equal(1m, result.Quantity);
        Assert.Equal(100.02m, result.CostQuote);
        Assert.Null(result.RealizedPnlQuote);
    }

    [Fact]
    public void SellWithValuedThirdAssetFeeReducesRealizedPnl()
    {
        var valuation = FeeValuation(new Asset("BNB"), 2m, Now);
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(.01m, new Asset("BNB")), price: 120m, feeAssetValuation: valuation), 2m, 200m);

        Assert.Equal(19.98m, result.RealizedPnlQuote);
    }

    [Fact]
    public void NegativeThirdAssetCommissionIsRecordedAsQuoteRebate()
    {
        var valuation = FeeValuation(new Asset("BNB"), 2m, Now);
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(-.01m, new Asset("BNB")), price: 120m, feeAssetValuation: valuation), 2m, 200m);

        Assert.Equal(20.02m, result.RealizedPnlQuote);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("wrong-asset")]
    [InlineData("wrong-quote")]
    public void ThirdAssetCommissionWithoutMatchingFreshValuationFailsClosed(string valuationCase)
    {
        var bnb = new Asset("BNB");
        var valuation = valuationCase switch
        {
            "missing" => null,
            "future" => FeeValuation(bnb, 2m, Now.AddTicks(1)),
            "stale" => FeeValuation(bnb, 2m, Now.AddMinutes(-2).AddTicks(-1)),
            "wrong-asset" => FeeValuation(new Asset("ETH"), 2m, Now),
            "wrong-quote" => new ExecutionFeeAssetValuation(bnb, new Asset("USDC"), 2m, Now, "test"),
            _ => throw new ArgumentOutOfRangeException(nameof(valuationCase))
        };

        Assert.Throws<InvalidOperationException>(() => ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Buy, new Money(.01m, bnb), feeAssetValuation: valuation), 0m, 0m));
    }

    [Fact]
    public void ZeroThirdAssetCommissionDoesNotRequireValuation()
    {
        var result = ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Buy, new Money(0m, new Asset("BNB"))), 0m, 0m);

        Assert.Equal(100m, result.CostQuote);
    }

    [Fact]
    public void SellBeforeInventoryFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(0m, Usdt)), 0m, 0m));
    }

    private static ExecutionFeeAssetValuation FeeValuation(Asset asset, decimal quotePerAsset, DateTimeOffset observedAtUtc) =>
        new(asset, Usdt, quotePerAsset, observedAtUtc, "test:fee-valuation");

    private static ExecutionAccountingCommand Command(
        OrderSide side,
        Money commission,
        decimal price = 100m,
        ExecutionFeeAssetValuation? feeAssetValuation = null) => new(
        "exec-1", 42, 7, new Symbol("BTCUSDT"), Btc, Usdt, side,
        new Quantity(1m), new Price(price), commission, Wallet, Fees,
        new LedgerEntryId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new CorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new IdempotencyKey("exec-1"), Now, feeAssetValuation: feeAssetValuation);
}
