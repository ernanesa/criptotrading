using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AccountStreamExecutionAccountingTests
{
    [Fact]
    public void TradeReportProducesDeterministicAccountingCommand()
    {
        var source = Event();
        var first = AccountStreamExecutionAccounting.ToCommand(source, new AccountId("exchange-wallet"));
        var second = AccountStreamExecutionAccounting.ToCommand(source, new AccountId("exchange-wallet"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(OrderSide.Buy, first!.Side);
        Assert.Equal(new Asset("BTC"), first.BaseAsset);
        Assert.Equal(new Asset("USDT"), first.QuoteAsset);
        Assert.Equal(new Money(0.001m, new Asset("USDT")), first.Commission);
    }

    [Fact]
    public void NonTradeLifecycleReportDoesNotCreateAccountingFact()
    {
        var command = AccountStreamExecutionAccounting.ToCommand(
            Event() with { ExecutionType = "NEW", LastExecutedQuantity = 0m },
            new AccountId("exchange-wallet"));

        Assert.Null(command);
    }

    private static BinanceExecutionReportEvent Event() => new(
        DateTimeOffset.UtcNow,
        1_700_000_000_123,
        "BTCUSDT",
        42,
        7,
        "client-42",
        "BUY",
        "LIMIT",
        "TRADE",
        "FILLED",
        0.01m,
        0.01m,
        100.50m,
        0.001m,
        "USDT");
}
