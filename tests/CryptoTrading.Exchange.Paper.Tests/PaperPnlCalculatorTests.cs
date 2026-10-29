using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Exchange.Paper.Tests;

public sealed class PaperPnlCalculatorTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly Asset Usdt = new("USDT");

    [Fact]
    public void RoundTripRealizesNotionalMinusCostAndBothFees()
    {
        var fills = new[]
        {
            Fill(1, OrderSide.Buy, 1m, 100m, 0.1m),
            Fill(2, OrderSide.Sell, 1m, 110m, 0.11m)
        };

        var summary = PaperPnlCalculator.Calculate(fills);

        // 110 - 100 - 0.1 (buy fee, capitalised) - 0.11 (sell fee)
        Assert.Equal(9.79m, summary.RealizedPnlQuote);
        Assert.Equal(0.21m, summary.FeesPaidQuote);
        Assert.Equal(0m, summary.OpenQuantity);
        Assert.Equal(0m, summary.OpenCostQuote);
        Assert.Equal(1, summary.ClosedFillCount);
    }

    [Fact]
    public void OpenPositionKeepsCostBasisAndNoRealizedPnl()
    {
        var summary = PaperPnlCalculator.Calculate([Fill(1, OrderSide.Buy, 2m, 50m, 0.1m)]);

        Assert.Equal(0m, summary.RealizedPnlQuote);
        Assert.Equal(2m, summary.OpenQuantity);
        Assert.Equal(100.1m, summary.OpenCostQuote);
        Assert.Equal(0, summary.ClosedFillCount);
    }

    [Fact]
    public void PartialExitRealizesProportionallyAndLeavesCostBasis()
    {
        var fills = new[]
        {
            Fill(1, OrderSide.Buy, 2m, 100m, 0m),
            Fill(2, OrderSide.Sell, 1m, 120m, 0m)
        };

        var summary = PaperPnlCalculator.Calculate(fills);

        Assert.Equal(20m, summary.RealizedPnlQuote);
        Assert.Equal(1m, summary.OpenQuantity);
        Assert.Equal(100m, summary.OpenCostQuote);
    }

    [Fact]
    public void SellWithoutInventoryFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PaperPnlCalculator.Calculate([Fill(1, OrderSide.Sell, 1m, 100m, 0m)]));
    }

    [Fact]
    public void ThirdAssetFeeIsRejectedInsteadOfBeingIgnored()
    {
        var fill = Fill(1, OrderSide.Buy, 1m, 100m, 0m) with { Fee = new Money(0.001m, new Asset("BNB")) };

        Assert.Throws<InvalidOperationException>(() => PaperPnlCalculator.Calculate([fill]));
    }

    [Fact]
    public void FillsAreOrderedBySequenceRegardlessOfInputOrder()
    {
        var fills = new[]
        {
            Fill(2, OrderSide.Sell, 1m, 110m, 0m),
            Fill(1, OrderSide.Buy, 1m, 100m, 0m)
        };

        var summary = PaperPnlCalculator.Calculate(fills);

        Assert.Equal(10m, summary.RealizedPnlQuote);
        Assert.Equal(0m, summary.OpenQuantity);
    }

    [Fact]
    public void DifferentSymbolsCannotBeCombinedIntoOnePaperPosition()
    {
        var buy = Fill(1, OrderSide.Buy, 1m, 100m, 0m);
        var sell = Fill(2, OrderSide.Sell, 1m, 110m, 0m) with { Symbol = new Symbol("ETHUSDT") };

        Assert.Throws<InvalidOperationException>(() => PaperPnlCalculator.Calculate([buy, sell]));
    }

    [Fact]
    public void DuplicateFillSequenceCannotBeCountedTwice()
    {
        var fill = Fill(1, OrderSide.Buy, 1m, 100m, 0m);

        Assert.Throws<InvalidOperationException>(() => PaperPnlCalculator.Calculate([fill, fill]));
    }

    [Fact]
    public void IncompleteSequenceCannotBeReportedAsCompletePaperPnl()
    {
        var first = Fill(1, OrderSide.Buy, 1m, 100m, 0m);
        var third = Fill(3, OrderSide.Sell, 1m, 110m, 0m);

        Assert.Throws<InvalidOperationException>(() => PaperPnlCalculator.Calculate([first, third]));
        Assert.Throws<InvalidOperationException>(() => PaperPnlCalculator.Calculate([Fill(2, OrderSide.Buy, 1m, 100m, 0m)]));
    }

    private static PaperFill Fill(long sequence, OrderSide side, decimal quantity, decimal price, decimal fee) =>
        new(sequence, OrderId.New(), ClientOrderId.New(), BtcUsdt, side,
            new Quantity(quantity), new Price(price), new Money(fee, Usdt), 0m,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));
}
