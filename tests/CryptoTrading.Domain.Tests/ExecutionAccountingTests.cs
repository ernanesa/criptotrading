using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExecutionAccountingTests
{
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
    public void SellBeforeInventoryFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ExecutionAccounting.ApplyAverageCost(
            Command(OrderSide.Sell, new Money(0m, Usdt)), 0m, 0m));
    }

    private static ExecutionAccountingCommand Command(OrderSide side, Money commission, decimal price = 100m) => new(
        "exec-1", 42, 7, new Symbol("BTCUSDT"), Btc, Usdt, side,
        new Quantity(1m), new Price(price), commission, Wallet, Fees,
        new LedgerEntryId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new CorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new IdempotencyKey("exec-1"), Now);
}
