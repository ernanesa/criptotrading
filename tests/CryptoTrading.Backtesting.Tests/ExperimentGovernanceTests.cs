using CryptoTrading.Backtesting;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ExperimentGovernanceTests
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
    private const string DatasetHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HoldoutHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ConfigurationHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    [Fact]
    public void RegistrationIsCanonicalAndIdempotent()
    {
        var registry = new ExperimentRegistry();
        var first = NewRegistration();
        var second = NewRegistration();

        Assert.Same(first, registry.Register(first));
        Assert.Same(first, registry.Register(second));
        Assert.Equal(first.RegistrationHash, second.RegistrationHash);
    }

    [Fact]
    public void ConflictingRegistrationIsRejected()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration());

        var conflicting = NewRegistration(hypothesis: "different hypothesis");
        Assert.Throws<InvalidOperationException>(() => registry.Register(conflicting));
    }

    [Fact]
    public void TrialsAreCountedAndBudgetIsFailClosed()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration(trialBudget: 2));

        Assert.Equal(1, registry.RecordTrial("exp-1", DatasetHash, "continue", RegisteredAt).TrialNumber);
        Assert.Equal(2, registry.RecordTrial("exp-1", ConfigurationHash, "reject", RegisteredAt.AddMinutes(1)).TrialNumber);
        Assert.Throws<InvalidOperationException>(() => registry.RecordTrial("exp-1", HoldoutHash, "reject", RegisteredAt.AddMinutes(2)));
        Assert.Equal(2, registry.TrialsFor("exp-1").Count);
    }

    [Fact]
    public void HoldoutAccessIsAuditedAndUnauthorizedAccessContaminatesIt()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration());

        var authorized = registry.AccessHoldout("exp-1", "researcher", "locked evaluation", true, RegisteredAt);
        Assert.Equal(1, authorized.AccessNumber);
        Assert.Equal(ExperimentStatus.Registered, registry.Register(NewRegistration()).Status);

        var unauthorized = registry.AccessHoldout("exp-1", "debugger", "exploration", false, RegisteredAt.AddMinutes(1));
        Assert.False(unauthorized.Authorized);
        Assert.Throws<InvalidOperationException>(() => registry.AccessHoldout("exp-1", "researcher", "retry", true, RegisteredAt.AddMinutes(2)));
    }

    [Fact]
    public void CompletionIsBlockedAfterContamination()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration());
        registry.AccessHoldout("exp-1", "debugger", "exploration", false, RegisteredAt);

        Assert.Throws<InvalidOperationException>(() => registry.MarkCompleted("exp-1", accepted: true));
    }

    [Fact]
    public void ContaminationRequiresALaterReplacementVersion()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration());
        registry.AccessHoldout("exp-1", "debugger", "exploration", false, RegisteredAt);

        var replacement = NewRegistration(experimentId: "exp-2", holdoutHash: "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", registeredAt: RegisteredAt.AddDays(1));
        var registered = registry.RegisterReplacement("exp-1", replacement);

        Assert.Equal("exp-2", registered.ExperimentId);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterReplacement("exp-1", NewRegistration(experimentId: "exp-3")));
    }

    [Fact]
    public void TrialRequiresPreregistration()
    {
        var registry = new ExperimentRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.RecordTrial("missing", DatasetHash, "reject", RegisteredAt));
    }

    [Fact]
    public void InvalidOrSameHoldoutDatasetIsRejected()
    {
        Assert.Throws<ArgumentException>(() => NewRegistration(datasetHash: HoldoutHash, holdoutHash: HoldoutHash));
        Assert.Throws<ArgumentException>(() => NewRegistration(datasetHash: "invalid"));
    }

    [Fact]
    public void AllTrialsIncludingRejectedOnesRemainAuditable()
    {
        var registry = new ExperimentRegistry();
        registry.Register(NewRegistration(trialBudget: 3));
        registry.RecordTrial("exp-1", DatasetHash, "reject", RegisteredAt);
        registry.RecordTrial("exp-1", ConfigurationHash, "continue", RegisteredAt.AddMinutes(1));
        registry.MarkCompleted("exp-1", accepted: false);

        Assert.Equal(["reject", "continue"], registry.TrialsFor("exp-1").Select(trial => trial.Decision));
    }

    private static ExperimentRegistration NewRegistration(
        string hypothesis = "momentum persists after trend confirmation",
        int trialBudget = 3,
        string datasetHash = DatasetHash,
        string holdoutHash = HoldoutHash,
        string experimentId = "exp-1",
        DateTimeOffset? registeredAt = null) =>
        new(
            "family-1", experimentId, hypothesis, "trend-v1", datasetHash, holdoutHash,
            "{\"ema_fast\": [20, 30], \"ema_slow\": [50, 80]}", trialBudget,
            "net_return", "NO_TRADE/cash", "reject if OOS lower bound <= 0", registeredAt ?? RegisteredAt,
            "metrics-v1", ConfigurationHash);
}
