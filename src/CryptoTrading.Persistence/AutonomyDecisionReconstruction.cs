using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Persistence;

/// <summary>
/// Point-in-time reconstruction of an autonomous cycle decision and all associated
/// audit evidence (versions, input/feature fingerprints, risk verdict, fee projection,
/// outbox submission, and lifecycle transitions).
/// </summary>
public sealed record AutonomyDecisionReconstructionReport(
    AutonomyDecisionCommit Commit,
    bool CanonicalIdentityHashMatches,
    string ExpectedCanonicalIdentity,
    string? IntentClientOrderId,
    ExecutionFeeEstimate? ExpectedFee,
    ExecutionSubmissionContext? SubmissionContext,
    IReadOnlyList<OrderLifecycleEvent>? LifecycleEvents)
{
    public bool IsAuditVerified => CanonicalIdentityHashMatches;

    public bool HasCompleteDispatchAudit =>
        CanonicalIdentityHashMatches &&
        (Commit.Outcome != "DISPATCHABLE" || (SubmissionContext is not null && ExpectedFee is not null));
}

/// <summary>
/// Reconstructs and verifies an autonomy decision cycle from durable store evidence
/// without causing any mutations or side effects.
/// </summary>
public static class AutonomyDecisionReconstructor
{
    public static string ComputeCanonicalIdentity(
        string environment,
        string accountScope,
        string symbol,
        string strategyId,
        string strategyVersion,
        DateTimeOffset closeTimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion);

        return string.Join('|',
            environment.Trim().ToUpperInvariant(),
            accountScope.Trim(),
            symbol.Trim().ToUpperInvariant(),
            strategyId.Trim(),
            strategyVersion.Trim(),
            closeTimeUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    public static string ComputeCanonicalDecisionId(
        string environment,
        string accountScope,
        string symbol,
        string strategyId,
        string strategyVersion,
        DateTimeOffset closeTimeUtc)
    {
        var canonical = ComputeCanonicalIdentity(environment, accountScope, symbol, strategyId, strategyVersion, closeTimeUtc);
        return "ad-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool VerifyCommitIdentity(AutonomyDecisionCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var expectedId = ComputeCanonicalDecisionId(
            commit.Environment,
            commit.AccountScope,
            commit.Symbol.Value,
            commit.StrategyId,
            commit.StrategyVersion,
            commit.CloseTimeUtc);
        return string.Equals(commit.DecisionId, expectedId, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AutonomyDecisionReconstructionReport?> ReconstructAsync(
        string decisionId,
        IAutonomyDecisionCommitStore decisionStore,
        IExpectedExecutionFeeStore? feeStore = null,
        IExecutionSubmissionContextStore? submissionStore = null,
        IOrderLifecycleStore? lifecycleStore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentNullException.ThrowIfNull(decisionStore);

        var commit = await decisionStore.ReadDecisionCommitAsync(decisionId, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }

        var expectedIdentity = ComputeCanonicalIdentity(
            commit.Environment,
            commit.AccountScope,
            commit.Symbol.Value,
            commit.StrategyId,
            commit.StrategyVersion,
            commit.CloseTimeUtc);
        var matches = VerifyCommitIdentity(commit);

        string? intentClientOrderId = null;
        if (commit.OutboxRecord is not null)
        {
            intentClientOrderId = commit.OutboxRecord.Command.ClientOrderId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(commit.TraceJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(commit.TraceJson);
                if (doc.RootElement.TryGetProperty("IntentClientOrderId", out var prop))
                {
                    intentClientOrderId = prop.GetString();
                }
                else if (doc.RootElement.TryGetProperty("BlockedClientOrderId", out var blockedProp))
                {
                    intentClientOrderId = blockedProp.GetString();
                }
            }
            catch (JsonException)
            {
                // TraceJson may not be json in test fakes
            }
        }

        ExecutionFeeEstimate? fee = null;
        ExecutionSubmissionContext? submission = null;
        IReadOnlyList<OrderLifecycleEvent>? lifecycle = null;

        if (!string.IsNullOrWhiteSpace(intentClientOrderId))
        {
            if (feeStore is not null)
            {
                fee = await feeStore.ReadExpectedExecutionFeeAsync(intentClientOrderId, cancellationToken).ConfigureAwait(false);
            }

            if (submissionStore is not null)
            {
                submission = await submissionStore.ReadExecutionSubmissionContextAsync(intentClientOrderId, cancellationToken).ConfigureAwait(false);
            }

            if (lifecycleStore is not null)
            {
                lifecycle = await lifecycleStore.ReadOrderLifecycleEventsAsync(intentClientOrderId, cancellationToken).ConfigureAwait(false);
            }
        }

        return new AutonomyDecisionReconstructionReport(
            commit,
            matches,
            expectedIdentity,
            intentClientOrderId,
            fee,
            submission,
            lifecycle ?? []);
    }
}
