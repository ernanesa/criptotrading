using CryptoTrading.Domain.Risk;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class RiskPolicyStartupHostedServiceTests
{
    [Fact]
    public async Task StartAsyncWhenRiskPolicyNotRequiredDoesNotQueryStore()
    {
        var store = new FakeRiskPolicyStore();
        var options = Options.Create(new WorkerRuntimeOptions { RiskPolicyRequired = false });
        var service = new RiskPolicyStartupHostedService(store, options, NullLogger<RiskPolicyStartupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, store.GetActiveCalls);
    }

    [Fact]
    public async Task StartAsyncWhenPolicyMissingThrowsInvalidOperationException()
    {
        var store = new FakeRiskPolicyStore { ActivePolicy = null };
        var options = Options.Create(new WorkerRuntimeOptions { RiskPolicyRequired = true });
        var service = new RiskPolicyStartupHostedService(store, options, NullLogger<RiskPolicyStartupHostedService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("WORKER_RISK_POLICY_REQUIRED=true but no valid active RiskPolicy was found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsyncWhenPolicyHasWithdrawalsEnabledThrowsFailClosed()
    {
        var policy = new RiskPolicy(
            version: "v1",
            maxOrderNotional: 100m,
            maxExposureNotional: 1000m,
            maxDailyLoss: 50m,
            maxDrawdown: 100m,
            maxMarketDataAge: TimeSpan.FromMinutes(1),
            withdrawalsEnabled: true);
        var document = RiskPolicyDocument.CreateDraft(
            policy,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(24),
            "creator");
        document = document.Approve("approval-1", "approver", DateTimeOffset.UtcNow.AddMinutes(-30));
        document = document.Activate(DateTimeOffset.UtcNow.AddMinutes(-20));

        var store = new FakeRiskPolicyStore { ActivePolicy = document };
        var options = Options.Create(new WorkerRuntimeOptions { RiskPolicyRequired = true, AutonomySymbol = "BTCUSDT" });
        var service = new RiskPolicyStartupHostedService(store, options, NullLogger<RiskPolicyStartupHostedService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("withdrawals enabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsyncWhenAutonomySymbolIsInvalidThrowsFailClosed()
    {
        var document = CreateValidActivePolicyDocument();
        var store = new FakeRiskPolicyStore { ActivePolicy = document };
        var options = Options.Create(new WorkerRuntimeOptions { RiskPolicyRequired = true, AutonomySymbol = " " });
        var service = new RiskPolicyStartupHostedService(store, options, NullLogger<RiskPolicyStartupHostedService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("Autonomy symbol is invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsyncWhenValidActivePolicySucceeds()
    {
        var document = CreateValidActivePolicyDocument();
        var store = new FakeRiskPolicyStore { ActivePolicy = document };
        var options = Options.Create(new WorkerRuntimeOptions { RiskPolicyRequired = true, AutonomySymbol = "BTCUSDT" });
        var service = new RiskPolicyStartupHostedService(store, options, NullLogger<RiskPolicyStartupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.GetActiveCalls);
    }

    private static RiskPolicyDocument CreateValidActivePolicyDocument()
    {
        var policy = new RiskPolicy(
            version: "v1-prod",
            maxOrderNotional: 100m,
            maxExposureNotional: 1000m,
            maxDailyLoss: 50m,
            maxDrawdown: 100m,
            maxMarketDataAge: TimeSpan.FromMinutes(1),
            withdrawalsEnabled: false);
        var document = RiskPolicyDocument.CreateDraft(
            policy,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(24),
            "creator");
        document = document.Approve("approval-1", "approver", DateTimeOffset.UtcNow.AddMinutes(-30));
        return document.Activate(DateTimeOffset.UtcNow.AddMinutes(-20));
    }

    private sealed class FakeRiskPolicyStore : IRiskPolicyStore
    {
        public RiskPolicyDocument? ActivePolicy { get; set; }
        public int GetActiveCalls { get; private set; }

        public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default)
        {
            GetActiveCalls++;
            return Task.FromResult(ActivePolicy);
        }

        public Task SaveRiskPolicyAsync(RiskPolicyDocument document, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(string policyVersion, string approvalId, string approver, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(ActivePolicy!);

        public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(string policyVersion, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(ActivePolicy!);
    }
}
