using CryptoTrading.Domain.Arbitrage;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ArbitrageTests
{
    private static readonly Symbol EthUsdt = new("ETHUSDT");
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void StatisticalArbitrageEngineGeneratesSignalsAndDetectsStopLossDivergence()
    {
        // 15 historical spread values around mean 0.5 with std dev ~0.02
        var history = new List<decimal>
        {
            0.50m, 0.49m, 0.51m, 0.50m, 0.48m,
            0.52m, 0.50m, 0.49m, 0.51m, 0.50m,
            0.50m, 0.49m, 0.51m, 0.50m, 0.50m
        };

        // Extreme spread divergence -> StopLossExit
        var stopLossSnapshot = StatisticalArbitrageEngine.EvaluatePairSpread(
            EthUsdt,
            BtcUsdt,
            priceA: 4000m,
            priceB: 50000m,
            hedgeRatioBeta: 0.8m,
            historicalSpreads: history);

        Assert.Equal(PairSignal.StopLossExit, stopLossSnapshot.Signal);
        Assert.Contains("STOP_LOSS", stopLossSnapshot.Reason, StringComparison.Ordinal);

        // Underpriced spread -> LongSpread
        var lowSpreadHistory = new List<decimal>();
        for (var i = 0; i < 20; i++)
        {
            lowSpreadHistory.Add(0.05m + (i * 0.001m));
        }

        var longSpreadSnapshot = StatisticalArbitrageEngine.EvaluatePairSpread(
            EthUsdt,
            BtcUsdt,
            priceA: 2500m,
            priceB: 65000m,
            hedgeRatioBeta: 0.705m,
            historicalSpreads: lowSpreadHistory,
            entryZThreshold: 1.0m);

        Assert.True(longSpreadSnapshot.Signal is PairSignal.LongSpread or PairSignal.StopLossExit);
    }

    [Fact]
    public void TriangularArbitrageDetectorIdentifiesDislocationsAndDeductsTakerFees()
    {
        // Dislocated price scenario where gross multiplier is high enough to overcome 3x 10bps fees
        // USDT -> BTC ($60,000) -> ETH (0.05 BTC = $3,000 equivalent) -> USDT (sell ETH for $3,150)
        // Gross: (1/60000) * (1/0.05) * 3150 = (1/60000) * 20 * 3150 = 63000 / 60000 = 1.05 (+5%)
        var opp = TriangularArbitrageDetector.EvaluateCycle(
            usdtToBtcAskPrice: 60_000m,
            ethToBtcAskPrice: 0.05m,
            ethToUsdtBidPrice: 3_150m,
            takerFeeRate: 0.001m); // 10 bps per leg

        Assert.True(opp.IsProfitable);
        Assert.True(opp.NetProfitBps > 400.0m); // ~467 bps profit
        Assert.Contains("ARBITRAGE_FOUND", opp.Reason, StringComparison.Ordinal);

        // Fair pricing scenario -> after fees it should be unprofitable
        var fairOpp = TriangularArbitrageDetector.EvaluateCycle(
            usdtToBtcAskPrice: 60_000m,
            ethToBtcAskPrice: 0.05m,
            ethToUsdtBidPrice: 3_000m, // exact theoretical cross
            takerFeeRate: 0.001m);

        Assert.False(fairOpp.IsProfitable);
        Assert.True(fairOpp.NetProfitBps < 0m); // Net negative due to 30 bps fees
        Assert.Contains("UNPROFITABLE", fairOpp.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossExchangeMarketMakerCalculatesProfitableTwoSidedQuotes()
    {
        // Hedge venue (Binance) has Bid: $60,000, Ask: $60,020
        var quote = CrossExchangeMarketMaker.CalculateQuotes(
            hedgeVenueBidPrice: 60_000m,
            hedgeVenueAskPrice: 60_020m,
            takerFeeRate: 0.001m, // 10 bps
            makerFeeRate: 0.0005m, // 5 bps
            minSpreadHurdleBps: 15m); // total requirement: 30 bps = 0.3%

        Assert.True(quote.IsViable);
        // Maker Bid should be below hedge venue bid ($60,000 * (1 - 0.003) = 59,820)
        Assert.True(quote.MakerBidPrice < 60_000m);
        // Maker Ask should be above hedge venue ask ($60,020 * (1 + 0.003) = 60,200)
        Assert.True(quote.MakerAskPrice > 60_020m);
        Assert.True(quote.ExpectedSpreadBps >= 30m);
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("1")]
    [InlineData("1.001")]
    public void TriangularArbitrageRejectsInvalidFees(string feeText)
    {
        var fee = decimal.Parse(feeText, System.Globalization.CultureInfo.InvariantCulture);
        var result = TriangularArbitrageDetector.EvaluateCycle(60_000m, 0.05m, 3_150m, fee);
        Assert.False(result.IsProfitable);
        Assert.StartsWith("INVALID_FEES", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TriangularArbitrageRequiresBuyingEthAtAskAndReportsOnlyTheoreticalProfit()
    {
        var atAsk = TriangularArbitrageDetector.EvaluateCycle(60_000m, 0.05m, 3_000m, 0m);
        var understatedBuyPrice = TriangularArbitrageDetector.EvaluateCycle(60_000m, 0.049m, 3_000m, 0m);
        Assert.Equal(1m, atAsk.GrossRateMultiplier);
        Assert.False(atAsk.IsProfitable);
        Assert.True(understatedBuyPrice.IsProfitable);
        Assert.Contains("THEORETICAL", understatedBuyPrice.Reason, StringComparison.Ordinal);
    }
}
