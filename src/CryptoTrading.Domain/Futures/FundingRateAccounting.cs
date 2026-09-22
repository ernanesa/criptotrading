using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Futures;

/// <summary>Settlement transaction for periodic 8h funding rate exchange between longs and shorts (US-144).</summary>
public sealed record FundingSettlementRecord(
    Symbol Symbol,
    PositionSide Side,
    decimal PositionNotional,
    decimal FundingRate,
    decimal NetFundingCashFlow,
    bool IsCashInflow,
    DateTimeOffset SettledAtUtc);

/// <summary>
/// Funding rate and basis settlement engine (US-144).
/// Computes periodic cash flows and basis divergence between spot and perpetual contracts.
/// </summary>
public static class FundingRateAccounting
{
    public static decimal ComputeAnnualizedBasisRate(
        decimal futuresPrice,
        decimal spotIndexPrice,
        decimal daysToExpiry = 365m)
    {
        if (spotIndexPrice <= 0m || daysToExpiry <= 0m)
        {
            return 0m;
        }

        var basis = (futuresPrice - spotIndexPrice) / spotIndexPrice;
        var annualized = basis * (365.0m / daysToExpiry);
        return Math.Round(annualized, 4);
    }

    public static FundingSettlementRecord SettleFundingPayment(
        Symbol symbol,
        PositionSide side,
        decimal positionNotional,
        decimal fundingRate,
        DateTimeOffset settledAtUtc)
    {
        if (positionNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(positionNotional), "Position notional cannot be negative.");
        }

        if (settledAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Settlement timestamp must be UTC.", nameof(settledAtUtc));
        }

        var grossPayment = positionNotional * Math.Abs(fundingRate);
        decimal netCashFlow;

        if (fundingRate >= 0m)
        {
            netCashFlow = side == PositionSide.LongSide ? -grossPayment : grossPayment;
        }
        else
        {
            netCashFlow = side == PositionSide.LongSide ? grossPayment : -grossPayment;
        }

        return new FundingSettlementRecord(
            symbol,
            side,
            Math.Round(positionNotional, 4),
            fundingRate,
            Math.Round(netCashFlow, 4),
            IsCashInflow: netCashFlow > 0m,
            settledAtUtc);
    }
}
