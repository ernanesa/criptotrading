using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Backtesting;

public enum ExperimentStatus
{
    Registered,
    Running,
    Completed,
    Rejected,
    Contaminated
}

public sealed record ExperimentRegistration
{
    public ExperimentRegistration(
        string experimentFamilyId,
        string experimentId,
        string hypothesis,
        string strategyVersion,
        string datasetHash,
        string holdoutDatasetHash,
        string searchSpaceJson,
        int trialBudget,
        string primaryMetric,
        string benchmark,
        string rejectionRule,
        DateTimeOffset preregisteredAtUtc,
        string metricDefinitionVersion,
        string configurationHash)
    {
        ExperimentFamilyId = Required(experimentFamilyId, nameof(experimentFamilyId));
        ExperimentId = Required(experimentId, nameof(experimentId));
        Hypothesis = Required(hypothesis, nameof(hypothesis));
        StrategyVersion = Required(strategyVersion, nameof(strategyVersion));
        DatasetHash = Sha256(datasetHash, nameof(datasetHash));
        HoldoutDatasetHash = Sha256(holdoutDatasetHash, nameof(holdoutDatasetHash));
        if (string.Equals(DatasetHash, HoldoutDatasetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Holdout must be a distinct dataset.", nameof(holdoutDatasetHash));
        }

        SearchSpaceJson = Required(searchSpaceJson, nameof(searchSpaceJson));
        try
        {
            using var document = JsonDocument.Parse(SearchSpaceJson);
            SearchSpaceJson = JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Search space must be valid JSON.", nameof(searchSpaceJson), exception);
        }

        if (trialBudget is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(trialBudget), "Trial budget must be between 1 and 1,000,000.");
        }

        TrialBudget = trialBudget;
        PrimaryMetric = Required(primaryMetric, nameof(primaryMetric));
        Benchmark = Required(benchmark, nameof(benchmark));
        RejectionRule = Required(rejectionRule, nameof(rejectionRule));
        if (preregisteredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Preregistration timestamp must be UTC.", nameof(preregisteredAtUtc));
        }

        PreregisteredAtUtc = preregisteredAtUtc;
        MetricDefinitionVersion = Required(metricDefinitionVersion, nameof(metricDefinitionVersion));
        ConfigurationHash = Sha256(configurationHash, nameof(configurationHash));
        Status = ExperimentStatus.Registered;
    }

    public string ExperimentFamilyId { get; }
    public string ExperimentId { get; }
    public string Hypothesis { get; }
    public string StrategyVersion { get; }
    public string DatasetHash { get; }
    public string HoldoutDatasetHash { get; }
    public string SearchSpaceJson { get; }
    public int TrialBudget { get; }
    public string PrimaryMetric { get; }
    public string Benchmark { get; }
    public string RejectionRule { get; }
    public DateTimeOffset PreregisteredAtUtc { get; }
    public string MetricDefinitionVersion { get; }
    public string ConfigurationHash { get; }
    public ExperimentStatus Status { get; internal set; }
    public int TrialCount { get; internal set; }
    public int HoldoutAccessCount { get; internal set; }
    public string RegistrationHash => ComputeHash(new
    {
        ExperimentFamilyId, ExperimentId, Hypothesis, StrategyVersion, DatasetHash,
        HoldoutDatasetHash, SearchSpaceJson, TrialBudget, PrimaryMetric, Benchmark,
        RejectionRule, PreregisteredAtUtc, MetricDefinitionVersion, ConfigurationHash
    });

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static string Sha256(string value, string name)
    {
        var normalized = Required(value, name).ToUpperInvariant();
        try
        {
            if (Convert.FromHexString(normalized).Length != SHA256.HashSizeInBytes)
            {
                throw new FormatException();
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Value must be a SHA-256 hexadecimal string.", name, exception);
        }

        return normalized;
    }

    private static string ComputeHash(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

public sealed record ExperimentTrial(
    string ExperimentId,
    int TrialNumber,
    string VariantHash,
    string Decision,
    DateTimeOffset RecordedAtUtc);

public sealed record HoldoutAccessAudit(
    string ExperimentId,
    int AccessNumber,
    string Actor,
    string Reason,
    bool Authorized,
    DateTimeOffset AccessedAtUtc,
    string HoldoutDatasetHash);

/// <summary>
/// Process-local append-only governance boundary. Persistence adapters can
/// replay these contracts later, but no trial or holdout access bypasses it.
/// </summary>
public sealed class ExperimentRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, ExperimentRegistration> registrations = new(StringComparer.Ordinal);
    private readonly List<ExperimentTrial> trials = [];
    private readonly List<HoldoutAccessAudit> holdoutAudits = [];

    public ExperimentRegistration Register(ExperimentRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (gate)
        {
            if (registrations.TryGetValue(registration.ExperimentId, out var existing))
            {
                if (!string.Equals(existing.RegistrationHash, registration.RegistrationHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Experiment identity collision with different preregistration content.");
                }

                return existing;
            }

            registrations.Add(registration.ExperimentId, registration);
            return registration;
        }
    }

    /// <summary>Registers a temporal replacement after holdout contamination.</summary>
    public ExperimentRegistration RegisterReplacement(string contaminatedExperimentId, ExperimentRegistration replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (gate)
        {
            var previous = Get(contaminatedExperimentId);
            if (previous.Status != ExperimentStatus.Contaminated)
            {
                throw new InvalidOperationException("A replacement requires an explicitly contaminated experiment.");
            }

            if (!string.Equals(previous.ExperimentFamilyId, replacement.ExperimentFamilyId, StringComparison.Ordinal) ||
                string.Equals(previous.ExperimentId, replacement.ExperimentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Replacement must remain in the same family with a new experiment identity.");
            }

            if (replacement.PreregisteredAtUtc <= previous.PreregisteredAtUtc ||
                string.Equals(previous.HoldoutDatasetHash, replacement.HoldoutDatasetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Replacement must use a later preregistration timestamp and a new holdout version.");
            }

            return Register(replacement);
        }
    }

    public ExperimentTrial RecordTrial(string experimentId, string variantHash, string decision, DateTimeOffset recordedAtUtc)
    {
        var variant = Required(variantHash, nameof(variantHash));
        var normalizedDecision = Required(decision, nameof(decision));
        if (recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Trial timestamp must be UTC.", nameof(recordedAtUtc));
        }

        lock (gate)
        {
            var registration = Get(experimentId);
            if (registration.Status is ExperimentStatus.Contaminated or ExperimentStatus.Rejected)
            {
                throw new InvalidOperationException("Contaminated or rejected experiments cannot record trials.");
            }

            if (registration.TrialCount >= registration.TrialBudget)
            {
                throw new InvalidOperationException("Experiment trial budget is exhausted.");
            }

            registration.Status = ExperimentStatus.Running;
            var trial = new ExperimentTrial(registration.ExperimentId, ++registration.TrialCount, variant, normalizedDecision, recordedAtUtc);
            trials.Add(trial);
            return trial;
        }
    }

    public HoldoutAccessAudit AccessHoldout(string experimentId, string actor, string reason, bool authorized, DateTimeOffset accessedAtUtc)
    {
        if (accessedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Holdout access timestamp must be UTC.", nameof(accessedAtUtc));
        }

        lock (gate)
        {
            var registration = Get(experimentId);
            if (registration.Status == ExperimentStatus.Contaminated)
            {
                throw new InvalidOperationException("Contaminated holdout cannot be accessed.");
            }

            var audit = new HoldoutAccessAudit(registration.ExperimentId, ++registration.HoldoutAccessCount,
                Required(actor, nameof(actor)), Required(reason, nameof(reason)), authorized, accessedAtUtc, registration.HoldoutDatasetHash);
            holdoutAudits.Add(audit);
            if (!authorized)
            {
                registration.Status = ExperimentStatus.Contaminated;
            }

            return audit;
        }
    }

    public ExperimentRegistration MarkCompleted(string experimentId, bool accepted)
    {
        lock (gate)
        {
            var registration = Get(experimentId);
            if (registration.Status == ExperimentStatus.Contaminated)
            {
                throw new InvalidOperationException("Contaminated experiment cannot be completed.");
            }

            registration.Status = accepted ? ExperimentStatus.Completed : ExperimentStatus.Rejected;
            return registration;
        }
    }

    public IReadOnlyList<ExperimentTrial> TrialsFor(string experimentId)
    {
        lock (gate)
        {
            return trials.Where(trial => string.Equals(trial.ExperimentId, experimentId, StringComparison.Ordinal)).ToArray();
        }
    }

    public IReadOnlyList<HoldoutAccessAudit> HoldoutAuditsFor(string experimentId)
    {
        lock (gate)
        {
            return holdoutAudits.Where(audit => string.Equals(audit.ExperimentId, experimentId, StringComparison.Ordinal)).ToArray();
        }
    }

    private ExperimentRegistration Get(string experimentId) =>
        registrations.TryGetValue(experimentId, out var registration)
            ? registration
            : throw new KeyNotFoundException($"Experiment '{experimentId}' is not preregistered.");

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
