using System.Globalization;
using CryptoTrading.Domain.Futures;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class FuturesTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void FuturesPositionCalculatesPnlAndLiquidationPricesForLongAndShort()
    {
        var spec = new FuturesContractSpec(
            Symbol: BtcUsdt,
            ContractType: ContractType.PerpetualLinear,
            ContractMultiplier: 1.0m,
            MaxLeverage: 20.0m,
            MaintenanceMarginRatio: 0.005m, // 0.5%
            MinNotional: 5.0m);

        // Long position at 10x leverage, entry $60,000, mark $63,000
        var longPos = new FuturesPosition(
            Symbol: BtcUsdt,
            Side: PositionSide.LongSide,
            Quantity: 1.0m,
            EntryPrice: 60_000.0m,
            MarkPrice: 63_000.0m,
            Leverage: 10.0m,
            MarginMode: MarginMode.Isolated,
            AllocatedMargin: 6_000.0m);

        Assert.Equal(3_000.0m, longPos.UnrealizedPnl);
        Assert.Equal(0.5m, longPos.ReturnOnEquityRate); // 3000 / 6000 = 50%

        // Liq price long: 60000 * (1 - 0.10 + 0.005) = 60000 * 0.905 = 54,300
        var longLiq = longPos.CalculateLiquidationPrice(spec.MaintenanceMarginRatio);
        Assert.Equal(54_300.0m, longLiq);

        // Short position at 10x leverage, entry $60,000, mark $58,000
        var shortPos = new FuturesPosition(
            Symbol: BtcUsdt,
            Side: PositionSide.ShortSide,
            Quantity: 1.0m,
            EntryPrice: 60_000.0m,
            MarkPrice: 58_000.0m,
            Leverage: 10.0m,
            MarginMode: MarginMode.Isolated,
            AllocatedMargin: 6_000.0m);

        Assert.Equal(2_000.0m, shortPos.UnrealizedPnl);
        // Liq price short: 60000 * (1 + 0.10 - 0.005) = 60000 * 1.095 = 65,700
        var shortLiq = shortPos.CalculateLiquidationPrice(spec.MaintenanceMarginRatio);
        Assert.Equal(65_700.0m, shortLiq);
    }

    [Fact]
    public void LiquidationRiskEngineDetectsSafeWarningAndEmergencyStates()
    {
        var spec = new FuturesContractSpec(
            Symbol: BtcUsdt,
            ContractType: ContractType.PerpetualLinear,
            ContractMultiplier: 1.0m,
            MaxLeverage: 20.0m,
            MaintenanceMarginRatio: 0.005m,
            MinNotional: 5.0m);

        // 1. Safe position (mark far from liq)
        var safePos = new FuturesPosition(
            Symbol: BtcUsdt,
            Side: PositionSide.LongSide,
            Quantity: 1.0m,
            EntryPrice: 60_000.0m,
            MarkPrice: 62_000.0m,
            Leverage: 5.0m,
            MarginMode: MarginMode.Isolated,
            AllocatedMargin: 12_000.0m);

        var safeAssessment = LiquidationRiskEngine.AssessPositionRisk(safePos, spec);
        Assert.True(safeAssessment.IsSafe);
        Assert.False(safeAssessment.RequiresMarginCall);
        Assert.False(safeAssessment.RequiresEmergencyDeleveraging);

        // 2. Warning / Emergency position (mark very close to liq)
        var dangerousPos = new FuturesPosition(
            Symbol: BtcUsdt,
            Side: PositionSide.LongSide,
            Quantity: 1.0m,
            EntryPrice: 60_000.0m,
            MarkPrice: 54_500.0m,
            Leverage: 10.0m,
            MarginMode: MarginMode.Isolated,
            AllocatedMargin: 6_000.0m);

        var dangerAssessment = LiquidationRiskEngine.AssessPositionRisk(dangerousPos, spec);
        Assert.False(dangerAssessment.IsSafe);
        Assert.True(dangerAssessment.RequiresMarginCall);
    }

    [Fact]
    public void DeltaHedgingEngineNeutralizesDirectionalExposure()
    {
        // Spot holding: +1.5 BTC
        var spotInventory = 1.5m;

        // Currently no futures positions: Net delta = +1.5 (long exposure)
        var hedge1 = DeltaHedgingEngine.ComputeHedge(BtcUsdt, spotInventory, []);
        Assert.True(hedge1.HedgeRequired);
        Assert.Equal(PositionSide.ShortSide, hedge1.ActionSide);
        Assert.Equal(1.5m, hedge1.HedgeQuantity);

        // Add 1.5 Short futures position: Net delta = 1.5 - 1.5 = 0
        var shortPosition = new FuturesPosition(
            Symbol: BtcUsdt,
            Side: PositionSide.ShortSide,
            Quantity: 1.5m,
            EntryPrice: 60_000m,
            MarkPrice: 60_000m,
            Leverage: 1.0m,
            MarginMode: MarginMode.Cross,
            AllocatedMargin: 90_000m);

        var hedge2 = DeltaHedgingEngine.ComputeHedge(BtcUsdt, spotInventory, [shortPosition]);
        Assert.False(hedge2.HedgeRequired);
        Assert.Null(hedge2.ActionSide);
        Assert.Equal(0m, hedge2.CurrentNetDelta);
    }

    [Fact]
    public void FundingRateAccountingAccruesPeriodicCashFlowsCorrectly()
    {
        var now = DateTimeOffset.Parse("2026-09-21T16:00:00Z", CultureInfo.InvariantCulture);

        // Long position with positive funding rate (0.01% = 0.0001) -> Long pays Short (cashflow < 0)
        var longSettlement = FundingRateAccounting.SettleFundingPayment(
            BtcUsdt,
            PositionSide.LongSide,
            positionNotional: 100_000m,
            fundingRate: 0.0001m,
            settledAtUtc: now);

        Assert.Equal(-10.0m, longSettlement.NetFundingCashFlow);
        Assert.False(longSettlement.IsCashInflow);

        // Short position with positive funding rate -> Short receives payment (cashflow > 0)
        var shortSettlement = FundingRateAccounting.SettleFundingPayment(
            BtcUsdt,
            PositionSide.ShortSide,
            positionNotional: 100_000m,
            fundingRate: 0.0001m,
            settledAtUtc: now);

        Assert.Equal(10.0m, shortSettlement.NetFundingCashFlow);
        Assert.True(shortSettlement.IsCashInflow);

        // Basis calculation
        var annualizedBasis = FundingRateAccounting.ComputeAnnualizedBasisRate(
            futuresPrice: 61_200m,
            spotIndexPrice: 60_000m,
            daysToExpiry: 90m);

        Assert.True(annualizedBasis > 0.05m); // > 5% annualized basis
    }
}
