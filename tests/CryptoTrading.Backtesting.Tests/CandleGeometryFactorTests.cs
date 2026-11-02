using System.Globalization;
using System.Text.Json.Nodes;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class CandleGeometryFactorTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    private static AdaptiveDecisionCycle CreateCycle() =>
        new(null, null, MlOperationalTestModels.Catalog(false));

    private static RiskPolicy CreateRiskPolicy() =>
        new(
            version: "v1.0.0-test",
            maxOrderNotional: 1000m,
            maxExposureNotional: 5000m,
            maxDailyLoss: 200m,
            maxDrawdown: 500m,
            maxMarketDataAge: TimeSpan.FromMinutes(5),
            withdrawalsEnabled: false,
            maxSlippageBps: 50m,
            maxVolatilityBps: 300m,
            minLiquidityNotional: 1000m);

    private static PortfolioSnapshot CreatePortfolio() =>
        new(
            Balances: new Dictionary<Asset, Money>
            {
                [new Asset("USDT")] = new(10_000m, new Asset("USDT")),
                [new Asset("BTC")] = new(0m, new Asset("BTC")),
            },
            GrossExposureNotional: 0m,
            DailyLossNotional: 0m,
            Drawdown: 0m,
            PositionCostNotional: 0m,
            RiskContextComplete: true);

    private static EffectiveCommissionSnapshot CreateCommission(DateTimeOffset observedAtUtc) => new(
        BtcUsdt, "testnet", "account",
        new CommissionRateSet(.00075m, .00075m, 0m, 0m),
        new CommissionRateSet(0m, 0m, 0m, 0m),
        new CommissionRateSet(0m, 0m, 0m, 0m),
        false, false, null, null, false, observedAtUtc);

    private static List<CloseBar> CreateSyntheticBars(int count, decimal startPrice, decimal step = 0.1m)
    {
        var bars = new List<CloseBar>(count);
        var current = startPrice;
        for (var i = 0; i < count; i++)
        {
            current += step;
            bars.Add(new CloseBar(
                BtcUsdt,
                TestTime.AddMinutes(i - count),
                current,
                openPrice: current - (step * 0.5m),
                highPrice: current + (step * 0.8m),
                lowPrice: current - (step * 0.8m),
                volume: 100m,
                quoteVolume: current * 100m,
                numberOfTrades: 50,
                takerBuyBaseVolume: 65m));
        }
        return bars;
    }

    private static List<CloseBar> Bars(params decimal[] prices)
    {
        var start = TestTime.AddMinutes(-prices.Length);
        var list = new List<CloseBar>(prices.Length);
        for (var i = 0; i < prices.Length; i++)
        {
            list.Add(new CloseBar(BtcUsdt, start.AddMinutes(i + 1), prices[i]));
        }
        return list;
    }

    private static List<CloseBar> WithWilderWarmup(List<CloseBar> bars)
    {
        var prefixCount = Math.Max(0, 28 - bars.Count);
        List<CloseBar> result = new(prefixCount + bars.Count);
        for (var i = 0; i < prefixCount; i++)
            result.Add(new CloseBar(bars[0].Symbol, bars[0].CloseTimeUtc.AddMinutes(i - prefixCount), bars[0].ClosePrice.Value));
        result.AddRange(bars);
        return result;
    }

    [Fact]
    public void TechnicalIndicatorCalculatorComputesAccurateShadowAndBodyRatiosForHammer()
    {
        var bars = CreateSyntheticBars(25, 100m);
        // Hammer pin-bar on the final bar:
        // Open = 100.0, Close = 100.5, Low = 90.0, High = 101.0
        // Range = 11.0. Lower shadow = 100.0 - 90.0 = 10.0. LowerShadowRatio = 10.0 / 11.0 = 0.9091
        // Upper shadow = 101.0 - 100.5 = 0.5. UpperShadowRatio = 0.5 / 11.0 = 0.0455
        // Body = 0.5. BodyToRangeRatio = 0.5 / 11.0 = 0.0455
        bars[^1] = new CloseBar(
            BtcUsdt,
            TestTime,
            closePrice: 100.5m,
            openPrice: 100.0m,
            highPrice: 101.0m,
            lowPrice: 90.0m,
            volume: 150m,
            quoteVolume: 100.5m * 150m,
            numberOfTrades: 100,
            takerBuyBaseVolume: 120m);

        var snapshot = TechnicalIndicatorCalculator.Evaluate(bars);

        Assert.Equal(0.9091m, snapshot.LowerShadowRatio);
        Assert.Equal(0.0455m, snapshot.UpperShadowRatio);
        Assert.Equal(0.0455m, snapshot.BodyToRangeRatio);
        Assert.Equal(1.0001m, snapshot.LowerShadowRatio + snapshot.UpperShadowRatio + snapshot.BodyToRangeRatio);
    }

    [Fact]
    public void TechnicalIndicatorCalculatorComputesAccurateShadowAndBodyRatiosForShootingStar()
    {
        var bars = CreateSyntheticBars(25, 100m);
        // Shooting star (long upper wick rejection):
        // Open = 92.0, Close = 93.0, High = 105.0, Low = 91.0
        // Range = 14.0. Upper shadow = 105.0 - 93.0 = 12.0. UpperShadowRatio = 12.0 / 14.0 = 0.8571
        // Lower shadow = 92.0 - 91.0 = 1.0. LowerShadowRatio = 1.0 / 14.0 = 0.0714
        // Body = 1.0. BodyToRangeRatio = 1.0 / 14.0 = 0.0714
        bars[^1] = new CloseBar(
            BtcUsdt,
            TestTime,
            closePrice: 93.0m,
            openPrice: 92.0m,
            highPrice: 105.0m,
            lowPrice: 91.0m,
            volume: 200m,
            quoteVolume: 93.0m * 200m,
            numberOfTrades: 120,
            takerBuyBaseVolume: 40m);

        var snapshot = TechnicalIndicatorCalculator.Evaluate(bars);

        Assert.Equal(0.8571m, snapshot.UpperShadowRatio);
        Assert.Equal(0.0714m, snapshot.LowerShadowRatio);
        Assert.Equal(0.0714m, snapshot.BodyToRangeRatio);
    }

    [Fact]
    public void TechnicalIndicatorCalculatorHandlesZeroRangeGracefully()
    {
        var bars = CreateSyntheticBars(25, 100m, step: 0m);
        bars[^1] = new CloseBar(
            BtcUsdt,
            TestTime,
            closePrice: 100m,
            openPrice: 100m,
            highPrice: 100m,
            lowPrice: 100m,
            volume: 10m,
            quoteVolume: 1000m,
            numberOfTrades: 1,
            takerBuyBaseVolume: 5m);

        var snapshot = TechnicalIndicatorCalculator.Evaluate(bars);

        Assert.Equal(0m, snapshot.LowerShadowRatio);
        Assert.Equal(0m, snapshot.UpperShadowRatio);
        Assert.Equal(1.0m, snapshot.BodyToRangeRatio);
    }

    [Fact]
    public void BlocksLongEntryOnUpperWickRejectionWithNegativeCvd()
    {
        var cycle = CreateCycle();
        var baseBars = WithWilderWarmup(Bars(100m, 100.1m, 100.2m, 100.3m, 100.2m, 100.38m));
        var bars = baseBars
            .Select(b => new CloseBar(b.Symbol, b.CloseTimeUtc, b.ClosePrice.Value,
                volume: 100m, takerBuyBaseVolume: 42m, quoteVolume: b.ClosePrice.Value * 100m, numberOfTrades: 50))
            .ToList();
        // Replace final bar with upper wick rejection (UpperShadowRatio >= 0.50m, CvdImbalance <= -0.10m):
        // Open = 100.38, Close = 100.39, High = 101.50, Low = 100.35. Range = 1.15. Upper shadow = 1.11 -> 1.11 / 1.15 = 0.965 >= 0.50m.
        // TakerBuyBaseVolume = 20m out of 100m -> delta = -60m -> CvdImbalance <= -0.10m.
        bars[^1] = new CloseBar(
            BtcUsdt,
            TestTime,
            closePrice: 100.39m,
            openPrice: 100.38m,
            highPrice: 101.50m,
            lowPrice: 100.35m,
            volume: 100m,
            quoteVolume: 100.39m * 100m,
            numberOfTrades: 50,
            takerBuyBaseVolume: 20m);

        var request = new AdaptiveDecisionCycleRequest(
            bars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(100.38m),
                new Price(100.40m),
                10_000m,
                TestTime,
                quantityStepSize: 0.0001m,
                priceTickSize: 0.01m,
                bidQuantity: 15m,
                askQuantity: 5m),
            ExecutionMarketMaxAge = TimeSpan.FromMinutes(5),
            AvailableAccountLiquidityNotional = 10_000m,
            EffectiveCommission = CreateCommission(TestTime),
            CommissionMaximumAge = TimeSpan.FromMinutes(1),
            UseConservativeCommissionReserve = true,
        };

        var result = cycle.Evaluate(request);

        Assert.True(result.IsNoTrade);
        Assert.Equal("CANDLE_UPPER_WICK_REJECTION_BLOCKED", result.NoTradeReason);
    }

    [Fact]
    public void AppliesAbsorptionBonusOnPinBarAbsorptionWithPositiveCvd()
    {
        var cycle = CreateCycle();
        var baseBars = WithWilderWarmup(Bars(100m, 100.1m, 100.2m, 100.3m, 100.2m, 100.38m));
        var normalBars = baseBars
            .Select(b => new CloseBar(b.Symbol, b.CloseTimeUtc, b.ClosePrice.Value,
                openPrice: b.ClosePrice.Value - 0.02m,
                highPrice: b.ClosePrice.Value + 0.05m,
                lowPrice: b.ClosePrice.Value - 0.04m,
                volume: 100m, takerBuyBaseVolume: 70m, quoteVolume: b.ClosePrice.Value * 100m, numberOfTrades: 50))
            .ToList();

        var baseRequest = new AdaptiveDecisionCycleRequest(
            normalBars,
            CreatePortfolio(),
            CreateRiskPolicy(),
            OperationalControlSnapshot.Initial,
            EntryTargetNotional: 500m,
            EvaluatedAtUtc: TestTime)
        {
            ExecutionMarket = new ExecutionMarketSnapshot(
                new Price(100.37m),
                new Price(100.39m),
                10_000m,
                TestTime,
                quantityStepSize: 0.0001m,
                priceTickSize: 0.01m,
                bidQuantity: 15m,
                askQuantity: 5m),
            ExecutionMarketMaxAge = TimeSpan.FromMinutes(5),
            AvailableAccountLiquidityNotional = 10_000m,
            EffectiveCommission = CreateCommission(TestTime),
            CommissionMaximumAge = TimeSpan.FromMinutes(1),
            UseConservativeCommissionReserve = true,
        };

        // Pin-bar absorption bar: Lower shadow >= 0.45m, CvdImbalance >= 0.10m
        // Open = 100.36, Close = 100.38, High = 100.40, Low = 100.10. Range = 0.30.
        // Lower shadow = 100.36 - 100.10 = 0.26 -> LowerShadowRatio = 0.26 / 0.30 = 0.867 (>= 0.45m).
        // TakerBuyBaseVolume = 85m out of 100m -> CvdImbalance >= 0.10m.
        var absorptionBars = normalBars.ToList();
        absorptionBars[^1] = new CloseBar(
            BtcUsdt,
            TestTime,
            closePrice: 100.38m,
            openPrice: 100.36m,
            highPrice: 100.40m,
            lowPrice: 100.10m,
            volume: 100m,
            quoteVolume: 100.38m * 100m,
            numberOfTrades: 50,
            takerBuyBaseVolume: 85m);

        var absorptionRequest = baseRequest with { Bars = absorptionBars };

        var baseResult = cycle.Evaluate(baseRequest);
        var absorptionResult = cycle.Evaluate(absorptionRequest);

        Assert.False(baseResult.IsNoTrade, baseResult.NoTradeReason);
        Assert.False(absorptionResult.IsNoTrade, absorptionResult.NoTradeReason);

        var baseTrace = JsonNode.Parse(baseResult.DecisionTraceJson)!;
        var absorptionTrace = JsonNode.Parse(absorptionResult.DecisionTraceJson)!;

        var baseConfidence = (decimal)baseTrace["DecisionConfidence"]!;
        var absorptionConfidence = (decimal)absorptionTrace["DecisionConfidence"]!;

        Assert.True(absorptionConfidence > baseConfidence, $"absorptionConfidence={absorptionConfidence}, baseConfidence={baseConfidence}");
        Assert.True(absorptionConfidence - baseConfidence >= 0.02m, $"delta={absorptionConfidence - baseConfidence}");
    }
}
