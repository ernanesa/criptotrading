namespace CryptoTrading.Domain.Risk;

/// <summary>Durable state used to derive an equity high-water mark safely.</summary>
public sealed record EquityHighWaterMarkState(
    decimal EquityNotional,
    decimal HighWaterMarkNotional,
    decimal DrawdownNotional,
    DateTimeOffset LastValuationAtUtc,
    long ObservationCount);

/// <summary>Result of applying one valuation observation to a monotonic high-water mark.</summary>
public sealed record EquityHighWaterMarkUpdate(
    EquityHighWaterMarkState State,
    bool IsComplete,
    string? UnavailableReason);

/// <summary>
/// Pure restart-safe high-water-mark transition. It never lowers the mark and
/// rejects time-travel or conflicting replay observations instead of guessing.
/// </summary>
public static class EquityHighWaterMarkCalculator
{
    public const string InitialObservationRequired = "INITIAL_HIGH_WATER_MARK_OBSERVATION_REQUIRED";
    public const string OutOfOrderValuation = "OUT_OF_ORDER_VALUATION";
    public const string ValuationConflict = "VALUATION_CONFLICT";

    public static EquityHighWaterMarkUpdate Apply(
        decimal equityNotional,
        DateTimeOffset valuationAtUtc,
        EquityHighWaterMarkState? existing)
    {
        if (equityNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(equityNotional), "Equity cannot be negative.");
        }

        if (valuationAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Equity valuations must be UTC.", nameof(valuationAtUtc));
        }

        if (existing is null)
        {
            var initial = new EquityHighWaterMarkState(
                equityNotional,
                equityNotional,
                0m,
                valuationAtUtc,
                1);
            return new(initial, false, InitialObservationRequired);
        }

        if (existing.ObservationCount < 1 ||
            existing.EquityNotional < 0m ||
            existing.HighWaterMarkNotional < existing.EquityNotional ||
            existing.DrawdownNotional < 0m ||
            existing.LastValuationAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Persisted equity high-water-mark state is invalid.");
        }

        if (valuationAtUtc < existing.LastValuationAtUtc)
        {
            return new(existing, false, OutOfOrderValuation);
        }

        if (valuationAtUtc == existing.LastValuationAtUtc)
        {
            if (equityNotional != existing.EquityNotional)
            {
                return new(existing, false, ValuationConflict);
            }

            return existing.ObservationCount > 1
                ? new(existing, true, null)
                : new(existing, false, InitialObservationRequired);
        }

        var highWaterMark = Math.Max(existing.HighWaterMarkNotional, equityNotional);
        var state = new EquityHighWaterMarkState(
            equityNotional,
            highWaterMark,
            Math.Max(0m, highWaterMark - equityNotional),
            valuationAtUtc,
            checked(existing.ObservationCount + 1));
        return new(state, true, null);
    }
}
