using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ExperimentRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HashC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    [Fact]
    public void RegistrationIsImmutableAndIdempotent()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration();

        var first = registry.Register(registration);
        var replay = registry.Register(Registration(registration.ExperimentId, registration.IdempotencyKey));

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(registration.PreregistrationSha256, replay.Record.Preregistration.PreregistrationSha256);
    }

    [Fact]
    public void ConflictingRegistrationFailsClosed()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration();
        registry.Register(registration);

        Assert.Throws<ExperimentRegistryConflictException>(() => registry.Register(
            Registration(registration.ExperimentId, registration.IdempotencyKey, hypothesis: "changed")));
    }

    [Fact]
    public void EveryTrialCountsAndBudgetIsEnforced()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration(trialBudget: 2);
        registry.Register(registration);

        registry.RecordTrial(registration.ExperimentId, Trial(1, "v1", "trial-1"));
        registry.RecordTrial(registration.ExperimentId, Trial(2, "v2", "trial-2"));
        Assert.Throws<ExperimentTrialBudgetExceededException>(() => registry.RecordTrial(registration.ExperimentId, Trial(3, "discarded", "trial-3")));
        Assert.Equal(2, registry.GetById(registration.ExperimentId).TrialCount);
    }

    [Fact]
    public void TrialReplayIsIdempotentAndConflictIsRejected()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration();
        registry.Register(registration);
        var trial = Trial(1, "v1", "trial-1");

        Assert.False(registry.RecordTrial(registration.ExperimentId, trial).IsReplay);
        Assert.True(registry.RecordTrial(registration.ExperimentId, trial).IsReplay);
        Assert.Throws<ExperimentRegistryConflictException>(() => registry.RecordTrial(
            registration.ExperimentId, Trial(1, "different", "trial-1")));
    }

    [Fact]
    public void UnauthorizedHoldoutAccessContaminatesAndBlocksFurtherAccess()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration();
        registry.Register(registration);

        var contaminated = registry.AccessHoldout(registration.ExperimentId,
            new HoldoutAccessRequest("researcher", "exploration", Start, new IdempotencyKey("access-1"), false));
        Assert.Equal(HoldoutAccessOutcome.Contaminated, contaminated.Audit.Outcome);
        Assert.Equal(HoldoutStatus.Contaminated, contaminated.Record.HoldoutStatus);

        var rejected = registry.AccessHoldout(registration.ExperimentId,
            new HoldoutAccessRequest("researcher", "retry", Start.AddMinutes(1), new IdempotencyKey("access-2"), true, "approval-1"));
        Assert.Equal(HoldoutAccessOutcome.RejectedAfterContamination, rejected.Audit.Outcome);
    }

    [Fact]
    public void ApprovedHoldoutAccessRequiresExplicitApprovalReference()
    {
        Assert.Throws<ArgumentException>(() => new HoldoutAccessRequest(
            "researcher", "evaluation", Start, new IdempotencyKey("access-1"), true));
    }

    [Fact]
    public void ExplicitContaminationRequiresReplacementVersionAndIsIdempotent()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = Registration();
        registry.Register(registration);

        var first = registry.ContaminateHoldout(registration.ExperimentId, "operator", "debug access", "holdout-v2", Start, new IdempotencyKey("contaminate-1"));
        var replay = registry.ContaminateHoldout(registration.ExperimentId, "operator", "debug access", "holdout-v2", Start, new IdempotencyKey("contaminate-1"));

        Assert.Equal("holdout-v2", first.ReplacementHoldoutVersion);
        Assert.Equal(first.HoldoutAccessCount, replay.HoldoutAccessCount);
        Assert.Throws<HoldoutContaminationException>(() => registry.ContaminateHoldout(registration.ExperimentId, "operator", "second", "holdout-v3", Start, new IdempotencyKey("contaminate-2")));
    }

    [Fact]
    public void PreregistrationValidatesTemporalWindowAndHashes()
    {
        Assert.Throws<ArgumentException>(() => Registration(datasetHash: "not-a-hash"));
        Assert.Throws<ArgumentException>(() => Registration(periodEnd: Start));
    }

    private static ExperimentPreregistration Registration(
        ExperimentId? experimentId = null,
        IdempotencyKey? idempotencyKey = null,
        string hypothesis = "trend persists after confirmation",
        int trialBudget = 3,
        string datasetHash = HashA,
        DateTimeOffset? periodEnd = null) =>
        new(
            experimentId ?? new ExperimentId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "family-1", hypothesis, "{\"ema_fast\":[20,30]}", ["baseline", "faster"], trialBudget,
            "net_return", "reject if OOS lower bound <= 0", "NO_TRADE/cash", Start, "researcher",
            idempotencyKey ?? new IdempotencyKey("registration-1"), "trend-v1", "commit-1", "dataset-v1", datasetHash,
            "holdout-v1", "metrics-v1", "environment-v1", HashC, 42, Start, periodEnd ?? Start.AddDays(30));

    private static ExperimentTrial Trial(int number, string variant, string idempotency) =>
        new(number, variant, "{\"ema_fast\":20}", HashB, ExperimentDecision.Rejected, Start, "researcher", new IdempotencyKey(idempotency));
}
