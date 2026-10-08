#pragma warning disable CA2007 // Tests retain the xUnit synchronization context.
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ExecutionSubmissionGuardTests
{
    [Theory]
    [InlineData(OperationalMode.SafeMode)]
    [InlineData(OperationalMode.KillSwitch)]
    public async Task OperationalStopBlocksBothGuardsBeforeAnyDependencyCall(OperationalMode mode)
    {
        var control = new InMemoryOperationalControl(new RejectReset());
        var stop = new OperationalControlCommand("test", "test stop", IdempotencyKey.New(), DateTimeOffset.UtcNow);
        if (mode == OperationalMode.KillSwitch) _ = await control.ActivateKillSwitchAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        else _ = await control.EnterSafeModeAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        await using var supervisor = new RuntimeSupervisor();
        var guard = Create(control, supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredIntentBlocksBeforeMarketOrAccountIo()
    {
        await using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        var guard = Create(new InMemoryOperationalControl(new RejectReset()), supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow.AddMinutes(-2));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ExecutionSubmissionGuard Create(IOperationalControl control, IRuntimeSupervisor supervisor) =>
        new(control, supervisor, null!, null!, null!, null!, null!, null!,
            Options.Create(new WorkerRuntimeOptions()), NullLogger<ExecutionSubmissionGuard>.Instance);

    private sealed class RejectReset : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand command,
            OperationalControlResetAuthorization authorization) => false;
    }

    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) Command(DateTimeOffset created)
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Market, new Quantity(0.01m), new Price(10000m), 100m,
            correlation, idempotency, "risk-v1", created);
        return (command, new LeasedOutboxMessage(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType,
            correlation, idempotency, "{}", created, 1, created.AddMinutes(1), "test"));
    }
}
