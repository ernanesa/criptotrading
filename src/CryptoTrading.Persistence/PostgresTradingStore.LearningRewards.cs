#pragma warning disable CA2007 // Npgsql async boundaries use ConfigureAwait(false).
#pragma warning disable CA2100 // SQL is constant and all values are parameters.

using System.Data;
using CryptoTrading.Domain.Regimes;
using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

public sealed partial class PostgresTradingStore : IExecutionLearningRewardStore
{
    public async Task<ExecutionLearningRewardResult> RecordExecutionRewardAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        var normalizedExecutionId = executionId.Trim();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        const string candidateSql = """
            SELECT d.decision_id, d.strategy_id, d.strategy_reason,
                   round(f.realized_pnl_quote / NULLIF(f.quantity * f.price, 0), 18)
            FROM trading.execution_fills f
            JOIN trading.execution_submission_context c ON c.client_order_id = f.client_order_id
            JOIN trading.autonomy_decisions d ON d.outbox_message_id = c.outbox_message_id
            WHERE f.execution_id = @execution_id
              AND d.outcome = 'DISPATCHABLE'
              AND d.symbol = f.symbol
              AND c.symbol = f.symbol
              AND c.side = f.side
              AND f.realized_pnl_quote IS NOT NULL
              AND f.fee_comparison_status = 'WITHIN_TOLERANCE'
              AND f.quote_asset = 'USDT'
              AND f.commission_asset = f.quote_asset;
            """;

        string decisionId;
        string strategyId;
        MarketRegime regime;
        decimal reward;
        await using (var candidateCommand = new NpgsqlCommand(candidateSql, connection, transaction))
        {
            candidateCommand.Parameters.Add(Parameter("execution_id", NpgsqlDbType.Varchar, normalizedExecutionId));
            await using var reader = await candidateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.CloseAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(false, false, "EXECUTION_ATTRIBUTION_REALIZED_PNL_OR_FEE_INCOMPLETE");
            }

            decisionId = reader.GetString(0);
            strategyId = reader.GetString(1);
            if (!Enum.TryParse(reader.GetString(2), ignoreCase: true, out regime) ||
                !Enum.IsDefined(regime))
            {
                await reader.CloseAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(false, false, "DECISION_REGIME_UNAVAILABLE");
            }

            reward = reader.GetDecimal(3);
        }

        const string insertSql = """
            INSERT INTO trading.execution_learning_rewards
                (execution_id, decision_id, strategy_id, regime, reward)
            VALUES (@execution_id, @decision_id, @strategy_id, @regime, @reward)
            ON CONFLICT (execution_id) DO NOTHING
            RETURNING execution_id;
            """;

        bool inserted;
        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.Add(Parameter("execution_id", NpgsqlDbType.Varchar, normalizedExecutionId));
            insertCommand.Parameters.Add(Parameter("decision_id", NpgsqlDbType.Varchar, decisionId));
            insertCommand.Parameters.Add(Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId));
            insertCommand.Parameters.Add(Parameter("regime", NpgsqlDbType.Varchar, regime.ToString()));
            insertCommand.Parameters.Add(Parameter("reward", NpgsqlDbType.Numeric, reward));
            inserted = await insertCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        if (!inserted)
        {
            const string existingSql = """
                SELECT decision_id, strategy_id, regime, reward
                FROM trading.execution_learning_rewards
                WHERE execution_id = @execution_id;
                """;
            await using var existingCommand = new NpgsqlCommand(existingSql, connection, transaction);
            existingCommand.Parameters.Add(Parameter("execution_id", NpgsqlDbType.Varchar, normalizedExecutionId));
            await using var existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(existingReader.GetString(0), decisionId, StringComparison.Ordinal) ||
                !string.Equals(existingReader.GetString(1), strategyId, StringComparison.Ordinal) ||
                !string.Equals(existingReader.GetString(2), regime.ToString(), StringComparison.OrdinalIgnoreCase) ||
                existingReader.GetDecimal(3) != reward)
            {
                throw new InvalidOperationException("Execution reward replay conflicts with previously recorded attribution.");
            }

            await existingReader.CloseAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(false, true, null, strategyId, regime, reward);
        }

        const string aggregateSql = """
            INSERT INTO trading.strategy_regime_performances
                (strategy_id, regime, observation_count, cumulative_reward, average_reward, updated_at)
            VALUES (@strategy_id, @regime, 1, @reward, @reward, transaction_timestamp())
            ON CONFLICT (strategy_id, regime) DO UPDATE
            SET observation_count = trading.strategy_regime_performances.observation_count + 1,
                cumulative_reward = trading.strategy_regime_performances.cumulative_reward + EXCLUDED.cumulative_reward,
                average_reward = (trading.strategy_regime_performances.cumulative_reward + EXCLUDED.cumulative_reward) /
                                 (trading.strategy_regime_performances.observation_count + 1),
                updated_at = EXCLUDED.updated_at;
            """;
        await using (var aggregateCommand = new NpgsqlCommand(aggregateSql, connection, transaction))
        {
            aggregateCommand.Parameters.Add(Parameter("strategy_id", NpgsqlDbType.Varchar, strategyId));
            aggregateCommand.Parameters.Add(Parameter("regime", NpgsqlDbType.Varchar, regime.ToString()));
            aggregateCommand.Parameters.Add(Parameter("reward", NpgsqlDbType.Numeric, reward));
            await aggregateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(true, false, null, strategyId, regime, reward);
    }
}
