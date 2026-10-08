#pragma warning disable CA2007 // Test assertions intentionally follow the task context.

using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class ReliableAlertDeliveryTests
{
    [Fact]
    public async Task RetriesATransientSinkFailureWithBoundedAttempts()
    {
        var sink = new FailingSink(failuresBeforeSuccess: 2);
        using var retrying = new RetryingTradingAlertSink(
            sink,
            new TradingAlertRetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });

        await retrying.DeliverAsync(CreateAlert(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(3, sink.Attempts);
        Assert.Equal(1, sink.Deliveries);
    }

    [Fact]
    public async Task ExhaustedRetriesPropagateFailureAndNeverSilentlySucceed()
    {
        var sink = new FailingSink(failuresBeforeSuccess: 10);
        using var retrying = new RetryingTradingAlertSink(
            sink,
            new TradingAlertRetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await retrying.DeliverAsync(
            CreateAlert(), TestContext.Current.CancellationToken).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(3, sink.Attempts);
        Assert.Equal(0, sink.Deliveries);
    }

    [Fact]
    public async Task CancellationIsNotRetried()
    {
        var sink = new FailingSink(failuresBeforeSuccess: 10);
        using var retrying = new RetryingTradingAlertSink(
            sink,
            new TradingAlertRetryOptions { MaxAttempts = 5, InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await retrying.DeliverAsync(CreateAlert(), cancellation.Token).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(0, sink.Attempts);
    }

    [Fact]
    public void RetryOptionsRejectUnboundedOrInvalidDelays()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingAlertRetryOptions { MaxAttempts = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingAlertRetryOptions { InitialDelay = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingAlertRetryOptions
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(1),
        }.Validate());
    }

    private static TradingAlert CreateAlert() => new(
        "TESTNET",
        "BINANCE",
        "ops",
        new DateTimeOffset(2026, 9, 19, 15, 0, 0, TimeSpan.Zero),
        "corr-retry",
        TradingAlertSeverity.Warning,
        "ops",
        "inspect alert delivery",
        "docs/runbook-local.md",
        "retry:test");

    private sealed class FailingSink(int failuresBeforeSuccess) : ITradingAlertSink
    {
        private readonly int failuresBeforeSuccess = failuresBeforeSuccess;

        public int Attempts { get; private set; }

        public int Deliveries { get; private set; }

        public ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException("synthetic alert sink failure");
            }

            Deliveries++;
            return ValueTask.CompletedTask;
        }
    }
}
