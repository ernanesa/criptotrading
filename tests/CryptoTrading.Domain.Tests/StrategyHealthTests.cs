using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class StrategyHealthTests
{
    private static StrategyHealthInput HealthyInput() => new(
        HasFreshMarketData: true,
        HasFreshSignal: true,
        RiskAllowsNewExposure: true,
        ReconciliationIsHealthy: true,
        OperationalControlAllowsTrading: true,
        EvaluatedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void HealthyInputAllowsNewExposure()
    {
        var result = StrategyHealthEvaluator.Evaluate(HealthyInput());

        Assert.Equal(StrategyHealthStatus.Healthy, result.Status);
        Assert.True(result.CanOpenNewExposure);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void StaleMarketDataProducesNoTrade()
    {
        var input = HealthyInput() with { HasFreshMarketData = false };

        var result = StrategyHealthEvaluator.Evaluate(input);

        Assert.Equal(StrategyHealthStatus.NoTrade, result.Status);
        Assert.False(result.CanOpenNewExposure);
        Assert.Contains("MARKET_DATA_STALE", result.Reasons);
    }

    [Fact]
    public void ReconciliationMismatchBlocksTrading()
    {
        var input = HealthyInput() with { ReconciliationIsHealthy = false };

        var result = StrategyHealthEvaluator.Evaluate(input);

        Assert.Equal(StrategyHealthStatus.Blocked, result.Status);
        Assert.False(result.CanOpenNewExposure);
    }

    [Fact]
    public void NonUtcEvaluationFailsClosed()
    {
        var input = HealthyInput() with
        {
            EvaluatedAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(-3))
        };

        Assert.Throws<ArgumentException>(() => StrategyHealthEvaluator.Evaluate(input));
    }
}
