namespace CryptoTrading.Exchange.Binance;

/// <summary>Reasons why a kline was not admitted to strategy processing.</summary>
public enum BinanceKlineRejectionReason
{
    None,
    NotClosed,
    FutureTimestamp,
    Stale,
    Invalid,
    Duplicate,
    OutOfOrder,
    GapDetected,
    ResyncRequired
}

/// <summary>
/// Canonical, exchange-neutral representation of a completed Binance kline.
/// Timestamps are UTC and the close time is the first instant after the bar
/// (Binance's wire close time is inclusive and is therefore normalized by +1ms).
/// </summary>
public sealed record BinanceClosedKline(
    string Symbol,
    string Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    DateTimeOffset AvailableAtUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Symbol) || string.IsNullOrWhiteSpace(Interval))
        {
            throw new InvalidDataException("Closed kline symbol and interval are required.");
        }

        if (OpenTimeUtc.Offset != TimeSpan.Zero || CloseTimeUtc.Offset != TimeSpan.Zero || AvailableAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Closed kline timestamps must be UTC.");
        }

        if (CloseTimeUtc <= OpenTimeUtc || Open <= 0m || High <= 0m || Low <= 0m || Close <= 0m || Volume < 0m ||
            High < Math.Max(Open, Close) || Low > Math.Min(Open, Close) || Low <= 0m)
        {
            throw new InvalidDataException("Closed kline OHLCV values are invalid.");
        }

        if (CloseTimeUtc > AvailableAtUtc)
        {
            throw new InvalidDataException("A closed kline cannot be admitted before its close time.");
        }
    }
}

public sealed record BinanceKlineGuardDecision(
    bool IsAccepted,
    BinanceKlineRejectionReason RejectionReason,
    BinanceClosedKline? Kline = null)
{
    public static BinanceKlineGuardDecision Accepted(BinanceClosedKline kline) =>
        new(true, BinanceKlineRejectionReason.None, kline);

    public static BinanceKlineGuardDecision Rejected(BinanceKlineRejectionReason reason) =>
        new(false, reason);
}

/// <summary>
/// Fail-closed quality gate for a single symbol/interval kline stream.
/// It removes open bars, duplicates and out-of-order events, rejects stale data,
/// and enters a resync-required state when a closed-bar gap is observed.
/// Call <see cref="Reset"/> only after a REST backfill or another authoritative
/// recovery has repaired the missing interval.
/// </summary>
public sealed class BinanceClosedKlineGuard
{
    private readonly string symbol;
    private readonly string interval;
    private readonly TimeSpan maximumStaleness;
    private DateTimeOffset? lastOpenTimeUtc;
    private bool resyncRequired;

    public BinanceClosedKlineGuard(string symbol, string interval, TimeSpan maximumStaleness)
    {
        this.symbol = NormalizeSymbol(symbol);
        this.interval = NormalizeInterval(interval);
        if (maximumStaleness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStaleness), "Maximum staleness must be positive.");
        }

        this.maximumStaleness = maximumStaleness;
    }

    public DateTimeOffset? LastOpenTimeUtc => lastOpenTimeUtc;

    public bool ResyncRequired => resyncRequired;

    public BinanceKlineGuardDecision Evaluate(BinanceKlineEvent source, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evaluation time must be UTC.", nameof(nowUtc));
        }

        if (resyncRequired)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.ResyncRequired);
        }

        if (!source.IsClosed)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.NotClosed);
        }

        string sourceSymbol;
        string sourceInterval;
        try
        {
            sourceSymbol = NormalizeSymbol(source.Symbol);
            sourceInterval = NormalizeInterval(source.Interval);
        }
        catch (ArgumentException)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        if (!string.Equals(sourceSymbol, symbol, StringComparison.Ordinal) ||
            !string.Equals(sourceInterval, interval, StringComparison.Ordinal))
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        DateTimeOffset open;
        DateTimeOffset wireClose;
        try
        {
            open = DateTimeOffset.FromUnixTimeMilliseconds(source.OpenTime).ToUniversalTime();
            wireClose = DateTimeOffset.FromUnixTimeMilliseconds(source.CloseTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        var close = wireClose.AddMilliseconds(1);
        if (close > nowUtc)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.FutureTimestamp);
        }

        if (nowUtc - close > maximumStaleness)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Stale);
        }

        var normalized = new BinanceClosedKline(
            symbol,
            interval,
            open,
            close,
            source.Open,
            source.High,
            source.Low,
            source.Close,
            source.Volume,
            nowUtc);
        try
        {
            normalized.Validate();
        }
        catch (InvalidDataException)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        if (lastOpenTimeUtc is { } previous)
        {
            if (open == previous)
            {
                return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Duplicate);
            }

            if (open < previous)
            {
                return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.OutOfOrder);
            }

            var expected = AddInterval(previous, interval);
            if (open != expected)
            {
                resyncRequired = true;
                return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.GapDetected);
            }
        }

        lastOpenTimeUtc = open;
        return BinanceKlineGuardDecision.Accepted(normalized);
    }

    /// <summary>Admits a REST kline through the same closed-bar contract as WebSocket data.</summary>
    public BinanceKlineGuardDecision Evaluate(BinanceSpotKline source, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.OpenTimeUtc.Offset != TimeSpan.Zero || source.CloseTimeUtc.Offset != TimeSpan.Zero)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        BinanceKlineEvent eventRecord;
        try
        {
            eventRecord = new BinanceKlineEvent(
                $"rest:{symbol}",
                symbol,
                source.CloseTimeUtc.ToUnixTimeMilliseconds(),
                nowUtc,
                source.OpenTimeUtc.ToUnixTimeMilliseconds(),
                source.CloseTimeUtc.ToUnixTimeMilliseconds(),
                source.Interval,
                source.Open,
                source.Close,
                source.High,
                source.Low,
                source.Volume,
                IsClosed: true);
        }
        catch (ArgumentException)
        {
            return BinanceKlineGuardDecision.Rejected(BinanceKlineRejectionReason.Invalid);
        }

        return Evaluate(eventRecord, nowUtc);
    }

    /// <summary>Marks the stream healthy after an authoritative backfill.</summary>
    public void Reset(DateTimeOffset? lastRecoveredOpenTimeUtc = null)
    {
        if (lastRecoveredOpenTimeUtc is { } recovered && recovered.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recovered timestamp must be UTC.", nameof(lastRecoveredOpenTimeUtc));
        }

        lastOpenTimeUtc = lastRecoveredOpenTimeUtc;
        resyncRequired = false;
    }

    private static DateTimeOffset AddInterval(DateTimeOffset value, string interval) => interval switch
    {
        "1s" => value.AddSeconds(1),
        "1m" => value.AddMinutes(1),
        "3m" => value.AddMinutes(3),
        "5m" => value.AddMinutes(5),
        "15m" => value.AddMinutes(15),
        "30m" => value.AddMinutes(30),
        "1h" => value.AddHours(1),
        "2h" => value.AddHours(2),
        "4h" => value.AddHours(4),
        "6h" => value.AddHours(6),
        "8h" => value.AddHours(8),
        "12h" => value.AddHours(12),
        "1d" => value.AddDays(1),
        "3d" => value.AddDays(3),
        "1w" => value.AddDays(7),
        "1M" => value.AddMonths(1),
        _ => throw new InvalidOperationException($"Unsupported Binance kline interval '{interval}'.")
    };

    private static string NormalizeSymbol(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant();
    }

    private static string NormalizeInterval(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        var allowed = new[] { "1s", "1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "6h", "8h", "12h", "1d", "3d", "1w", "1M" };
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unsupported Binance spot kline interval.", nameof(value));
        }

        return normalized;
    }
}
