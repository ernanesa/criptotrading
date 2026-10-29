namespace CryptoTrading.Shadow;

/// <summary>
/// Deterministic clock used to replay a bar timeline through the Paper exchange.
/// The caller advances it explicitly; it never reads the wall clock, so identical
/// inputs use the same explicitly supplied clock values.
/// </summary>
public sealed class AdvancingTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private long utcTicks = initial.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

    /// <summary>Moves the clock forward. Going backwards is rejected to keep ordering sane.</summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        var target = instant.UtcTicks;
        while (true)
        {
            var current = Interlocked.Read(ref utcTicks);
            if (target < current)
            {
                throw new InvalidOperationException("Replay clock cannot move backwards.");
            }

            if (target == current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref utcTicks, target, current) == current)
            {
                return;
            }
        }
    }
}
