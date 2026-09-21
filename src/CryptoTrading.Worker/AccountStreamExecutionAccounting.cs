using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;

namespace CryptoTrading.Worker;

/// <summary>Converts a validated Binance TRADE report into an atomic accounting command.</summary>
internal static class AccountStreamExecutionAccounting
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];

    public static ExecutionAccountingCommand? ToCommand(
        BinanceExecutionReportEvent execution,
        AccountId walletAccount,
        ExecutionFeeEstimate? feeEstimate = null,
        decimal feeDriftToleranceBps = ExecutionFeeDriftEvaluator.DefaultToleranceBps,
        ExecutionFeeAssetValuation? feeAssetValuation = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!string.Equals(execution.ExecutionType, "TRADE", StringComparison.OrdinalIgnoreCase)
            || execution.LastExecutedQuantity <= 0m)
        {
            return null;
        }

        var symbol = new Symbol(execution.Symbol);
        var (baseAsset, quoteAsset) = SplitSymbol(symbol.Value);
        var side = execution.Side.Trim().ToUpperInvariant() switch
        {
            "BUY" => OrderSide.Buy,
            "SELL" => OrderSide.Sell,
            _ => throw new InvalidOperationException($"Unsupported execution side '{execution.Side}'.")
        };
        var commissionAsset = string.IsNullOrWhiteSpace(execution.CommissionAsset)
            ? quoteAsset
            : new Asset(execution.CommissionAsset);
        var executionId = execution.TradeId is { } tradeId
            ? $"trade:{tradeId.ToString(CultureInfo.InvariantCulture)}"
            : $"report:{AccountStreamHostedService.ToRecord(execution).DedupeKey}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(executionId));
        var ledgerId = new LedgerEntryId(new Guid(digest.AsSpan(0, 16)));
        var correlationId = new CorrelationId(new Guid(digest.AsSpan(16, 16)));
        var idempotency = new IdempotencyKey($"account-execution-{Convert.ToHexString(digest[..16])}");
        var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(execution.EventTimeMilliseconds);
        return new ExecutionAccountingCommand(
            executionId,
            execution.TradeId,
            execution.OrderId,
            symbol,
            baseAsset,
            quoteAsset,
            side,
            new Quantity(execution.LastExecutedQuantity),
            new Price(execution.LastExecutedPrice),
            new Money(execution.Commission, commissionAsset),
            walletAccount,
            new AccountId($"{walletAccount.Value}:trading-fees"),
            ledgerId,
            correlationId,
            idempotency,
            occurredAt,
            execution.ClientOrderId,
            feeEstimate,
            feeDriftToleranceBps,
            feeAssetValuation,
            new Quantity(execution.CumulativeExecutedQuantity));
    }

    private static (Asset Base, Asset Quote) SplitSymbol(string symbol)
    {
        var quote = QuoteAssets.FirstOrDefault(asset => symbol.EndsWith(asset, StringComparison.Ordinal));
        if (quote is null || symbol.Length <= quote.Length)
        {
            throw new InvalidOperationException($"Cannot derive quote asset from Binance symbol '{symbol}'.");
        }

        return (new Asset(symbol[..^quote.Length]), new Asset(quote));
    }
}
