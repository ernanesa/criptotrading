using System.Security.Cryptography;
using System.Text;

namespace CryptoTrading.Domain.Research;

/// <summary>Lifecycle of a model/strategy artifact in the Continuous Learning registry (US-170).</summary>
public enum ModelLifecycleStatus
{
    Draft,
    Candidate,
    Active,
    Quarantined,
    Retired,
}

/// <summary>Immutable record representing a versioned model or strategy with complete lineage.</summary>
public sealed record StrategyModelRecord
{
    public StrategyModelRecord(
        string modelId,
        string strategyId,
        string version,
        string codeCommitSha,
        string datasetHash,
        string configurationFingerprint,
        ModelLifecycleStatus status,
        DateTimeOffset registeredAtUtc,
        string registeredBy,
        string? promotionJustification = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeCommitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredBy);
        if (registeredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Registration timestamp must be UTC.", nameof(registeredAtUtc));
        }

        ModelId = modelId.Trim();
        StrategyId = strategyId.Trim();
        Version = version.Trim();
        CodeCommitSha = codeCommitSha.Trim();
        DatasetHash = datasetHash.Trim();
        ConfigurationFingerprint = configurationFingerprint.Trim();
        Status = status;
        RegisteredAtUtc = registeredAtUtc;
        RegisteredBy = registeredBy.Trim();
        PromotionJustification = promotionJustification?.Trim();
    }

    public string ModelId { get; }
    public string StrategyId { get; }
    public string Version { get; }
    public string CodeCommitSha { get; }
    public string DatasetHash { get; }
    public string ConfigurationFingerprint { get; }
    public ModelLifecycleStatus Status { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; }
    public string RegisteredBy { get; }
    public string? PromotionJustification { get; init; }

    /// <summary>Canonical cryptographic identity of this registered artifact.</summary>
    public string CanonicalHash
    {
        get
        {
            var canonical = string.Join(
                '|',
                ModelId,
                StrategyId,
                Version,
                CodeCommitSha,
                DatasetHash,
                ConfigurationFingerprint,
                Status.ToString(),
                RegisteredAtUtc.ToUniversalTime().ToString("O"),
                RegisteredBy);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }
}

/// <summary>Boundary for registering and querying model artifacts with immutable provenance.</summary>
public interface IModelStrategyRegistry
{
    void RegisterModel(StrategyModelRecord record);
    StrategyModelRecord? GetModel(string modelId);
    StrategyModelRecord? GetActiveModel(string strategyId);
    StrategyModelRecord TransitionStatus(string modelId, ModelLifecycleStatus targetStatus, string actor, string justification, DateTimeOffset transitionAtUtc);
    IReadOnlyList<StrategyModelRecord> ListModels(string strategyId);
}

/// <summary>Thread-safe in-memory registry for local research and testing.</summary>
public sealed class InMemoryModelStrategyRegistry : IModelStrategyRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, StrategyModelRecord> modelsById = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterModel(StrategyModelRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            if (modelsById.ContainsKey(record.ModelId))
            {
                throw new InvalidOperationException($"Model with ID '{record.ModelId}' already exists.");
            }

            modelsById.Add(record.ModelId, record);
        }
    }

    public StrategyModelRecord? GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        lock (gate)
        {
            return modelsById.GetValueOrDefault(modelId);
        }
    }

    public StrategyModelRecord? GetActiveModel(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        lock (gate)
        {
            return modelsById.Values.FirstOrDefault(m =>
                string.Equals(m.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase) &&
                m.Status == ModelLifecycleStatus.Active);
        }
    }

    public StrategyModelRecord TransitionStatus(
        string modelId,
        ModelLifecycleStatus targetStatus,
        string actor,
        string justification,
        DateTimeOffset transitionAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        if (transitionAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition timestamp must be UTC.", nameof(transitionAtUtc));
        }

        lock (gate)
        {
            if (!modelsById.TryGetValue(modelId, out var current))
            {
                throw new KeyNotFoundException($"Model '{modelId}' was not found in registry.");
            }

            // Invariant: Activating a model automatically retires existing active model for that strategy
            if (targetStatus == ModelLifecycleStatus.Active)
            {
                var existingActive = modelsById.Values.FirstOrDefault(m =>
                    string.Equals(m.StrategyId, current.StrategyId, StringComparison.OrdinalIgnoreCase) &&
                    m.Status == ModelLifecycleStatus.Active &&
                    !string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

                if (existingActive is not null)
                {
                    var retiredExisting = existingActive with
                    {
                        Status = ModelLifecycleStatus.Retired,
                        PromotionJustification = $"Superseded by {modelId} on {transitionAtUtc:O} by {actor}"
                    };
                    modelsById[existingActive.ModelId] = retiredExisting;
                }
            }

            var updated = current with
            {
                Status = targetStatus,
                PromotionJustification = justification
            };

            modelsById[modelId] = updated;
            return updated;
        }
    }

    public IReadOnlyList<StrategyModelRecord> ListModels(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        lock (gate)
        {
            return modelsById.Values
                .Where(m => string.Equals(m.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.RegisteredAtUtc)
                .ToList();
        }
    }
}
