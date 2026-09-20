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
        DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (executionId.Length > 200) throw new ArgumentOutOfRangeException(nameof(executionId));
        ArgumentOutOfRangeException.ThrowIfNegative(orderId);
        if (quantity.Value <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (occurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Execution timestamps must be UTC.", nameof(occurredAtUtc));
        if (commission.Amount < 0m) throw new ArgumentOutOfRangeException(nameof(commission));

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
}

public sealed record ExecutionAccountingResult(
    string ExecutionId,
    LedgerEntry LedgerEntry,
    decimal? RealizedPnlQuote,
    decimal? AverageCostQuote,
    bool IsDuplicate);

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

        if (command.Commission.Amount > 0m)
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
    /// A non-quote commission is recorded in the ledger but is intentionally not
    /// converted without a trusted mark price.
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

        if (command.Side == OrderSide.Buy)
        {
            var received = command.Quantity.Value - commissionInBase;
            if (received <= 0m) throw new InvalidOperationException("A buy execution must leave positive base inventory.");
            return (currentQuantity + received, currentCostQuote + notional + commissionInQuote, null);
        }

        var disposed = command.Quantity.Value + commissionInBase;
        if (disposed > currentQuantity) throw new InvalidOperationException("Sell execution exceeds tracked inventory.");
        var averageCost = currentQuantity == 0m ? 0m : currentCostQuote / currentQuantity;
        var realized = (command.Quantity.Value * command.Price.Value) - (command.Quantity.Value * averageCost) - commissionInQuote;
        return (currentQuantity - disposed, currentCostQuote - disposed * averageCost, realized);
    }
}
