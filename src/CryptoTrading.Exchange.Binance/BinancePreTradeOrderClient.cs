namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Order-client boundary that requires fresh symbol rules before submission.
/// It performs no retries and delegates query/cancel unchanged.
/// </summary>
public sealed class BinancePreTradeValidatedOrderClient : IBinanceSpotOrderClient
{
    private readonly IBinanceSpotOrderClient inner;
    private readonly IBinanceSymbolTradingRulesProvider rulesProvider;

    public BinancePreTradeValidatedOrderClient(
        IBinanceSpotOrderClient inner,
        IBinanceSymbolTradingRulesProvider rulesProvider)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public async Task<BinanceSpotOrderResult> SubmitAsync(
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Execution must revalidate the venue rules immediately before the
        // external boundary; a still-fresh cache is insufficient after a
        // delayed outbox delivery or exchange filter change.
        var rules = await rulesProvider.RefreshAsync(request.Symbol, cancellationToken).ConfigureAwait(false);
        var validation = request.Type switch
        {
            BinanceSpotOrderType.Limit => rules.ValidateLimitOrder(request.Price!.Value, request.Quantity),
            BinanceSpotOrderType.Market => ValidateMarketOrder(rules, request.Quantity),
            _ => throw new BinancePreTradeValidationException(request.Symbol, [new("ORDER_TYPE_UNSUPPORTED", "Order type is not supported by the pre-trade boundary.")])
        };

        if (!validation.IsValid)
        {
            throw new BinancePreTradeValidationException(request.Symbol, validation.Violations);
        }

        return await inner.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        inner.QueryByClientOrderIdAsync(symbol, clientOrderId, cancellationToken);

    public Task<BinanceSpotOrderResult> CancelAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        inner.CancelAsync(symbol, clientOrderId, cancellationToken);

    private static BinanceOrderValidationResult ValidateMarketOrder(
        BinanceSymbolTradingRules rules,
        decimal quantity)
    {
        var violations = new List<BinanceOrderFilterViolation>();
        if (!string.Equals(rules.Status, "TRADING", StringComparison.Ordinal))
        {
            violations.Add(new("SYMBOL_NOT_TRADING", $"Symbol '{rules.Symbol}' is not in TRADING status."));
        }

        if (quantity <= 0m)
        {
            violations.Add(new("QUANTITY_NON_POSITIVE", "Quantity must be greater than zero."));
        }
        else
        {
            if (quantity < rules.MinimumQuantity)
            {
                violations.Add(new("QUANTITY_BELOW_MINIMUM", "Market quantity is below the symbol minimum."));
            }

            if (rules.MaximumQuantity > 0m && quantity > rules.MaximumQuantity)
            {
                violations.Add(new("QUANTITY_ABOVE_MAXIMUM", "Market quantity is above the symbol maximum."));
            }

            if (rules.QuantityStepSize > 0m && quantity % rules.QuantityStepSize != 0m)
            {
                violations.Add(new("QUANTITY_STEP_MISMATCH", "Market quantity is not aligned to the symbol step size."));
            }
        }

        // A market order has no client-supplied price. Without a trusted
        // reference price, min/max notional cannot be proven before submit.
        violations.Add(new("MARKET_NOTIONAL_UNVERIFIABLE", "Market-order notional cannot be verified before submission."));
        return new(violations.Count is 0, violations);
    }
}

public sealed class BinancePreTradeValidationException : InvalidOperationException
{
    public BinancePreTradeValidationException()
        : this(string.Empty, [])
    {
    }

    public BinancePreTradeValidationException(string message)
        : base(message)
    {
        Symbol = string.Empty;
        Violations = [];
    }

    public BinancePreTradeValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Symbol = string.Empty;
        Violations = [];
    }

    public BinancePreTradeValidationException(
        string symbol,
        IReadOnlyList<BinanceOrderFilterViolation> violations)
        : base($"Pre-trade validation rejected symbol '{symbol}' with {Count(violations)} violation(s).")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(violations);
        Symbol = symbol;
        Violations = violations;
    }

    public string Symbol { get; }

    public IReadOnlyList<BinanceOrderFilterViolation> Violations { get; }

    private static int Count(IReadOnlyList<BinanceOrderFilterViolation> violations) =>
        violations?.Count ?? throw new ArgumentNullException(nameof(violations));
}
