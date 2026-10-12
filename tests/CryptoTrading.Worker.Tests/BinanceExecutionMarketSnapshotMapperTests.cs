using CryptoTrading.Exchange.Binance;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class BinanceExecutionMarketSnapshotMapperTests
{
    [Fact]
    public void MapsBothSidesAndUsesConservativeLiquidityNotional()
    {
        var snapshot = BinanceExecutionMarketSnapshotMapper.Map(
            new BinanceBookTicker(
                "BTCUSDT",
                100m,
                2m,
                101m,
                1m,
                DateTimeOffset.UtcNow));

        Assert.Equal(100m, snapshot.BestBid.Value);
        Assert.Equal(101m, snapshot.BestAsk.Value);
        Assert.Equal(101m, snapshot.AvailableLiquidityNotional);
        Assert.Equal(2m, snapshot.BidQuantity);
        Assert.Equal(1m, snapshot.AskQuantity);
        Assert.Equal(0.3333m, snapshot.OrderBookImbalance);
    }
}
