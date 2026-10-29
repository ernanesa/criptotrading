using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Accounting;

/// <summary>Immutable facts needed to account one venue trade exactly once.</summary>
public sealed record ExecutionAccountingCommand
{
    public ExecutionAccountingCommand(
        string executionId,
        long? tradeId,
        long orderId,
        Symbol symbol,
        Asset baseAsset,
        Asset quoteAsset,
        OrderSide side,
        Quantity quantity,
        Price price,
        Money commission,
        AccountId walletAccount,
        AccountId feesAccount,
        LedgerEntryId ledgerEntryId,
        CorrelationId correlationId,
        IdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        string? clientOrderId = null,
        ExecutionFeeEstimate? feeEstimate = null,
        decimal feeDriftToleranceBps = ExecutionFeeDriftEvaluator.DefaultToleranceBps,
        ExecutionFeeAssetValuation? feeAssetValuation = null,
        Quantity? cumulativeExecutedQuantity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (executionId.Length > 200) throw new ArgumentOutOfRangeException(nameof(executionId));
        ArgumentOutOfRangeException.ThrowIfNegative(orderId);
        if (side is not (OrderSide.Buy or OrderSide.Sell)) throw new ArgumentOutOfRangeException(nameof(side));
        if (quantity.Value <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Execution timestamps must be UTC.", nameof(occurredAtUtc));
        if (feeDriftToleranceBps < 0m || feeDriftToleranceBps > 10_000m) throw new ArgumentOutOfRangeException(nameof(feeDriftToleranceBps));
        if (cumulativeExecutedQuantity is { } cumulative && cumulative.Value < quantity.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(cumulativeExecutedQuantity), "Cumulative execution quantity cannot be below this fill.");
        }

        ExecutionId = executionId.Trim();
        TradeId = tradeId;
        OrderId = orderId;
        Symbol = symbol;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        Side = side;
        Quantity = quantity;
        Price = price;
        Commission = commission;
        WalletAccount = walletAccount;
        FeesAccount = feesAccount;
        LedgerEntryId = ledgerEntryId;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        OccurredAtUtc = occurredAtUtc;
        ClientOrderId = clientOrderId?.Trim();
        FeeEstimate = feeEstimate;
        FeeDriftToleranceBps = feeDriftToleranceBps;
        FeeAssetValuation = feeAssetValuation;
        CumulativeExecutedQuantity = cumulativeExecutedQuantity;
    }

    public string ExecutionId { get; }
    public long? TradeId { get; }
    public long OrderId { get; }
    public Symbol Symbol { get; }
    public Asset BaseAsset { get; }
    public Asset QuoteAsset { get; }
    public OrderSide Side { get; }
    public Quantity Quantity { get; }
    public Price Price { get; }
    public Money Commission { get; }
    public AccountId WalletAccount { get; }
    public AccountId FeesAccount { get; }
    public LedgerEntryId LedgerEntryId { get; }
    public CorrelationId CorrelationId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public string? ClientOrderId { get; }
    public ExecutionFeeEstimate? FeeEstimate { get; }
    public decimal FeeDriftToleranceBps { get; }
    public ExecutionFeeAssetValuation? FeeAssetValuation { get; }
    public Quantity? CumulativeExecutedQuantity { get; }
}

public sealed record ExecutionAccountingResult(
    string ExecutionId,
    LedgerEntry LedgerEntry,
    decimal? RealizedPnlQuote,
    decimal? AverageCostQuote,
    bool IsDuplicate,
    ExecutionFeeComparison? FeeComparison = null,
    ExecutionQualityComparison? QualityComparison = null);

/// <summary>Pure construction of balanced postings for one spot trade.</summary>
public static class ExecutionAccounting
{
    public static LedgerEntry CreateLedgerEntry(ExecutionAccountingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var notional = command.Quantity.Value * command.Price.Value;
        var venueSettlement = new AccountId($"{command.WalletAccount.Value}:venue:{command.Symbol.Value}");
        var postings = new List<LedgerPosting>
        {
            new(command.WalletAccount, command.BaseAsset, command.Side == OrderSide.Buy ? command.Quantity.Value : -command.Quantity.Value),
            new(venueSettlement, command.BaseAsset, command.Side == OrderSide.Buy ? -command.Quantity.Value : command.Quantity.Value),
            new(command.WalletAccount, command.QuoteAsset, command.Side == OrderSide.Buy ? -notional : notional),
            new(venueSettlement, command.QuoteAsset, command.Side == OrderSide.Buy ? notional : -notional),
        };

        if (command.Commission.Amount != 0m)
        {
            postings.Add(new LedgerPosting(command.WalletAccount, command.Commission.Asset, -command.Commission.Amount));
            postings.Add(new LedgerPosting(command.FeesAccount, command.Commission.Asset, command.Commission.Amount));
        }

        return new LedgerEntry(
            command.LedgerEntryId,
            command.CorrelationId,
            command.OccurredAtUtc,
            $"execution:{command.ExecutionId}",
            postings,
            command.IdempotencyKey);
    }

    /// <summary>
    /// Updates average-cost inventory and computes quote-denominated realized PnL.
    /// A commission in a third asset requires fresh, non-future conversion evidence
    /// into the fill quote asset before the quote cost or realized PnL is computed.
    /// </summary>
    public static (decimal Quantity, decimal CostQuote, decimal? RealizedPnlQuote) ApplyAverageCost(
        ExecutionAccountingCommand command,
        decimal currentQuantity,
        decimal currentCostQuote)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (currentQuantity < 0m || currentCostQuote < 0m) throw new ArgumentOutOfRangeException(nameof(currentQuantity));
        var notional = command.Quantity.Value * command.Price.Value;
        var commissionInBase = command.Commission.Asset == command.BaseAsset ? command.Commission.Amount : 0m;
        var commissionInQuote = command.Commission.Asset == command.QuoteAsset ? command.Commission.Amount : 0m;
        if (command.Commission.Amount != 0m && commissionInBase == 0m && commissionInQuote == 0m)
        {
            var valuation = command.FeeAssetValuation;
            if (valuation is null || valuation.Asset != command.Commission.Asset ||
                valuation.QuoteAsset != command.QuoteAsset || valuation.ObservedAtUtc > command.OccurredAtUtc ||
                command.OccurredAtUtc - valuation.ObservedAtUtc > TimeSpan.FromMinutes(2))
            {
                throw new InvalidOperationException("A third-asset commission requires a matching, fresh quote valuation at or before the fill.");
            }

            commissionInQuote = checked(command.Commission.Amount * valuation.QuotePerAsset);
        }

        return ApplyAverageCostCore(
            command.Side, command.Quantity.Value, command.Price.Value,
            commissionInBase, commissionInQuote, currentQuantity, currentCostQuote);
    }

    /// <summary>
    /// Average-cost arithmetic without identity, accounts or ledger concerns, so
    /// simulation paths can reuse exactly the same math as live accounting.
    /// </summary>
    public static (decimal Quantity, decimal CostQuote, decimal? RealizedPnlQuote) ApplyAverageCostCore(
        OrderSide side,
        decimal quantity,
        decimal price,
        decimal commissionInBase,
        decimal commissionInQuote,
        decimal currentQuantity,
        decimal currentCostQuote)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentQuantity);
        ArgumentOutOfRangeException.ThrowIfNegative(currentCostQuote);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        if (side is not (OrderSide.Buy or OrderSide.Sell)) throw new ArgumentOutOfRangeException(nameof(side));
        if (currentQuantity == 0m && currentCostQuote != 0m) throw new InvalidOperationException("Zero inventory cannot carry a cost basis.");
        var notional = quantity * price;

        if (side == OrderSide.Buy)
        {
            var received = quantity - commissionInBase;
            if (received <= 0m) throw new InvalidOperationException("A buy execution must leave positive base inventory.");
            var nextCost = currentCostQuote + notional + commissionInQuote;
            if (nextCost < 0m) throw new InvalidOperationException("A buy execution cannot produce negative cost basis.");
            return (currentQuantity + received, nextCost, null);
        }

        var disposed = quantity + commissionInBase;
        if (disposed <= 0m) throw new InvalidOperationException("A sell execution must dispose of positive base inventory.");
        if (disposed > currentQuantity) throw new InvalidOperationException("Sell execution exceeds tracked inventory.");
        var averageCost = currentQuantity == 0m ? 0m : currentCostQuote / currentQuantity;
        var realized = notional - (disposed * averageCost) - commissionInQuote;
        return (currentQuantity - disposed, currentCostQuote - disposed * averageCost, realized);
    }
}
