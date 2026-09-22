using System.Globalization;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ModelStrategyRegistryTests
{
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void ModelRecordCalculatesCanonicalSha256Identity()
    {
        var model = new StrategyModelRecord(
            "model-1",
            "trend-btcusdt",
            "1.0.0",
            "commit-abc123",
            "ds-hash-456",
            "cfg-fp-789",
            ModelLifecycleStatus.Candidate,
            TestTime,
            "quant-lead");

        Assert.Matches("^[0-9A-F]{64}$", model.CanonicalHash);
    }

    [Fact]
    public void RegistryPreventsDuplicateModelIds()
    {
        var registry = new InMemoryModelStrategyRegistry();
        var model = new StrategyModelRecord("m-1", "s-1", "1", "c-1", "d-1", "f-1", ModelLifecycleStatus.Draft, TestTime, "author");

        registry.RegisterModel(model);

        Assert.Throws<InvalidOperationException>(() => registry.RegisterModel(model));
    }

    [Fact]
    public void PromotingNewModelToActiveRetiresPreviousActiveModel()
    {
        var registry = new InMemoryModelStrategyRegistry();
        var model1 = new StrategyModelRecord("m-1", "strat-btc", "1.0", "c-1", "d-1", "f-1", ModelLifecycleStatus.Active, TestTime, "lead");
        var model2 = new StrategyModelRecord("m-2", "strat-btc", "1.1", "c-2", "d-2", "f-2", ModelLifecycleStatus.Candidate, TestTime.AddHours(1), "lead");

        registry.RegisterModel(model1);
        registry.RegisterModel(model2);

        Assert.Equal("m-1", registry.GetActiveModel("strat-btc")?.ModelId);

        // Transition m-2 to Active
        registry.TransitionStatus("m-2", ModelLifecycleStatus.Active, "lead", "Champion defeated in OOS", TestTime.AddHours(2));

        Assert.Equal("m-2", registry.GetActiveModel("strat-btc")?.ModelId);
        Assert.Equal(ModelLifecycleStatus.Retired, registry.GetModel("m-1")?.Status);
    }
}
