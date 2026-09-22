using CryptoTrading.Domain.Accounting;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class FinancialPerformanceTests
{
    [Fact]
    public void CalculateWithValidInputsComputesAccuratePeriodsAndCapitalBasis()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var usdBrl = 5.60m;
        var totalDepositedUsd = 10_000m;
        var totalWithdrawnUsd = 0m;
        var cashBalanceUsd = 4_000m;
        var positionsMarketValueUsd = 8_500m; // NAV = 12,500 USD

        // Act
        var snapshot = FinancialPerformanceCalculator.Calculate(
            totalDepositedUsd: totalDepositedUsd,
            totalWithdrawnUsd: totalWithdrawnUsd,
            cashBalanceUsd: cashBalanceUsd,
            positionsMarketValueUsd: positionsMarketValueUsd,
            usdBrlRate: usdBrl,
            // Day
            dayRealizedUsd: 150m,
            dayUnrealizedUsd: 50m,
            dayFeesUsd: 10m,
            dayTrades: 4,
            dayWins: 3,
            // Week
            weekRealizedUsd: 450m,
            weekUnrealizedUsd: 100m,
            weekFeesUsd: 35m,
            weekTrades: 15,
            weekWins: 11,
            // Month
            monthRealizedUsd: 1_200m,
            monthUnrealizedUsd: 200m,
            monthFeesUsd: 90m,
            monthTrades: 45,
            monthWins: 32,
            // Year
            yearRealizedUsd: 2_400m,
            yearUnrealizedUsd: 200m,
            yearFeesUsd: 100m,
            yearTrades: 120,
            yearWins: 84,
            calculatedAtUtc: now);

        // Assert
        Assert.Equal(10_000m, snapshot.TotalDepositedUsd);
        Assert.Equal(56_000m, snapshot.TotalDepositedBrl);
        Assert.Equal(10_000m, snapshot.NetInvestedCapitalUsd);
        Assert.Equal(56_000m, snapshot.NetInvestedCapitalBrl);

        Assert.Equal(12_500m, snapshot.CurrentNavUsd);
        Assert.Equal(70_000m, snapshot.CurrentNavBrl);

        Assert.Equal(2_500m, snapshot.TotalProfitUsd);
        Assert.Equal(14_000m, snapshot.TotalProfitBrl);
        Assert.Equal(25.0m, snapshot.TotalProfitPercentage);

        // Day: 150 + 50 - 10 = 190 USD
        Assert.Equal(190m, snapshot.Day.ProfitUsd);
        Assert.Equal(190m * usdBrl, snapshot.Day.ProfitBrl);
        Assert.Equal(75m, snapshot.Day.WinRatePercentage); // 3 / 4 = 75%
        Assert.Equal(4, snapshot.Day.TradesCount);

        // Week: 450 + 100 - 35 = 515 USD
        Assert.Equal(515m, snapshot.Week.ProfitUsd);
        Assert.Equal(515m * usdBrl, snapshot.Week.ProfitBrl);
        Assert.Equal(15, snapshot.Week.TradesCount);

        // Month: 1200 + 200 - 90 = 1310 USD
        Assert.Equal(1310m, snapshot.Month.ProfitUsd);
        Assert.Equal(1310m * usdBrl, snapshot.Month.ProfitBrl);

        // Year: 2400 + 200 - 100 = 2500 USD
        Assert.Equal(2500m, snapshot.Year.ProfitUsd);
        Assert.Equal(2500m * usdBrl, snapshot.Year.ProfitBrl);
        Assert.Equal(70m, snapshot.Year.WinRatePercentage); // 84 / 120 = 70%
    }
}
