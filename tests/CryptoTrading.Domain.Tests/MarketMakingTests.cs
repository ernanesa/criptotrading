using System.Globalization;
using CryptoTrading.Domain.MarketMaking;
using CryptoTrading.Domain.Orders;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class MarketMakingTests
{
    [Fact]
    public void OrderBookQueueSimulatorEstimatesQueueDepthAndFillProbability()
    {
        // 10 BTC level depth, 4 BTC executed since placement, our order size is 1 BTC
        // Total volume ahead was 9 BTC. After 4 BTC executed, remaining ahead is 5 BTC.
        var estimate = OrderBookQueueSimulator.EstimateQueuePosition(
            OrderSide.Buy,
            price: 64_000m,
            totalLevelVolume: 10m,
            executedVolumeSincePlacement: 4m,
            myOrderQuantity: 1m);

        Assert.Equal(5m, estimate.QuantityAhead);
        Assert.False(estimate.IsAtFrontOfQueue);
        Assert.Equal(0.4m, estimate.EstimatedFillProbability);

        // When executed volume exceeds initial volume ahead (e.g. 9.5 BTC executed)
        var frontEstimate = OrderBookQueueSimulator.EstimateQueuePosition(
            OrderSide.Buy,
            price: 64_000m,
            totalLevelVolume: 10m,
            executedVolumeSincePlacement: 9.5m,
            myOrderQuantity: 1m);

        Assert.Equal(0m, frontEstimate.QuantityAhead);
        Assert.True(frontEstimate.IsAtFrontOfQueue);
    }

    [Fact]
    public void AvellanedaStoikovModelCalculatesReservationPriceAndAsymmetricQuotes()
    {
        // Zero inventory scenario -> reservation price equals mid price
        var neutralQuotes = AvellanedaStoikovModel.ComputeQuotes(
            midPrice: 60_000m,
            currentInventoryQuantity: 0m,
            volatilitySigma: 100m,
            riskAversionGamma: 0.01m);

        Assert.Equal(60_000m, neutralQuotes.ReservationPrice);
        Assert.Equal(0m, neutralQuotes.InventorySkew);
        Assert.True(neutralQuotes.OptimalAskPrice > neutralQuotes.OptimalBidPrice);

        // Long inventory scenario (q = 5.0) -> reservation price skews downward to unload
        var longQuotes = AvellanedaStoikovModel.ComputeQuotes(
            midPrice: 60_000m,
            currentInventoryQuantity: 5.0m,
            volatilitySigma: 100m,
            riskAversionGamma: 0.01m);

        Assert.True(longQuotes.ReservationPrice < 60_000m);
        Assert.True(longQuotes.InventorySkew > 0m);
        // Both bid and ask are lower than neutral quotes
        Assert.True(longQuotes.OptimalBidPrice < neutralQuotes.OptimalBidPrice);
        Assert.True(longQuotes.OptimalAskPrice < neutralQuotes.OptimalAskPrice);
    }

    [Fact]
    public void InventoryAwareMarketMakerEnforcesHardLimitsAndSkewsOrderSizes()
    {
        // 1. Balanced inventory (q = 0.5 with max 2.0) -> both sides active, sizes slightly tilted
        var balanced = InventoryAwareMarketMaker.SizeQuotes(
            baseBidPrice: 59_990m,
            baseAskPrice: 60_010m,
            baseOrderQuantity: 0.10m,
            currentInventory: 0.5m,
            maxAllowedInventory: 2.0m);

        Assert.True(balanced.AllowBid);
        Assert.True(balanced.AllowAsk);
        // Asks should be larger than bids to offload 0.5 long
        Assert.True(balanced.AskQuantity > balanced.BidQuantity);

        // 2. Max long breach (q = 2.0 with max 2.0) -> Bids prohibited
        var maxLongBreach = InventoryAwareMarketMaker.SizeQuotes(
            baseBidPrice: 59_990m,
            baseAskPrice: 60_010m,
            baseOrderQuantity: 0.10m,
            currentInventory: 2.0m,
            maxAllowedInventory: 2.0m);

        Assert.False(maxLongBreach.AllowBid);
        Assert.True(maxLongBreach.AllowAsk);
        Assert.Equal(0m, maxLongBreach.BidQuantity);
        Assert.Contains("MAX_LONG_INVENTORY_BREACH", maxLongBreach.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AdverseSelectionDetectorIdentifiesToxicFlowViaPriceMarkouts()
    {
        var now = DateTimeOffset.Parse("2026-09-21T20:00:00Z", CultureInfo.InvariantCulture);

        // Generate 10 fills where 8 of them were adversely selected (price dropped after Buy)
        var observations = new List<FillMarkoutObservation>();
        for (var i = 0; i < 10; i++)
        {
            var isAdverse = i < 8;
            var fillPrice = 60_000m;
            // If adverse buy: markout price drops to 59,950 (-$50 markout)
            // If benign buy: markout price rises to 60,050 (+$50 markout)
            var markout = isAdverse ? 59_950m : 60_050m;
            observations.Add(new FillMarkoutObservation(
                $"fill-{i}",
                OrderSide.Buy,
                fillPrice,
                markout,
                now.AddSeconds(i * 10)));
        }

        var assessment = AdverseSelectionDetector.EvaluateMarkouts(observations);
        Assert.True(assessment.IsToxicFlowDetected);
        Assert.True(assessment.ShouldWidenSpreads);
        Assert.Equal(0.8m, assessment.AdverseRatio);
        Assert.Contains("TOXIC_FLOW_DETECTED", assessment.Reason, StringComparison.Ordinal);
    }
}
