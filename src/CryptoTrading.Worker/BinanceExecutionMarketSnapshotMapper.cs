using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;

namespace CryptoTrading.Worker;

/// <summary>
/// Converts public Binance top-of-book evidence into the domain snapshot used by
/// the pure execution planner. The conservative minimum of both sides is used
/// because the strategy signal is evaluated after this adapter is called.
/// </summary>
internal static class BinanceExecutionMarketSnapshotMapper
{
    public static ExecutionMarketSnapshot Map(
        BinanceBookTicker ticker,
        BinanceSymbolTradingRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        return new ExecutionMarketSnapshot(
            new Price(ticker.BidPrice),
            new Price(ticker.AskPrice),
            Math.Min(ticker.BidLiquidityNotional, ticker.AskLiquidityNotional),
            ticker.ObservedAtUtc,
            quantityStepSize: rules?.QuantityStepSize,
            priceTickSize: rules?.PriceTickSize);
    }
}
