using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExecutionQualityEvaluatorTests
{
    [Fact]
    public void LegacyEstimateWithoutQualityBenchmarkRemainsValidAndNotComparable()
    {
        var expectation = new ExecutionFeeEstimate(
            .001m, "hash", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var result = ExecutionQualityEvaluator.Compare(
            expectation, OrderSide.Buy, new Quantity(4m), null, new Price(101m), DateTimeOffset.UnixEpoch);

        Assert.Equal("NOT_COMPARABLE", result.Status);
        Assert.Equal("EXECUTION_BENCHMARK_UNAVAILABLE", result.Reason);
    }

    [Fact]
    public void RejectsPartialQualityBenchmark()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionFeeEstimate(
            .001m, "hash", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            referencePrice: 100m));
    }

    [Fact]
    public void ComputesAdverseShortfallCumulativeFillRatioAndLatency()
    {
        var expectation = new ExecutionFeeEstimate(.001m, "hash", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            100m, 10m, DateTimeOffset.UnixEpoch);
        var result = ExecutionQualityEvaluator.Compare(
            expectation, OrderSide.Buy, new Quantity(4m), new Quantity(7m), new Price(101m), DateTimeOffset.UnixEpoch.AddSeconds(2));
        Assert.Equal(.7m, result.FillRatio);
        Assert.Equal(4m, result.ImplementationShortfallQuote);
        Assert.Equal(2_000L, result.DecisionToFillLatencyMilliseconds);
        Assert.Equal("COMPARABLE", result.Status);
    }

    [Fact]
    public void DoesNotCompareFillThatExceedsAuthorizedQuantity()
    {
        var expectation = new ExecutionFeeEstimate(.001m, "hash", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            100m, 10m, DateTimeOffset.UnixEpoch);

        var result = ExecutionQualityEvaluator.Compare(
            expectation, OrderSide.Sell, new Quantity(4m), new Quantity(11m), new Price(99m), DateTimeOffset.UnixEpoch);

        Assert.Equal("NOT_COMPARABLE", result.Status);
        Assert.Equal("CUMULATIVE_FILL_INVALID", result.Reason);
    }

    [Fact]
    public void DoesNotCompareFillThatPrecedesDecision()
    {
        var expectation = new ExecutionFeeEstimate(.001m, "hash", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            100m, 10m, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var result = ExecutionQualityEvaluator.Compare(
            expectation, OrderSide.Buy, new Quantity(1m), new Quantity(1m), new Price(100m), DateTimeOffset.UnixEpoch);

        Assert.Equal("NOT_COMPARABLE", result.Status);
        Assert.Equal("FILL_PRECEDES_DECISION", result.Reason);
    }
}
