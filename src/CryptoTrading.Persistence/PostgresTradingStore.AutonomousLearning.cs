#pragma warning disable CA2007 // Npgsql async disposal/commands are intentionally awaited with ConfigureAwait(false) at each I/O boundary.
#pragma warning disable CA1849 // Synchronous interface implementations call async implementations via GetAwaiter().GetResult().
#pragma warning disable CA2100 // Constant query strings with parameterized values.

using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

public sealed partial class PostgresTradingStore : IMarketRegimeStore, IModelStrategyRegistryStore, IDriftObservationStore, IStrategyRegimePerformanceStore
{
    // ==========================================
    // IMarketRegimeStore
    // ==========================================

    public async Task RecordMarketRegimeAsync(MarketRegimeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var payloadJson = JsonSerializer.Serialize(snapshot, MarketRegimeJsonContext.Default.MarketRegimeSnapshot);

        const string sql = """
            INSERT INTO trading.market_regime_snapshots
                (symbol, observed_at, regime, confidence, trend_slope, volatility, reason, payload)
            VALUES
                (@symbol, @observed_at, @regime, @confidence, @trend_slope, @volatility, @reason, @payload::jsonb);
            """;

        await ExecuteAsync(connection, null, sql,
        [
            Parameter("symbol", NpgsqlDbType.Varchar, snapshot.Symbol.Value),
            Parameter("observed_at", NpgsqlDbType.TimestampTz, snapshot.ObservedAtUtc),
            Parameter("regime", NpgsqlDbType.Varchar, snapshot.Regime.ToString()),
            Parameter("confidence", NpgsqlDbType.Numeric, snapshot.Confidence),
            Parameter("trend_slope", NpgsqlDbType.Numeric, snapshot.TrendSlopeRate),
            Parameter("volatility", NpgsqlDbType.Numeric, snapshot.VolatilityRate),
            Parameter("reason", NpgsqlDbType.Varchar, snapshot.Reason),
            Parameter("payload", NpgsqlDbType.Jsonb, payloadJson),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketRegimeSnapshot?> GetLatestMarketRegimeAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT symbol, observed_at, regime, confidence, trend_slope, volatility, reason,
                   COALESCE((payload->>'hasCompleteMetrics')::boolean, false),
                   (payload->>'returnSigmaPerBar')::numeric, (payload->>'efficiencyRatio')::numeric,
                   (payload->>'legacyDispersionLimitBreached')::boolean,
                   (payload->>'appliedMaxReturnSigmaPerBar')::numeric,
                   payload ? 'appliedMaxReturnSigmaPerBar'
            FROM trading.market_regime_snapshots
            WHERE symbol = @symbol
            ORDER BY observed_at DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol.Value));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadMarketRegimeSnapshot(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<MarketRegimeSnapshot>> GetMarketRegimeHistoryAsync(Symbol symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT symbol, observed_at, regime, confidence, trend_slope, volatility, reason,
                   COALESCE((payload->>'hasCompleteMetrics')::boolean, false),
                   (payload->>'returnSigmaPerBar')::numeric, (payload->>'efficiencyRatio')::numeric,
                   (payload->>'legacyDispersionLimitBreached')::boolean,
                   (payload->>'appliedMaxReturnSigmaPerBar')::numeric,
                   payload ? 'appliedMaxReturnSigmaPerBar'
            FROM trading.market_regime_snapshots
            WHERE symbol = @symbol
            ORDER BY observed_at DESC
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol.Value));
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));

        var list = new List<MarketRegimeSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadMarketRegimeSnapshot(reader));
        }

        return list;
    }

    private static MarketRegimeSnapshot ReadMarketRegimeSnapshot(NpgsqlDataReader reader) =>
        new(new Symbol(reader.GetString(0)), Utc(reader.GetDateTime(1)),
            Enum.Parse<MarketRegime>(reader.GetString(2), ignoreCase: true),
            reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetString(6))
        {
            HasCompleteMetrics = reader.GetBoolean(7) && !reader.IsDBNull(8) && !reader.IsDBNull(9) && !reader.IsDBNull(10) && reader.GetBoolean(12),
            ReturnSigmaPerBar = reader.IsDBNull(8) ? 0m : reader.GetDecimal(8),
            EfficiencyRatio = reader.IsDBNull(9) ? 0m : reader.GetDecimal(9),
            LegacyDispersionLimitBreached = !reader.IsDBNull(10) && reader.GetBoolean(10),
            AppliedMaxReturnSigmaPerBar = reader.IsDBNull(11) ? null : reader.GetDecimal(11)
        };

    // ==========================================
    // IModelStrategyRegistryStore (Async & Sync)
    // ==========================================

    public void RegisterModel(StrategyModelRecord record) =>
        RegisterModelAsync(record).GetAwaiter().GetResult();

    public StrategyModelRecord? GetModel(string modelId) =>
        GetModelAsync(modelId).GetAwaiter().GetResult();

    public StrategyModelRecord? GetActiveModel(string strategyId) =>
        GetActiveModelAsync(strategyId).GetAwaiter().GetResult();

    public StrategyModelRecord TransitionStatus(string modelId, ModelLifecycleStatus targetStatus, string actor, string justification, DateTimeOffset transitionAtUtc) =>
        TransitionStatusAsync(modelId, targetStatus, actor, justification, transitionAtUtc).GetAwaiter().GetResult();

    public IReadOnlyList<StrategyModelRecord> ListModels(string strategyId) =>
        ListModelsAsync(strategyId).GetAwaiter().GetResult();

    public async Task RegisterModelAsync(StrategyModelRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        const string sql = """
            INSERT INTO trading.strategy_models
                (model_id, strategy_id, version, code_commit_sha, dataset_hash, configuration_fingerprint,
                 status, registered_at, registered_by, promotion_justification, canonical_hash)
            VALUES
                (@model_id, @strategy_id, @version, @code_commit_sha, @dataset_hash, @configuration_fingerprint,
                 @status, @registered_at, @registered_by, @promotion_justification, @canonical_hash)
            ON CONFLICT (model_id) DO NOTHING
            RETURNING model_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(new NpgsqlParameter[]
        {
            Parameter("model_id", NpgsqlDbType.Varchar, record.ModelId),
            Parameter("strategy_id", NpgsqlDbType.Varchar, record.StrategyId),
            Parameter("version", NpgsqlDbType.Varchar, record.Version),
            Parameter("code_commit_sha", NpgsqlDbType.Varchar, record.CodeCommitSha),
            Parameter("dataset_hash", NpgsqlDbType.Char, record.DatasetHash),
            Parameter("configuration_fingerprint", NpgsqlDbType.Varchar, record.ConfigurationFingerprint),
            Parameter("status", NpgsqlDbType.Varchar, record.Status.ToString()),
            Parameter("registered_at", NpgsqlDbType.TimestampTz, record.RegisteredAtUtc),
            Parameter("registered_by", NpgsqlDbType.Varchar, record.RegisteredBy),
            Parameter("promotion_justification", NpgsqlDbType.Varchar, record.PromotionJustification),
            Parameter("canonical_hash", NpgsqlDbType.Char, record.CanonicalHash),
        });
        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!inserted)
        {
            if (!await MatchesModelPayloadForUpdateAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Model identity collision for {record.ModelId}: existing provenance differs from the replay payload.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MatchesModelPayloadForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StrategyModelRecord record,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT canonical_hash = @canonical_hash
               AND strategy_id = @strategy_id
               AND version = @version
               AND code_commit_sha = @code_commit_sha
               AND dataset_hash = @dataset_hash
               AND configuration_fingerprint = @configuration_fingerprint
               AND status = @status
               AND registered_at = @registered_at
               AND registered_by = @registered_by
               AND promotion_justification IS NOT DISTINCT FROM @promotion_justification
            FROM trading.strategy_models
            WHERE model_id = @model_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(new NpgsqlParameter[]
        {
            Parameter("model_id", NpgsqlDbType.Varchar, record.ModelId),
            Parameter("strategy_id", NpgsqlDbType.Varchar, record.StrategyId),
            Parameter("version", NpgsqlDbType.Varchar, record.Version),
            Parameter("code_commit_sha", NpgsqlDbType.Varchar, record.CodeCommitSha),
            Parameter("dataset_hash", NpgsqlDbType.Char, record.DatasetHash),
            Parameter("configuration_fingerprint", NpgsqlDbType.Varchar, record.ConfigurationFingerprint),
            Parameter("status", NpgsqlDbType.Varchar, record.Status.ToString()),
            Parameter("registered_at", NpgsqlDbType.TimestampTz, record.RegisteredAtUtc),
            Parameter("registered_by", NpgsqlDbType.Varchar, record.RegisteredBy),
            Parameter("promotion_justification", NpgsqlDbType.Varchar, record.PromotionJustification),
            Parameter("canonical_hash", NpgsqlDbType.Char, record.CanonicalHash),
        });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<StrategyModelRecord?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT model_id, strategy_id, version, code_commit_sha, dataset_hash,
                   configuration_fingerprint, status, registered_at, registered_by, promotion_justification
            FROM trading.strategy_models
            WHERE model_id = @model_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("model_id", NpgsqlDbType.Varchar, modelId.Trim()));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MapStrategyModelRecord(reader);
        }

        return null;
    }

    public async Task<StrategyModelRecord?> GetActiveModelAsync(string strategyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT model_id, strategy_id, version, code_commit_sha, dataset_hash,
                   configuration_fingerprint, status, registered_at, registered_by, promotion_justification
            FROM trading.strategy_models
            WHERE strategy_id = @strategy_id AND status = 'Active'
            ORDER BY registered_at DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId.Trim()));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MapStrategyModelRecord(reader);
        }

        return null;
    }

    public async Task<StrategyModelRecord> TransitionStatusAsync(
        string modelId,
        ModelLifecycleStatus targetStatus,
        string actor,
        string justification,
        DateTimeOffset transitionAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        var existing = await GetModelForUpdateAsync(connection, transaction, modelId.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Model {modelId} was not found.");

        // If target is Active, demote current active models for this strategy to Retired
        if (targetStatus == ModelLifecycleStatus.Active)
        {
            const string retireSql = """
                UPDATE trading.strategy_models
                SET status = 'Retired', updated_at = @now
                WHERE strategy_id = @strategy_id AND status = 'Active' AND model_id <> @model_id;
                """;

            await ExecuteAsync(connection, transaction, retireSql,
            [
                Parameter("now", NpgsqlDbType.TimestampTz, transitionAtUtc),
                Parameter("strategy_id", NpgsqlDbType.Varchar, existing.StrategyId),
                Parameter("model_id", NpgsqlDbType.Varchar, existing.ModelId),
            ], cancellationToken).ConfigureAwait(false);
        }

        // Update model status
        const string updateSql = """
            UPDATE trading.strategy_models
            SET status = @status, promotion_justification = @justification, updated_at = @now
            WHERE model_id = @model_id;
            """;

        await ExecuteAsync(connection, transaction, updateSql,
        [
            Parameter("status", NpgsqlDbType.Varchar, targetStatus.ToString()),
            Parameter("justification", NpgsqlDbType.Varchar, justification),
            Parameter("now", NpgsqlDbType.TimestampTz, transitionAtUtc),
            Parameter("model_id", NpgsqlDbType.Varchar, existing.ModelId),
        ], cancellationToken).ConfigureAwait(false);

        // Record immutable audit transition
        const string transitionAuditSql = """
            INSERT INTO trading.strategy_model_transitions
                (model_id, previous_status, target_status, actor, justification, transitioned_at)
            VALUES
                (@model_id, @previous_status, @target_status, @actor, @justification, @transitioned_at);
            """;

        await ExecuteAsync(connection, transaction, transitionAuditSql,
        [
            Parameter("model_id", NpgsqlDbType.Varchar, existing.ModelId),
            Parameter("previous_status", NpgsqlDbType.Varchar, existing.Status.ToString()),
            Parameter("target_status", NpgsqlDbType.Varchar, targetStatus.ToString()),
            Parameter("actor", NpgsqlDbType.Varchar, actor),
            Parameter("justification", NpgsqlDbType.Varchar, justification),
            Parameter("transitioned_at", NpgsqlDbType.TimestampTz, transitionAtUtc),
        ], cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return existing with
        {
            Status = targetStatus,
            PromotionJustification = justification,
        };
    }

    public async Task<IReadOnlyList<StrategyModelRecord>> ListModelsAsync(string strategyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT model_id, strategy_id, version, code_commit_sha, dataset_hash,
                   configuration_fingerprint, status, registered_at, registered_by, promotion_justification
            FROM trading.strategy_models
            WHERE strategy_id = @strategy_id
            ORDER BY registered_at DESC;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId.Trim()));

        var list = new List<StrategyModelRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapStrategyModelRecord(reader));
        }

        return list;
    }

    private static async Task<StrategyModelRecord?> GetModelForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string modelId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT model_id, strategy_id, version, code_commit_sha, dataset_hash,
                   configuration_fingerprint, status, registered_at, registered_by, promotion_justification
            FROM trading.strategy_models
            WHERE model_id = @model_id
            FOR UPDATE;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(Parameter("model_id", NpgsqlDbType.Varchar, modelId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MapStrategyModelRecord(reader);
        }

        return null;
    }

    private static StrategyModelRecord MapStrategyModelRecord(NpgsqlDataReader reader)
    {
        var modelId = reader.GetString(0);
        var strategyId = reader.GetString(1);
        var version = reader.GetString(2);
        var codeCommit = reader.GetString(3);
        var datasetHash = reader.GetString(4);
        var configFingerprint = reader.GetString(5);
        var statusStr = reader.GetString(6);
        var registeredAt = reader.GetDateTime(7);
        var registeredBy = reader.GetString(8);
        var justification = reader.IsDBNull(9) ? null : reader.GetString(9);

        var status = Enum.Parse<ModelLifecycleStatus>(statusStr, ignoreCase: true);

        return new StrategyModelRecord(
            modelId,
            strategyId,
            version,
            codeCommit,
            datasetHash,
            configFingerprint,
            status,
            Utc(registeredAt),
            registeredBy,
            justification);
    }

    // ==========================================
    // IDriftObservationStore
    // ==========================================

    public async Task RecordDriftObservationAsync(DriftObservation observation, DriftEvaluationResult evaluation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(evaluation);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            INSERT INTO trading.drift_observations
                (strategy_id, drift_type, expected_value, observed_value, allowed_threshold,
                 absolute_deviation, relative_rate, severity, requires_quarantine, message, observed_at)
            VALUES
                (@strategy_id, @drift_type, @expected_value, @observed_value, @allowed_threshold,
                 @absolute_deviation, @relative_rate, @severity, @requires_quarantine, @message, @observed_at);
            """;

        await ExecuteAsync(connection, null, sql,
        [
            Parameter("strategy_id", NpgsqlDbType.Varchar, observation.StrategyId),
            Parameter("drift_type", NpgsqlDbType.Varchar, observation.Type.ToString()),
            Parameter("expected_value", NpgsqlDbType.Numeric, observation.ExpectedValue),
            Parameter("observed_value", NpgsqlDbType.Numeric, observation.ObservedValue),
            Parameter("allowed_threshold", NpgsqlDbType.Numeric, observation.AllowedDeviationThreshold),
            Parameter("absolute_deviation", NpgsqlDbType.Numeric, evaluation.AbsoluteDeviation),
            Parameter("relative_rate", NpgsqlDbType.Numeric, evaluation.RelativeDeviationRate),
            Parameter("severity", NpgsqlDbType.Varchar, evaluation.Severity.ToString()),
            Parameter("requires_quarantine", NpgsqlDbType.Boolean, evaluation.RequiresQuarantine),
            Parameter("message", NpgsqlDbType.Varchar, evaluation.Message),
            Parameter("observed_at", NpgsqlDbType.TimestampTz, observation.ObservedAtUtc),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DriftObservationRecord>> GetRecentDriftsAsync(string? strategyId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var sql = string.IsNullOrWhiteSpace(strategyId)
            ? """
              SELECT observation_id, strategy_id, drift_type, expected_value, observed_value, allowed_threshold,
                     absolute_deviation, relative_rate, severity, requires_quarantine, message, observed_at
              FROM trading.drift_observations
              ORDER BY observed_at DESC
              LIMIT @limit;
              """
            : """
              SELECT observation_id, strategy_id, drift_type, expected_value, observed_value, allowed_threshold,
                     absolute_deviation, relative_rate, severity, requires_quarantine, message, observed_at
              FROM trading.drift_observations
              WHERE strategy_id = @strategy_id
              ORDER BY observed_at DESC
              LIMIT @limit;
              """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("limit", NpgsqlDbType.Integer, limit));
        if (!string.IsNullOrWhiteSpace(strategyId))
        {
            command.Parameters.Add(Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId.Trim()));
        }

        var list = new List<DriftObservationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new DriftObservationRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                Enum.Parse<DriftType>(reader.GetString(2), ignoreCase: true),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                Enum.Parse<DriftSeverity>(reader.GetString(8), ignoreCase: true),
                reader.GetBoolean(9),
                reader.GetString(10),
                Utc(reader.GetDateTime(11))));
        }

        return list;
    }

    // ==========================================
    // IStrategyRegimePerformanceStore
    // ==========================================

    public async Task RecordStrategyRewardAsync(
        string strategyId,
        MarketRegime regime,
        decimal reward,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            INSERT INTO trading.strategy_regime_performances
                (strategy_id, regime, observation_count, cumulative_reward, average_reward, updated_at)
            VALUES
                (@strategy_id, @regime, 1, @reward, @reward, @updated_at)
            ON CONFLICT (strategy_id, regime) DO UPDATE
            SET observation_count = trading.strategy_regime_performances.observation_count + 1,
                cumulative_reward = trading.strategy_regime_performances.cumulative_reward + EXCLUDED.cumulative_reward,
                average_reward = (trading.strategy_regime_performances.cumulative_reward + EXCLUDED.cumulative_reward) /
                                 (trading.strategy_regime_performances.observation_count + 1),
                updated_at = EXCLUDED.updated_at;
            """;

        await ExecuteAsync(connection, null, sql,
        [
            Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId.Trim()),
            Parameter("regime", NpgsqlDbType.Varchar, regime.ToString()),
            Parameter("reward", NpgsqlDbType.Numeric, reward),
            Parameter("updated_at", NpgsqlDbType.TimestampTz, updatedAtUtc),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StrategyRegimePerformance>> GetPerformancesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT strategy_id, regime, observation_count, cumulative_reward, average_reward
            FROM trading.strategy_regime_performances
            ORDER BY strategy_id, regime;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var list = new List<StrategyRegimePerformance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new StrategyRegimePerformance(
                reader.GetString(0),
                Enum.Parse<MarketRegime>(reader.GetString(1), ignoreCase: true),
                reader.GetInt32(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4)));
        }

        return list;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MarketRegimeSnapshot))]
internal partial class MarketRegimeJsonContext : JsonSerializerContext;
