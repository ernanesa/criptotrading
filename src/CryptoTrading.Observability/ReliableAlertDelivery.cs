#pragma warning disable CA1031 // Sink implementations expose heterogeneous transient failures; the boundary retries boundedly.

#pragma warning disable CA1031 // The bounded policy intentionally retries arbitrary sink failures before propagating the final error.

namespace CryptoTrading.Observability;

/// <summary>Bounded, deterministic retry policy for a local or external alert sink.</summary>
public sealed record TradingAlertRetryOptions
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (MaxAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "Alert retry attempts must be between 1 and 20.");
        }

        if (InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), "Alert retry delay cannot be negative.");
        }

        if (MaxDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), "Maximum alert retry delay cannot be smaller than the initial delay.");
        }
    }
}

/// <summary>
/// Retries transient sink failures with bounded exponential backoff. Cancellation
/// is never retried and the final exception is propagated to the caller.
/// </summary>
public sealed class RetryingTradingAlertSink : ITradingAlertSink
{
    private readonly ITradingAlertSink inner;
    private readonly TradingAlertRetryOptions options;
    private readonly TimeProvider timeProvider;

    public RetryingTradingAlertSink(
        ITradingAlertSink inner,
        TradingAlertRetryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.options = options ?? new TradingAlertRetryOptions();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Validate();

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await inner.DeliverAsync(alert, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attempt < options.MaxAttempts)
            {
                var delay = CalculateDelay(attempt);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Alert delivery retry loop exited unexpectedly.");
    }

    private TimeSpan CalculateDelay(int failedAttempt)
    {
        var ticks = Math.Min(
            (double)options.MaxDelay.Ticks,
            options.InitialDelay.Ticks * Math.Pow(2, failedAttempt - 1));
        return TimeSpan.FromTicks((long)ticks);
    }
}
