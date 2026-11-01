using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class StopRiskSizingConfigurationTests
{
    [Fact]
    public void DefaultDoesNotAuthorizeANewSizingBudget()
    {
        Assert.Null(new WorkerRuntimeOptions().ResolveStopRiskSizing());
    }

    [Fact]
    public void ExplicitVersionedBudgetIsASeparateReductionOnlySizingPolicy()
    {
        var options = new WorkerRuntimeOptions { AutonomyStopRiskSizingVersion = "sizing-v1", AutonomyMaximumPlannedLossNotional = 1m };
        var policy = options.ResolveStopRiskSizing();
        Assert.NotNull(policy);
        Assert.Equal("sizing-v1", policy.Version);
        Assert.Equal(1m, policy.MaximumPlannedLossNotional);
        Assert.True(options.NoTrade);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("sizing-v1", null)]
    [InlineData("sizing-v1", "0")]
    [InlineData("sizing-v1", "-1")]
    public void IncompleteOrNonpositiveBudgetsCannotBeActivated(string? version, string? amount)
    {
        var options = new WorkerRuntimeOptions
        {
            AutonomyStopRiskSizingVersion = version,
            AutonomyMaximumPlannedLossNotional = amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)
        };
        Assert.Throws<OptionsValidationException>(() => options.ResolveStopRiskSizing());
    }
}
