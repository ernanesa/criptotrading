#pragma warning disable CA1812 // Hosted service is instantiated by the generic host through dependency injection.

using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>Fail-closed startup gate for an explicitly required active RiskPolicy.</summary>
internal sealed class RiskPolicyStartupHostedService(
    IRiskPolicyStore policyStore,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<RiskPolicyStartupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.RiskPolicyRequired)
        {
            return;
        }

        var policy = await policyStore.GetActiveRiskPolicyAsync(DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            throw new InvalidOperationException(
                "WORKER_RISK_POLICY_REQUIRED=true but no valid active RiskPolicy was found; startup is blocked.");
        }

        if (policy.Policy.WithdrawalsEnabled)
        {
            throw new InvalidOperationException(
                "Active RiskPolicy has withdrawals enabled; startup is blocked fail-closed.");
        }

        var autonomySymbol = options.Value.AutonomySymbol;
        if (string.IsNullOrWhiteSpace(autonomySymbol) || autonomySymbol.Trim().Length < 2)
        {
            throw new InvalidOperationException(
                "Autonomy symbol is invalid; startup is blocked fail-closed.");
        }

        if (string.Equals(options.Value.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase) &&
            policy.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                "Active RiskPolicy has expired; startup is blocked fail-closed.");
        }

        RiskPolicyStartupLog.Validated(logger, policy.Policy.Version, policy.DocumentHash);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class RiskPolicyStartupLog
{
    [LoggerMessage(LogLevel.Information, "Active RiskPolicy validated at startup. Version={PolicyVersion} Hash={DocumentHash}")]
    public static partial void Validated(ILogger logger, string policyVersion, string documentHash);
}
