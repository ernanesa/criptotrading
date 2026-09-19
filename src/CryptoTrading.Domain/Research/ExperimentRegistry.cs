using CryptoTrading.Domain.Primitives;
using System.Security.Cryptography;
using System.Text;

namespace CryptoTrading.Domain.Research;

/// <summary>Decision recorded for a preregistered research trial.</summary>
public enum ExperimentDecision
{
    Rejected,
    Iterate,
    PaperEligible,
    ShadowEligible,
    CanaryEligible,
}

/// <summary>Lifecycle of the final holdout attached to an experiment.</summary>
public enum HoldoutStatus
{
    Locked,
    Contaminated,
}

/// <summary>Outcome of a holdout access attempt.</summary>
public enum HoldoutAccessOutcome
{
    Granted,
    Contaminated,
    RejectedAfterContamination,
}

public sealed class ExperimentRegistryConflictException : InvalidOperationException
{
    public ExperimentRegistryConflictException()
    {
    }

    public ExperimentRegistryConflictException(string message)
        : base(message)
    {
    }

    public ExperimentRegistryConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ExperimentTrialBudgetExceededException : InvalidOperationException
{
    public ExperimentTrialBudgetExceededException()
    {
    }

    public ExperimentTrialBudgetExceededException(string message)
        : base(message)
    {
    }

    public ExperimentTrialBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HoldoutContaminationException : InvalidOperationException
{
    public HoldoutContaminationException()
    {
    }

    public HoldoutContaminationException(string message)
        : base(message)
    {
    }

    public HoldoutContaminationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Immutable preregistration contract. Its digest is calculated before any
/// result exists and therefore binds the hypothesis, search space and budget
/// to the later experiment evidence.
/// </summary>
public sealed class ExperimentPreregistration
{
    public ExperimentPreregistration(
        ExperimentId experimentId,
        string experimentFamilyId,
        string hypothesis,
        string searchSpace,
        IEnumerable<string> plannedVariants,
        int trialBudget,
        string primaryMetric,
        string rejectionRule,
        string benchmark,
        DateTimeOffset preregisteredAtUtc,
        string preregisteredBy,
        IdempotencyKey idempotencyKey,
        string strategyVersion,
        string codeCommit,
        string datasetVersion,
        string datasetHash,
        string holdoutVersion,
        string metricDefinitionVersion,
        string environmentFingerprint,
        string dependencyLockHash,
        int seed,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc)
    {
        ExperimentId = experimentId;
        ExperimentFamilyId = RequireText(experimentFamilyId, nameof(experimentFamilyId), 200);
        Hypothesis = RequireText(hypothesis, nameof(hypothesis), 4_000);
        SearchSpace = RequireText(searchSpace, nameof(searchSpace), 16_000);
        PlannedVariants = RequireList(plannedVariants, nameof(plannedVariants), 10_000);
        TrialBudget = trialBudget > 0 ? trialBudget : throw new ArgumentOutOfRangeException(nameof(trialBudget));
        if (PlannedVariants.Count > trialBudget)
        {
            throw new ArgumentException("Planned variants cannot exceed the approved trial budget.", nameof(plannedVariants));
        }

        PrimaryMetric = RequireText(primaryMetric, nameof(primaryMetric), 500);
        RejectionRule = RequireText(rejectionRule, nameof(rejectionRule), 2_000);
        Benchmark = RequireText(benchmark, nameof(benchmark), 1_000);
        PreregisteredAtUtc = RequireUtc(preregisteredAtUtc, nameof(preregisteredAtUtc));
        PreregisteredBy = RequireText(preregisteredBy, nameof(preregisteredBy), 200);
        IdempotencyKey = idempotencyKey;
        StrategyVersion = RequireText(strategyVersion, nameof(strategyVersion), 200);
        CodeCommit = RequireText(codeCommit, nameof(codeCommit), 200);
        DatasetVersion = RequireText(datasetVersion, nameof(datasetVersion), 200);
        DatasetHash = RequireSha256(datasetHash, nameof(datasetHash));
        HoldoutVersion = RequireText(holdoutVersion, nameof(holdoutVersion), 200);
        MetricDefinitionVersion = RequireText(metricDefinitionVersion, nameof(metricDefinitionVersion), 200);
        EnvironmentFingerprint = RequireText(environmentFingerprint, nameof(environmentFingerprint), 500);
        DependencyLockHash = RequireSha256(dependencyLockHash, nameof(dependencyLockHash));
        Seed = seed;
        PeriodStartUtc = RequireUtc(periodStartUtc, nameof(periodStartUtc));
        PeriodEndUtc = RequireUtc(periodEndUtc, nameof(periodEndUtc));
        if (PeriodEndUtc <= PeriodStartUtc)
        {
            throw new ArgumentException("Experiment period must end after it starts.", nameof(periodEndUtc));
        }

        PreregistrationSha256 = ComputeHash();
    }

    public ExperimentId ExperimentId { get; }
    public string ExperimentFamilyId { get; }
    public string Hypothesis { get; }
    public string SearchSpace { get; }
    public IReadOnlyList<string> PlannedVariants { get; }
    public int TrialBudget { get; }
    public string PrimaryMetric { get; }
    public string RejectionRule { get; }
    public string Benchmark { get; }
    public DateTimeOffset PreregisteredAtUtc { get; }
    public string PreregisteredBy { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public string StrategyVersion { get; }
    public string CodeCommit { get; }
    public string DatasetVersion { get; }
    public string DatasetHash { get; }
    public string HoldoutVersion { get; }
    public string MetricDefinitionVersion { get; }
    public string EnvironmentFingerprint { get; }
    public string DependencyLockHash { get; }
    public int Seed { get; }
    public DateTimeOffset PeriodStartUtc { get; }
    public DateTimeOffset PeriodEndUtc { get; }
    public string PreregistrationSha256 { get; }

    private string ComputeHash()
    {
        var canonical = string.Join('\n',
            "experiment-preregistration-v1",
            ExperimentId.Value.ToString("D"),
            ExperimentFamilyId,
            Hypothesis,
            SearchSpace,
            string.Join('\u001f', PlannedVariants),
            TrialBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PrimaryMetric,
            RejectionRule,
            Benchmark,
            PreregisteredAtUtc.ToString("O"),
            PreregisteredBy,
            IdempotencyKey.Value,
            StrategyVersion,
            CodeCommit,
            DatasetVersion,
            DatasetHash,
            HoldoutVersion,
            MetricDefinitionVersion,
            EnvironmentFingerprint,
            DependencyLockHash,
            Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PeriodStartUtc.ToString("O"),
            PeriodEndUtc.ToString("O"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string RequireText(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} cannot exceed {maxLength} characters.");
        }

        return normalized;
    }

    internal static IReadOnlyList<string> RequireList(IEnumerable<string>? values, string parameterName, int maxItemLength)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.Select(value => RequireText(value, parameterName, maxItemLength)).ToArray();
        if (result.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new ArgumentException("Values must be unique.", parameterName);
        }

        return result;
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("Timestamps must be UTC.", parameterName);

    internal static string RequireSha256(string? value, string parameterName)
    {
        var normalized = RequireText(value, parameterName, 64).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Value must be a SHA-256 hexadecimal digest.", parameterName);
        }

        return normalized;
    }
}

/// <summary>One counted trial. Rejected and discarded variants remain trials.</summary>
public sealed class ExperimentTrial
{
    public ExperimentTrial(
        int trialNumber,
        string variantId,
        string parametersCanonical,
        string resultArtifactHash,
        ExperimentDecision decision,
        DateTimeOffset recordedAtUtc,
        string recordedBy,
        IdempotencyKey idempotencyKey)
    {
        TrialNumber = trialNumber > 0 ? trialNumber : throw new ArgumentOutOfRangeException(nameof(trialNumber));
        VariantId = ExperimentPreregistration.RequireText(variantId, nameof(variantId), 200);
        ParametersCanonical = ExperimentPreregistration.RequireText(parametersCanonical, nameof(parametersCanonical), 16_000);
        ResultArtifactHash = ExperimentPreregistration.RequireSha256(resultArtifactHash, nameof(resultArtifactHash));
        Decision = decision;
        RecordedAtUtc = ExperimentPreregistration.RequireUtc(recordedAtUtc, nameof(recordedAtUtc));
        RecordedBy = ExperimentPreregistration.RequireText(recordedBy, nameof(recordedBy), 200);
        IdempotencyKey = idempotencyKey;
        TrialSha256 = ComputeHash();
    }

    public int TrialNumber { get; }
    public string VariantId { get; }
    public string ParametersCanonical { get; }
    public string ResultArtifactHash { get; }
    public ExperimentDecision Decision { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string RecordedBy { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public string TrialSha256 { get; }

    private string ComputeHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        "experiment-trial-v1", TrialNumber, VariantId, ParametersCanonical, ResultArtifactHash,
        Decision, RecordedAtUtc.ToString("O"), RecordedBy, IdempotencyKey.Value))));
}

public sealed class HoldoutAccessRequest
{
    public HoldoutAccessRequest(
        string actor,
        string purpose,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey,
        bool approvedByPolicy,
        string? approvalReference = null)
    {
        Actor = ExperimentPreregistration.RequireText(actor, nameof(actor), 200);
        Purpose = ExperimentPreregistration.RequireText(purpose, nameof(purpose), 1_000);
        OccurredAtUtc = ExperimentPreregistration.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        IdempotencyKey = idempotencyKey;
        ApprovedByPolicy = approvedByPolicy;
        ApprovalReference = approvalReference is null ? null : ExperimentPreregistration.RequireText(approvalReference, nameof(approvalReference), 200);
        if (approvedByPolicy && ApprovalReference is null)
        {
            throw new ArgumentException("An approved holdout access requires an approval reference.", nameof(approvalReference));
        }
    }

    public string Actor { get; }
    public string Purpose { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public bool ApprovedByPolicy { get; }
    public string? ApprovalReference { get; }
}

public sealed class HoldoutAccessAudit
{
    internal HoldoutAccessAudit(
        ExperimentId experimentId,
        string holdoutVersion,
        HoldoutAccessRequest request,
        HoldoutAccessOutcome outcome,
        string? contaminationReason)
    {
        EventId = Guid.NewGuid();
        ExperimentId = experimentId;
        HoldoutVersion = holdoutVersion;
        Actor = request.Actor;
        Purpose = request.Purpose;
        OccurredAtUtc = request.OccurredAtUtc;
        IdempotencyKey = request.IdempotencyKey;
        ApprovedByPolicy = request.ApprovedByPolicy;
        ApprovalReference = request.ApprovalReference;
        Outcome = outcome;
        ContaminationReason = contaminationReason;
        PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            "holdout-access-v1", EventId, ExperimentId.Value, HoldoutVersion, Actor, Purpose,
            OccurredAtUtc.ToString("O"), IdempotencyKey.Value, ApprovedByPolicy,
            ApprovalReference ?? string.Empty, Outcome, ContaminationReason ?? string.Empty))));
    }

    public Guid EventId { get; }
    public ExperimentId ExperimentId { get; }
    public string HoldoutVersion { get; }
    public string Actor { get; }
    public string Purpose { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public bool ApprovedByPolicy { get; }
    public string? ApprovalReference { get; }
    public HoldoutAccessOutcome Outcome { get; }
    public string? ContaminationReason { get; }
    public string PayloadSha256 { get; }
}

public sealed class ExperimentRecord
{
    internal ExperimentRecord(
        ExperimentPreregistration preregistration,
        IReadOnlyList<ExperimentTrial> trials,
        IReadOnlyList<HoldoutAccessAudit> holdoutAccesses,
        HoldoutStatus holdoutStatus,
        string? contaminationReason,
        string? replacementHoldoutVersion)
    {
        Preregistration = preregistration;
        Trials = trials;
        HoldoutAccesses = holdoutAccesses;
        HoldoutStatus = holdoutStatus;
        ContaminationReason = contaminationReason;
        ReplacementHoldoutVersion = replacementHoldoutVersion;
    }

    public ExperimentPreregistration Preregistration { get; }
    public IReadOnlyList<ExperimentTrial> Trials { get; }
    public IReadOnlyList<HoldoutAccessAudit> HoldoutAccesses { get; }
    public HoldoutStatus HoldoutStatus { get; }
    public string? ContaminationReason { get; }
    public string? ReplacementHoldoutVersion { get; }
    public int TrialCount => Trials.Count;
    public int HoldoutAccessCount => HoldoutAccesses.Count;
    public bool IsPromotionEligible => HoldoutStatus == HoldoutStatus.Locked && Trials.Count > 0;
}

public sealed record ExperimentRegistrationResult(ExperimentRecord Record, bool IsReplay);
public sealed record ExperimentTrialResult(ExperimentRecord Record, ExperimentTrial Trial, bool IsReplay);
public sealed record HoldoutAccessResult(ExperimentRecord Record, HoldoutAccessAudit Audit, bool IsReplay);

public interface IExperimentRegistry
{
    ExperimentRegistrationResult Register(ExperimentPreregistration preregistration);
    ExperimentTrialResult RecordTrial(ExperimentId experimentId, ExperimentTrial trial);
    HoldoutAccessResult AccessHoldout(ExperimentId experimentId, HoldoutAccessRequest request);
    ExperimentRecord ContaminateHoldout(
        ExperimentId experimentId,
        string actor,
        string reason,
        string replacementHoldoutVersion,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey);
    ExperimentRecord GetById(ExperimentId experimentId);
}

/// <summary>
/// Thread-safe local registry. It is intentionally in-memory and append-oriented;
/// persistence adapters must preserve the same hashes, idempotency and transitions.
/// </summary>
public sealed class InMemoryExperimentRegistry : IExperimentRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<ExperimentId, Entry> entries = [];
    private readonly Dictionary<IdempotencyKey, ExperimentId> registrationKeys = [];

    public ExperimentRegistrationResult Register(ExperimentPreregistration preregistration)
    {
        ArgumentNullException.ThrowIfNull(preregistration);
        lock (gate)
        {
            if (entries.TryGetValue(preregistration.ExperimentId, out var existing))
            {
                if (existing.Persisted.Preregistration.PreregistrationSha256 != preregistration.PreregistrationSha256)
                {
                    throw new ExperimentRegistryConflictException("Experiment ID is already registered with a different preregistration.");
                }

                return new(Clone(existing), true);
            }

            if (registrationKeys.TryGetValue(preregistration.IdempotencyKey, out var otherId) && otherId != preregistration.ExperimentId)
            {
                throw new ExperimentRegistryConflictException("Registration idempotency key was already used by another experiment.");
            }

            var entry = new Entry(preregistration);
            entries.Add(preregistration.ExperimentId, entry);
            registrationKeys.Add(preregistration.IdempotencyKey, preregistration.ExperimentId);
            return new(Clone(entry), false);
        }
    }

    public ExperimentTrialResult RecordTrial(ExperimentId experimentId, ExperimentTrial trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        lock (gate)
        {
            var entry = Find(experimentId);
            if (entry.TrialsByKey.TryGetValue(trial.IdempotencyKey, out var replay))
            {
                EnsureSameTrial(replay, trial);
                return new(Clone(entry), replay, true);
            }

            if (entry.Trials.Count >= entry.Persisted.Preregistration.TrialBudget)
            {
                throw new ExperimentTrialBudgetExceededException(
                    $"Experiment trial budget of {entry.Persisted.Preregistration.TrialBudget} has been exhausted.");
            }

            if (trial.TrialNumber != entry.Trials.Count + 1)
            {
                throw new ExperimentRegistryConflictException("Trial numbers must be contiguous and every trial must be counted.");
            }

            if (entry.Trials.Any(item => item.TrialNumber == trial.TrialNumber))
            {
                throw new ExperimentRegistryConflictException("Trial number was already recorded with different evidence.");
            }

            entry.Trials.Add(trial);
            entry.TrialsByKey.Add(trial.IdempotencyKey, trial);
            return new(Clone(entry), trial, false);
        }
    }

    public HoldoutAccessResult AccessHoldout(ExperimentId experimentId, HoldoutAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var entry = Find(experimentId);
            if (entry.AccessesByKey.TryGetValue(request.IdempotencyKey, out var replay))
            {
                return new(Clone(entry), replay, true);
            }

            HoldoutAccessOutcome outcome;
            string? contaminationReason = null;
            if (entry.HoldoutStatus == HoldoutStatus.Contaminated)
            {
                outcome = HoldoutAccessOutcome.RejectedAfterContamination;
            }
            else if (!request.ApprovedByPolicy)
            {
                outcome = HoldoutAccessOutcome.Contaminated;
                contaminationReason = $"Unauthorized holdout access by {request.Actor}: {request.Purpose}";
                ContaminateLocked(entry, request.Actor, contaminationReason, replacementVersion: null);
            }
            else
            {
                outcome = HoldoutAccessOutcome.Granted;
            }

            var audit = new HoldoutAccessAudit(
                experimentId,
                entry.Persisted.Preregistration.HoldoutVersion,
                request,
                outcome,
                contaminationReason ?? entry.ContaminationReason);
            entry.Accesses.Add(audit);
            entry.AccessesByKey.Add(request.IdempotencyKey, audit);
            return new(Clone(entry), audit, false);
        }
    }

    public ExperimentRecord ContaminateHoldout(
        ExperimentId experimentId,
        string actor,
        string reason,
        string replacementHoldoutVersion,
        DateTimeOffset occurredAtUtc,
        IdempotencyKey idempotencyKey)
    {
        var request = new HoldoutAccessRequest(actor, "contamination-control", occurredAtUtc, idempotencyKey, false);
        lock (gate)
        {
            var entry = Find(experimentId);
            if (entry.ContaminationCommands.TryGetValue(idempotencyKey, out var replay))
            {
                if (!string.Equals(replay.Reason, reason, StringComparison.Ordinal) ||
                    !string.Equals(replay.ReplacementVersion, replacementHoldoutVersion, StringComparison.Ordinal))
                {
                    throw new ExperimentRegistryConflictException("Contamination idempotency key was reused with different data.");
                }

                return Clone(entry);
            }

            var normalizedReason = ExperimentPreregistration.RequireText(reason, nameof(reason), 2_000);
            var normalizedReplacement = ExperimentPreregistration.RequireText(replacementHoldoutVersion, nameof(replacementHoldoutVersion), 200);
            if (entry.HoldoutStatus == HoldoutStatus.Contaminated)
            {
                throw new HoldoutContaminationException("A contaminated holdout is irreversible and cannot be replaced in place.");
            }

            ContaminateLocked(entry, actor, normalizedReason, normalizedReplacement);
            entry.ContaminationCommands.Add(idempotencyKey, new ContaminationCommand(normalizedReason, normalizedReplacement));
            return Clone(entry);
        }
    }

    public ExperimentRecord GetById(ExperimentId experimentId)
    {
        lock (gate)
        {
            return Clone(Find(experimentId));
        }
    }

    private Entry Find(ExperimentId id) => entries.TryGetValue(id, out var entry)
        ? entry
        : throw new KeyNotFoundException($"Experiment {id.Value:D} is not registered.");

    private static void EnsureSameTrial(ExperimentTrial existing, ExperimentTrial candidate)
    {
        if (existing.TrialSha256 != candidate.TrialSha256)
        {
            throw new ExperimentRegistryConflictException("Trial idempotency key was reused with different evidence.");
        }
    }

    private static void ContaminateLocked(Entry entry, string actor, string reason, string? replacementVersion)
    {
        if (entry.HoldoutStatus == HoldoutStatus.Contaminated)
        {
            return;
        }

        entry.HoldoutStatus = HoldoutStatus.Contaminated;
        entry.ContaminationReason = ExperimentPreregistration.RequireText(reason, nameof(reason), 2_000);
        entry.ReplacementHoldoutVersion = replacementVersion is null
            ? null
            : ExperimentPreregistration.RequireText(replacementVersion, nameof(replacementVersion), 200);
        entry.ContaminatedBy = ExperimentPreregistration.RequireText(actor, nameof(actor), 200);
    }

    private static ExperimentRecord Clone(Entry entry) => new(
        entry.Persisted.Preregistration,
        entry.Trials.ToArray(),
        entry.Accesses.ToArray(),
        entry.HoldoutStatus,
        entry.ContaminationReason,
        entry.ReplacementHoldoutVersion);

    private sealed class Entry
    {
        public Entry(ExperimentPreregistration preregistration) => Persisted = new(
            preregistration, [], [], HoldoutStatus.Locked, null, null);

        public ExperimentRecord Persisted { get; }
        public List<ExperimentTrial> Trials { get; } = [];
        public Dictionary<IdempotencyKey, ExperimentTrial> TrialsByKey { get; } = [];
        public List<HoldoutAccessAudit> Accesses { get; } = [];
        public Dictionary<IdempotencyKey, HoldoutAccessAudit> AccessesByKey { get; } = [];
        public Dictionary<IdempotencyKey, ContaminationCommand> ContaminationCommands { get; } = [];
        public HoldoutStatus HoldoutStatus { get; set; } = HoldoutStatus.Locked;
        public string? ContaminationReason { get; set; }
        public string? ReplacementHoldoutVersion { get; set; }
        public string? ContaminatedBy { get; set; }
    }

    private sealed record ContaminationCommand(string Reason, string ReplacementVersion);
}
