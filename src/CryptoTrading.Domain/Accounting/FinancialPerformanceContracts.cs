using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Accounting;

/// <summary>
/// Immutable metrics of profit and loss over a defined temporal window.
/// </summary>
public sealed record PeriodPerformance
{
    public PeriodPerformance(
        string periodName,
        decimal profitUsd,
        decimal profitBrl,
        decimal profitPercentage,
        decimal realizedProfitUsd,
        decimal unrealizedProfitUsd,
        decimal feesPaidUsd,
        int tradesCount,
        decimal winRatePercentage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodName);
        PeriodName = periodName.Trim();
        ProfitUsd = profitUsd;
        ProfitBrl = profitBrl;
        ProfitPercentage = profitPercentage;
        RealizedProfitUsd = realizedProfitUsd;
        UnrealizedProfitUsd = unrealizedProfitUsd;
        FeesPaidUsd = feesPaidUsd;
        TradesCount = Math.Max(0, tradesCount);
        WinRatePercentage = Math.Clamp(winRatePercentage, 0m, 100m);
    }

    public string PeriodName { get; }
    public decimal ProfitUsd { get; }
    public decimal ProfitBrl { get; }
    public decimal ProfitPercentage { get; }
    public decimal RealizedProfitUsd { get; }
    public decimal UnrealizedProfitUsd { get; }
    public decimal FeesPaidUsd { get; }
    public int TradesCount { get; }
    public decimal WinRatePercentage { get; }
}

/// <summary>
/// Complete portfolio financial performance snapshot including capital invested, current NAV, and period PnL.
/// </summary>
public sealed record PortfolioFinancialSnapshot
{
    public PortfolioFinancialSnapshot(
        decimal totalDepositedUsd,
        decimal totalDepositedBrl,
        decimal totalWithdrawnUsd,
        decimal totalWithdrawnBrl,
        decimal currentNavUsd,
        decimal currentNavBrl,
        decimal cashBalanceUsd,
        decimal positionsMarketValueUsd,
        decimal totalProfitUsd,
        decimal totalProfitBrl,
        decimal totalProfitPercentage,
        PeriodPerformance day,
        PeriodPerformance week,
        PeriodPerformance month,
        PeriodPerformance year,
        PeriodPerformance allTime,
        decimal usdBrlRate,
        DateTimeOffset calculatedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDepositedUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(totalDepositedBrl);
        ArgumentOutOfRangeException.ThrowIfNegative(totalWithdrawnUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(totalWithdrawnBrl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(usdBrlRate);

        TotalDepositedUsd = totalDepositedUsd;
        TotalDepositedBrl = totalDepositedBrl;
        TotalWithdrawnUsd = totalWithdrawnUsd;
        TotalWithdrawnBrl = totalWithdrawnBrl;
        CurrentNavUsd = currentNavUsd;
        CurrentNavBrl = currentNavBrl;
        CashBalanceUsd = cashBalanceUsd;
        PositionsMarketValueUsd = positionsMarketValueUsd;
        TotalProfitUsd = totalProfitUsd;
        TotalProfitBrl = totalProfitBrl;
        TotalProfitPercentage = totalProfitPercentage;
        Day = day ?? throw new ArgumentNullException(nameof(day));
        Week = week ?? throw new ArgumentNullException(nameof(week));
        Month = month ?? throw new ArgumentNullException(nameof(month));
        Year = year ?? throw new ArgumentNullException(nameof(year));
        AllTime = allTime ?? throw new ArgumentNullException(nameof(allTime));
        UsdBrlRate = usdBrlRate;
        CalculatedAtUtc = calculatedAtUtc;
    }

    public decimal TotalDepositedUsd { get; }
    public decimal TotalDepositedBrl { get; }
    public decimal TotalWithdrawnUsd { get; }
    public decimal TotalWithdrawnBrl { get; }

    public decimal NetInvestedCapitalUsd => TotalDepositedUsd - TotalWithdrawnUsd;
    public decimal NetInvestedCapitalBrl => TotalDepositedBrl - TotalWithdrawnBrl;

    public decimal CurrentNavUsd { get; }
    public decimal CurrentNavBrl { get; }
    public decimal CashBalanceUsd { get; }
    public decimal PositionsMarketValueUsd { get; }

    public decimal TotalProfitUsd { get; }
    public decimal TotalProfitBrl { get; }
    public decimal TotalProfitPercentage { get; }

    public PeriodPerformance Day { get; }
    public PeriodPerformance Week { get; }
    public PeriodPerformance Month { get; }
    public PeriodPerformance Year { get; }
    public PeriodPerformance AllTime { get; }

    public decimal UsdBrlRate { get; }
    public DateTimeOffset CalculatedAtUtc { get; }
}

/// <summary>
/// Mathematical engine computing capital basis and period PnL.
/// </summary>
public static class FinancialPerformanceCalculator
{
    public static PortfolioFinancialSnapshot Calculate(
        decimal totalDepositedUsd,
        decimal totalWithdrawnUsd,
        decimal cashBalanceUsd,
        decimal positionsMarketValueUsd,
        decimal usdBrlRate,
        decimal dayRealizedUsd,
        decimal dayUnrealizedUsd,
        decimal dayFeesUsd,
        int dayTrades,
        int dayWins,
        decimal weekRealizedUsd,
        decimal weekUnrealizedUsd,
        decimal weekFeesUsd,
        int weekTrades,
        int weekWins,
        decimal monthRealizedUsd,
        decimal monthUnrealizedUsd,
        decimal monthFeesUsd,
        int monthTrades,
        int monthWins,
        decimal yearRealizedUsd,
        decimal yearUnrealizedUsd,
        decimal yearFeesUsd,
        int yearTrades,
        int yearWins,
        DateTimeOffset calculatedAtUtc)
    {
        var currentNavUsd = cashBalanceUsd + positionsMarketValueUsd;
        var currentNavBrl = currentNavUsd * usdBrlRate;
        var totalDepositedBrl = totalDepositedUsd * usdBrlRate;
        var totalWithdrawnBrl = totalWithdrawnUsd * usdBrlRate;
        var netInvestedUsd = totalDepositedUsd - totalWithdrawnUsd;

        var totalProfitUsd = currentNavUsd - netInvestedUsd;
        var totalProfitBrl = totalProfitUsd * usdBrlRate;
        var totalProfitPct = netInvestedUsd > 0m ? (totalProfitUsd / netInvestedUsd) * 100m : 0m;

        PeriodPerformance CreatePeriod(
            string name,
            decimal realized,
            decimal unrealized,
            decimal fees,
            int trades,
            int wins)
        {
            var netProfitUsd = realized + unrealized - fees;
            var netProfitBrl = netProfitUsd * usdBrlRate;
            var capitalBase = currentNavUsd - netProfitUsd;
            var pct = capitalBase > 0m ? (netProfitUsd / capitalBase) * 100m : 0m;
            var winRate = trades > 0 ? ((decimal)wins / trades) * 100m : 0m;

            return new PeriodPerformance(
                name,
                netProfitUsd,
                netProfitBrl,
                pct,
                realized,
                unrealized,
                fees,
                trades,
                winRate);
        }

        var day = CreatePeriod("Dia (24h)", dayRealizedUsd, dayUnrealizedUsd, dayFeesUsd, dayTrades, dayWins);
        var week = CreatePeriod("Semana (7d)", weekRealizedUsd, weekUnrealizedUsd, weekFeesUsd, weekTrades, weekWins);
        var month = CreatePeriod("Mês (30d)", monthRealizedUsd, monthUnrealizedUsd, monthFeesUsd, monthTrades, monthWins);
        var year = CreatePeriod("Ano (YTD)", yearRealizedUsd, yearUnrealizedUsd, yearFeesUsd, yearTrades, yearWins);

        var allTime = new PeriodPerformance(
            "Total Acumulado",
            totalProfitUsd,
            totalProfitBrl,
            totalProfitPct,
            totalProfitUsd,
            0m,
            yearFeesUsd,
            yearTrades,
            yearTrades > 0 ? ((decimal)yearWins / yearTrades) * 100m : 0m);

        return new PortfolioFinancialSnapshot(
            totalDepositedUsd,
            totalDepositedBrl,
            totalWithdrawnUsd,
            totalWithdrawnBrl,
            currentNavUsd,
            currentNavBrl,
            cashBalanceUsd,
            positionsMarketValueUsd,
            totalProfitUsd,
            totalProfitBrl,
            totalProfitPct,
            day,
            week,
            month,
            year,
            allTime,
            usdBrlRate,
            calculatedAtUtc);
    }
}
