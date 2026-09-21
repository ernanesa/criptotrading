using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CryptoTrading.Worker;

internal interface IExecutionFeeAssetValuationProvider
{
    Task<ExecutionFeeAssetValuation?> ResolveAsync(
        BinanceExecutionReportEvent execution,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves BNB commission using public aggregate-trade evidence no later than
/// the fill. A missing, delayed, or malformed quote remains unavailable so the
/// accounting path can persist the fill and enter SafeMode without fabrication.
/// </summary>
internal sealed class BinanceFeeAssetValuationProvider(
    IBinanceSpotMarketClient marketClient,
    IOptions<WorkerRuntimeOptions> options) : IExecutionFeeAssetValuationProvider
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];

    public async Task<ExecutionFeeAssetValuation?> ResolveAsync(
        BinanceExecutionReportEvent execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!string.Equals(execution.ExecutionType, "TRADE", StringComparison.OrdinalIgnoreCase) ||
            execution.LastExecutedQuantity <= 0m ||
            !string.Equals(execution.CommissionAsset?.Trim(), "BNB", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var quoteAsset = ResolveQuoteAsset(execution.Symbol);
        if (string.Equals(quoteAsset.Value, "BNB", StringComparison.Ordinal))
        {
            return null;
        }

        var fillAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(execution.EventTimeMilliseconds);
        try
        {
            var trades = await marketClient.GetAggregateTradesAsync(
                "BNB" + quoteAsset.Value,
                fillAtUtc,
                limit: 1000,
                cancellationToken).ConfigureAwait(false);
            var trade = trades
                .Where(candidate => candidate.OccurredAtUtc <= fillAtUtc)
                .OrderByDescending(static candidate => candidate.OccurredAtUtc)
                .ThenByDescending(static candidate => candidate.AggregateTradeId)
                .FirstOrDefault();
            if (trade is null || fillAtUtc - trade.OccurredAtUtc > TimeSpan.FromSeconds(options.Value.FeeAssetValuationMaxAgeSeconds))
            {
                return null;
            }

            return new ExecutionFeeAssetValuation(
                new Asset("BNB"),
                quoteAsset,
                trade.Price,
                trade.OccurredAtUtc,
                "binance:GET:/api/v3/aggTrades");
        }
        catch (Exception exception) when (exception is HttpRequestException or BinanceApiException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static Asset ResolveQuoteAsset(string symbol)
    {
        var normalized = new Symbol(symbol).Value;
        var quote = QuoteAssets.FirstOrDefault(asset => normalized.EndsWith(asset, StringComparison.Ordinal));
        return quote is not null && normalized.Length > quote.Length
            ? new Asset(quote)
            : throw new InvalidOperationException($"Cannot derive quote asset from Binance symbol '{symbol}'.");
    }
}
