using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Domain.Tests.Research;

public sealed class StrictHoldoutLockTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void AccessHoldoutStrictAllowsFirstApprovedEvaluation()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = CreateRegistration();
        registry.Register(registration);

        var request = new HoldoutAccessRequest(
            "lead-researcher",
            "final-holdout-eval",
            Start,
            new IdempotencyKey("strict-eval-1"),
            approvedByPolicy: true,
            approvalReference: "gov-board-approval-42");

        var result = registry.AccessHoldoutStrict(registration.ExperimentId, request);

        Assert.False(result.IsReplay);
        Assert.Equal(HoldoutAccessOutcome.Granted, result.Audit.Outcome);
        Assert.Equal(HoldoutStatus.Locked, result.Record.HoldoutStatus);
        Assert.Equal(1, result.Record.HoldoutAccessCount);
    }

    [Fact]
    public void AccessHoldoutStrictRejectsSecondEvaluationWithException()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = CreateRegistration();
        registry.Register(registration);

        var firstRequest = new HoldoutAccessRequest(
            "lead-researcher",
            "final-holdout-eval",
            Start,
            new IdempotencyKey("strict-eval-1"),
            approvedByPolicy: true,
            approvalReference: "gov-board-approval-42");
        registry.AccessHoldoutStrict(registration.ExperimentId, firstRequest);

        var secondRequest = new HoldoutAccessRequest(
            "lead-researcher",
            "peeking-at-holdout-again",
            Start.AddHours(1),
            new IdempotencyKey("strict-eval-2"),
            approvedByPolicy: true,
            approvalReference: "gov-board-approval-43");

        var ex = Assert.Throws<HoldoutAlreadyEvaluatedException>(() =>
            registry.AccessHoldoutStrict(registration.ExperimentId, secondRequest));
        Assert.Contains("already been evaluated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessHoldoutStrictIsIdempotentForExactReplay()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = CreateRegistration();
        registry.Register(registration);

        var request = new HoldoutAccessRequest(
            "lead-researcher",
            "final-holdout-eval",
            Start,
            new IdempotencyKey("strict-eval-1"),
            approvedByPolicy: true,
            approvalReference: "gov-board-approval-42");

        var first = registry.AccessHoldoutStrict(registration.ExperimentId, request);
        var replay = registry.AccessHoldoutStrict(registration.ExperimentId, request);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Audit.PayloadSha256, replay.Audit.PayloadSha256);
    }

    [Fact]
    public void AccessHoldoutStrictFailsClosedOnContaminatedHoldout()
    {
        var registry = new InMemoryExperimentRegistry();
        var registration = CreateRegistration();
        registry.Register(registration);

        // Unauthorized access contaminates the holdout
        var unapprovedRequest = new HoldoutAccessRequest(
            "junior-dev",
            "curious-peek",
            Start,
            new IdempotencyKey("unauth-1"),
            approvedByPolicy: false);
        registry.AccessHoldout(registration.ExperimentId, unapprovedRequest);

        var strictRequest = new HoldoutAccessRequest(
            "lead-researcher",
            "attempted-strict-eval",
            Start.AddMinutes(5),
            new IdempotencyKey("strict-after-contam"),
            approvedByPolicy: true,
            approvalReference: "board-approval-99");

        var ex = Assert.Throws<HoldoutContaminationException>(() =>
            registry.AccessHoldoutStrict(registration.ExperimentId, strictRequest));
        Assert.Contains("contaminated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExperimentPreregistration CreateRegistration(int trialBudget = 5) => new(
        ExperimentId.New(),
        "holdout-governance-family",
        "Strict holdout access is locked to exactly one evaluation",
        "variants-space",
        ["variant-a", "variant-b"],
        trialBudget,
        "NetReturn",
        "MaxDrawdown > 0.15",
        "BuyAndHold",
        Start,
        "researcher-1",
        new IdempotencyKey("reg-holdout-1"),
        "1.0.0",
        "commit-sha",
        "dataset-v1",
        HashA,
        "holdout-v1",
        "metrics-v1",
        "env-fingerprint",
        HashB,
        seed: 42,
        Start,
        Start.AddDays(30));
}

