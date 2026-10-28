using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExecutionFeeDriftEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Asset Base = new("BTC");
    private static readonly Asset Quote = new("USDT");

    [Fact]
    public void QuoteCommissionWithinToleranceIsValuedFromTheFill()
    {
        var result = ExecutionFeeDriftEvaluator.Compare(
            Estimate(.001m), new Money(.1001m, Quote), new Quantity(1m), new Price(100m), Base, Quote, Now, 25m);

        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, result.Status);
        Assert.Equal(.1m, result.ExpectedQuote);
        Assert.Equal(.1001m, result.ActualQuote);
        Assert.Equal("fill-quote-asset", result.ValuationSource);
    }

    [Fact]
    public void BaseCommissionOverToleranceProducesExecutionFeeDrift()
    {
        var result = ExecutionFeeDriftEvaluator.Compare(
            Estimate(.001m), new Money(.002m, Base), new Quantity(1m), new Price(100m), Base, Quote, Now, 25m);

        Assert.True(result.IsDrift);
        Assert.Equal("EXECUTION_FEE_DRIFT", result.Reason);
        Assert.Equal(.2m, result.ActualQuote);
    }

    [Fact]
    public void BnbCommissionWithoutTrustedValuationIsNotComparable()
    {
        var result = ExecutionFeeDriftEvaluator.Compare(
            Estimate(.001m), new Money(.01m, new Asset("BNB")), new Quantity(1m), new Price(100m), Base, Quote, Now, 25m);

        Assert.Equal(ExecutionFeeComparisonStatus.NotComparable, result.Status);
        Assert.Equal("COMMISSION_VALUATION_UNAVAILABLE", result.Reason);
        Assert.Null(result.ActualQuote);
    }

    [Fact]
    public void BnbCommissionUsesNonFutureTrustedValuation()
    {
        var valuation = new ExecutionFeeAssetValuation(
            new Asset("BNB"), Quote, 100m, Now.AddSeconds(-2), "binance:GET:/api/v3/aggTrades");

        var result = ExecutionFeeDriftEvaluator.Compare(
            Estimate(.001m), new Money(.001m, new Asset("BNB")),
            new Quantity(1m), new Price(100m), Base, Quote, Now, 25m, valuation);

        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, result.Status);
        Assert.Equal(.1m, result.ActualQuote);
        Assert.Equal("binance:GET:/api/v3/aggTrades", result.ValuationSource);
    }

    [Fact]
    public void FutureFeeValuationRemainsNotComparable()
    {
        var valuation = new ExecutionFeeAssetValuation(
            new Asset("BNB"), Quote, 600m, Now.AddMilliseconds(1), "test");

        var result = ExecutionFeeDriftEvaluator.Compare(
            Estimate(.001m), new Money(.01m, new Asset("BNB")), new Quantity(1m), new Price(100m), Base, Quote, Now, 25m, valuation);

        Assert.Equal(ExecutionFeeComparisonStatus.NotComparable, result.Status);
    }

    [Fact]
    public void MissingEstimateLeavesExpectedFeeUnknown()
    {
        var result = ExecutionFeeDriftEvaluator.Compare(
            null, new Money(.1m, Quote), new Quantity(1m), new Price(100m), Base, Quote, Now);

        Assert.Equal(ExecutionFeeComparisonStatus.NotComparable, result.Status);
        Assert.Null(result.ExpectedQuote);
        Assert.Equal("FEE_ESTIMATE_UNAVAILABLE", result.Reason);
    }

    private static ExecutionFeeEstimate Estimate(decimal rate) => new(
        rate, "ABC123", "binance:commission", Now.AddMinutes(-1), Now.AddMinutes(14));
}
