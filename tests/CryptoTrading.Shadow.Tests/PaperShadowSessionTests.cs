using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using Xunit;

namespace CryptoTrading.Shadow.Tests;

public sealed class PaperShadowSessionTests
{
    [Fact]
    public void RunsStrategyThroughPaperAndReportsNoDriftForNoTradeWindow()
    {
        var symbol = new Symbol("BTCUSDT");
        var bars = new[]
        {
            new CloseBar(symbol, DateTimeOffset.UnixEpoch, 100m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(1), 99m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(2), 98m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(3), 97m),
        };
        var strategy = new TrendFollowingStrategy(new TrendFollowingParameters("shadow", "1", symbol, 2, 3, 0m, 0m));
        var paper = new PaperExchange();

        var result = new PaperShadowSession(strategy, paper).Run(bars);

        Assert.Equal(4, result.ObservationCount);
        Assert.Equal(0, result.DivergenceCount);
        Assert.Empty(paper.Fills);
    }

    [Fact]
    public void RecordsConfiguredStaleAndRecoveryWindowsWithoutNetwork()
    {
        var symbol = new Symbol("BTCUSDT");
        var bars = Enumerable.Range(0, 4)
            .Select(index => new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(index), 100m - index))
            .ToArray();
        var strategy = new TrendFollowingStrategy(new TrendFollowingParameters("shadow", "1", symbol, 2, 3, 0m, 0m));
        var result = new PaperShadowSession(
            strategy,
            new PaperExchange(),
            new PaperShadowOptions { StaleBarIndexes = new HashSet<int> { 1 }, RecoveredBarIndexes = new HashSet<int> { 2 } })
            .Run(bars);

        Assert.Equal(1, result.StaleCount);
        Assert.Equal(1, result.RecoveryCount);
        Assert.Equal(0, result.DivergenceCount);
    }

    [Fact]
    public void AppliesPaperFillWhenClockAndBarWindowAreAligned()
    {
        var symbol = new Symbol("BTCUSDT");
        var start = DateTimeOffset.UtcNow.AddSeconds(1);
        var bars = new[]
        {
            new CloseBar(symbol, start, 100m),
            new CloseBar(symbol, start.AddMinutes(1), 90m),
            new CloseBar(symbol, start.AddMinutes(2), 80m),
            new CloseBar(symbol, start.AddMinutes(3), 120m),
        };
        var strategy = new TrendFollowingStrategy(new TrendFollowingParameters("shadow", "1", symbol, 2, 3, 0m, 0m));
        var result = new PaperShadowSession(strategy, new PaperExchange()).Run(bars);

        Assert.Equal(0, result.DivergenceCount);
    }
}
