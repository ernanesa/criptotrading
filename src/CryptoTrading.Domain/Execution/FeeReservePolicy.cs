namespace CryptoTrading.Domain.Execution;

/// <summary>
/// Conservative quote-currency reserve used to cover exchange fees for BUY
/// sizing. SELL sizing never consumes this reserve because it reduces the
/// reconciled base position.
/// </summary>
public sealed record FeeReservePolicy
{
    public const decimal DefaultReserveBps = 25m;
    public const decimal MaximumReserveBps = 10_000m;

    public FeeReservePolicy(decimal reserveBps = DefaultReserveBps)
    {
        if (reserveBps < 0m || reserveBps > MaximumReserveBps)
        {
            throw new ArgumentOutOfRangeException(nameof(reserveBps), "Fee reserve must be between 0 and 10000 bps.");
        }

        ReserveBps = reserveBps;
    }

    public decimal ReserveBps { get; }

    public bool TryCalculate(decimal authorizedNotional, out decimal reserveNotional)
    {
        reserveNotional = 0m;
        if (authorizedNotional < 0m)
        {
            return false;
        }

        try
        {
            reserveNotional = authorizedNotional * ReserveBps / 10_000m;
            return reserveNotional >= 0m;
        }
        catch (OverflowException)
        {
            reserveNotional = 0m;
            return false;
        }
    }

    public bool TryCalculate(decimal authorizedNotional, decimal effectiveCommissionRate, out decimal reserveNotional)
    {
        reserveNotional = 0m;
        if (authorizedNotional < 0m || effectiveCommissionRate < 0m)
        {
            return false;
        }

        try
        {
            var configured = checked(authorizedNotional * ReserveBps / 10_000m);
            var effective = checked(authorizedNotional * effectiveCommissionRate);
            reserveNotional = Math.Max(configured, effective);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
