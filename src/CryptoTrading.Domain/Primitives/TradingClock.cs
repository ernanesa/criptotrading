namespace CryptoTrading.Domain.Primitives;

/// <summary>
/// Small domain-facing view over the BCL <see cref="TimeProvider"/>.
/// Consumers should inject a TimeProvider (for example TimeProvider.System or
/// a deterministic test provider) instead of reading the system clock directly.
/// </summary>
public sealed class TradingClock(TimeProvider timeProvider)
{
    public TimeProvider Provider { get; } =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DateTimeOffset UtcNow => Provider.GetUtcNow();

    public long GetTimestamp() => Provider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Provider.GetElapsedTime(startingTimestamp);
}
