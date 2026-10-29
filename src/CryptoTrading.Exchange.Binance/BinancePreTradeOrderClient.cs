using System.Globalization;
namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Order-client boundary that requires fresh rules before submission. New
/// submissions remain blocked until exchange-wide and account filters can be
/// independently verified. Query/cancel are delegated unchanged.
/// </summary>
public sealed class BinancePreTradeValidatedOrderClient : IBinanceSpotOrderClient
{
    private readonly IBinanceSpotOrderClient inner;
    private readonly IBinanceSymbolTradingRulesProvider rulesProvider;
    private readonly IBinanceAccountFiltersProvider? accountFiltersProvider;
    private readonly IBinanceAccountTradingStateProvider? tradingStateProvider;
    private readonly IBinanceReferencePriceProvider? referencePriceProvider;
    private readonly TimeProvider referencePriceClock;

    /// <summary>A reference price older than this cannot justify a price band.</summary>
    private static readonly TimeSpan BinanceReferencePriceMaxAge = TimeSpan.FromMinutes(1);

    public BinancePreTradeValidatedOrderClient(
        IBinanceSpotOrderClient inner,
        IBinanceSymbolTradingRulesProvider rulesProvider,
        IBinanceAccountFiltersProvider? accountFiltersProvider = null,
        IBinanceAccountTradingStateProvider? tradingStateProvider = null,
        IBinanceReferencePriceProvider? referencePriceProvider = null,
        TimeProvider? referencePriceClock = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
        this.accountFiltersProvider = accountFiltersProvider;
        this.tradingStateProvider = tradingStateProvider;
        this.referencePriceProvider = referencePriceProvider;
        this.referencePriceClock = referencePriceClock ?? TimeProvider.System;
    }

    public async Task<BinanceSpotOrderResult> SubmitAsync(
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rules = await RefreshRulesAsync(request, cancellationToken).ConfigureAwait(false);

        var symbolValidation = await ValidateSymbolFiltersAsync(rules, request, cancellationToken).ConfigureAwait(false);
        if (!symbolValidation.IsValid)
        {
            throw new BinancePreTradeValidationException(request.Symbol, symbolValidation.Violations);
        }

        var accountViolations = await ValidateAccountFiltersAsync(rules, request, cancellationToken).ConfigureAwait(false);
        if (accountViolations.Count > 0)
        {
            throw new BinancePreTradeValidationException(request.Symbol, accountViolations);
        }

        // The current account, symbol and reference-price evidence is not yet
        // sufficient to authorize a submission safely. Do not bypass this gate
        // until the missing contracts and their recovery tests are verified.
        throw new BinancePreTradeValidationException(request.Symbol,
            [new("PRETRADE_EVIDENCE_UNVERIFIED", "Account and exchange filter evidence is not sufficiently verified.")]);
    }

    /// <summary>
    /// Refreshes venue rules immediately before the external boundary: a stale
    /// cache is insufficient after delayed outbox delivery or a filter change.
    /// </summary>
    private async Task<BinanceSymbolTradingRules> RefreshRulesAsync(
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken)
    {
        var rules = await rulesProvider.RefreshAsync(request.Symbol, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(rules.Symbol, request.Symbol, StringComparison.Ordinal))
        {
            throw new BinancePreTradeValidationException(request.Symbol,
                [new("SYMBOL_RULES_MISMATCH", "Refreshed rules do not match the order symbol.")]);
        }

        if (request.Side is not (BinanceSpotOrderSide.Buy or BinanceSpotOrderSide.Sell))
        {
            throw new BinancePreTradeValidationException(request.Symbol,
                [new("ORDER_SIDE_UNSUPPORTED", "Order side is not supported by the pre-trade boundary.")]);
        }

        return rules;
    }

    private async Task<BinanceOrderValidationResult> ValidateSymbolFiltersAsync(
        BinanceSymbolTradingRules rules,
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken)
    {
        decimal? referencePrice = null;
        if (HasPercentPriceFilter(rules))
        {
            referencePrice = await ResolveReferencePriceAsync(request.Symbol, cancellationToken).ConfigureAwait(false);
        }

        var typeName = request.Type switch
        {
            BinanceSpotOrderType.Limit => "LIMIT",
            BinanceSpotOrderType.Market => "MARKET",
            _ => throw new BinancePreTradeValidationException(request.Symbol,
                [new("ORDER_TYPE_UNSUPPORTED", "Order type is not supported by the pre-trade boundary.")])
        };

        // exchangeInfo advertises the order types the symbol accepts.
        if (rules.AllowedOrderTypes is not { Count: > 0 } allowed)
        {
            return new(false, [new("ORDER_TYPE_METADATA_UNAVAILABLE", "Symbol order types are missing.")]);
        }

        if (!allowed.Contains(typeName, StringComparer.Ordinal))
        {
            return new(false, [new("ORDER_TYPE_NOT_ALLOWED", $"Symbol does not accept {typeName} orders.")]);
        }

        return request.Type switch
        {
            BinanceSpotOrderType.Limit => ValidateLimitOrder(rules, request, referencePrice),
            _ => ValidateMarketOrder(rules, request.Quantity)
        };
    }

    /// <summary>
    /// Account evidence is mandatory: a symbol check alone is not proof of
    /// compliance, including for risk-reducing sells.
    /// </summary>
    private async Task<IReadOnlyList<BinanceOrderFilterViolation>> ValidateAccountFiltersAsync(
        BinanceSymbolTradingRules rules,
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (accountFiltersProvider is null || tradingStateProvider is null)
        {
            return [new("ACCOUNT_EXCHANGE_FILTERS_UNVERIFIED", "Exchange and account filter evidence is unavailable.")];
        }

        if (string.IsNullOrWhiteSpace(rules.BaseAsset) || string.IsNullOrWhiteSpace(rules.QuoteAsset))
        {
            return [new("SYMBOL_ASSET_METADATA_UNAVAILABLE", "Base and quote assets are required to verify account filters.")];
        }

        var accountFilters = await accountFiltersProvider
            .GetAccountFiltersAsync(request.Symbol, cancellationToken).ConfigureAwait(false);
        var state = await ReadTradingStateAsync(rules, request, cancellationToken).ConfigureAwait(false);
        if (state.CanTrade is null)
        {
            return [new("ACCOUNT_STATE_UNAVAILABLE", "Account trading state could not be verified.")];
        }

        if (!state.IsConsistent)
        {
            return [new("ACCOUNT_STATE_INCONSISTENT",
                "Account and open-order observations were too far apart to describe one state.")];
        }
        if (state.CanTrade is false)
        {
            // The venue reports the account as unable to trade; stop before the
            // adapter can attempt a submission.
            return [new("ACCOUNT_CANNOT_TRADE", "The exchange reports canTrade=false for this account.")];
        }

        return BinanceAccountFilterEvaluator.Evaluate(
            accountFilters, rules.BaseAsset, rules.QuoteAsset, request.Type,
            request.Quantity, request.Price, state.OpenOrders, request.Side, state.BasePosition,
            ReadMaxPosition(rules), state.SymbolOrders, ReadMaxSymbolOrders(rules), rules.SymbolFilters);
    }

    /// <summary>
    /// Returns nulls rather than failing so the evaluator reports the specific
    /// missing evidence instead of a generic transport error.
    /// </summary>
    private async Task<TradingStateEvidence> ReadTradingStateAsync(
        BinanceSymbolTradingRules rules,
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await tradingStateProvider!
                .GetTradingStateAsync(request.Symbol, rules.BaseAsset, cancellationToken).ConfigureAwait(false);
            return new TradingStateEvidence(
                state.OpenOrderCount, state.SymbolOpenOrderCount, state.BasePositionQuantity, state.CanTrade, state.IsConsistent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Unknown values are reported by the evaluator with specific codes.
            return new TradingStateEvidence(null, null, null, null, false);
        }
    }

    private readonly record struct TradingStateEvidence(
        long? OpenOrders,
        long? SymbolOrders,
        decimal? BasePosition,
        bool? CanTrade,
        bool IsConsistent);

    private static decimal? ReadMaxPosition(BinanceSymbolTradingRules rules) =>
        (rules.SymbolFilters ?? [])
            .Where(static filter => filter?.FilterType is "MAX_POSITION")
            .Select(static filter => decimal.TryParse(filter.MaxPosition, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null)
            .FirstOrDefault();

    private static long? ReadMaxSymbolOrders(BinanceSymbolTradingRules rules) =>
        (rules.SymbolFilters ?? [])
            .Where(static filter => filter?.FilterType is "MAX_NUM_ORDERS")
            .Select(static filter => filter.MaxNumOrders)
            .FirstOrDefault();

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

    private static bool HasPercentPriceFilter(BinanceSymbolTradingRules rules) =>
        (rules.SymbolFilters ?? []).Any(static filter =>
            filter?.FilterType is "PERCENT_PRICE" or "PERCENT_PRICE_BY_SIDE");

    /// <summary>
    /// Resolves a reference price that provably belongs to the ordered symbol and
    /// is not older than the market-data policy. Anything else returns null so the
    /// percentage-band evaluator fails closed with a specific reason.
    /// </summary>
    private async Task<decimal?> ResolveReferencePriceAsync(string symbol, CancellationToken cancellationToken)
    {
        if (referencePriceProvider is null)
        {
            return null;
        }

        BinanceReferencePriceEvidence? evidence;
        try
        {
            evidence = await referencePriceProvider.GetReferencePriceAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (evidence is null ||
            !string.Equals(evidence.Symbol, symbol, StringComparison.Ordinal))
        {
            return null;
        }

        var now = referencePriceClock.GetUtcNow();
        return evidence.VenueTimestampUtc <= now && now - evidence.VenueTimestampUtc <= BinanceReferencePriceMaxAge
            ? evidence.Price
            : null;
    }

    private static BinanceOrderValidationResult ValidateLimitOrder(
        BinanceSymbolTradingRules rules,
        BinanceSpotOrderRequest request,
        decimal? referencePrice)
    {
        var basic = rules.ValidateLimitOrder(request.Price!.Value, request.Quantity);
        var violations = new List<BinanceOrderFilterViolation>(basic.Violations);
        var filters = rules.SymbolFilters;
        if (filters is null || filters.Count == 0)
        {
            violations.Add(new("FILTER_METADATA_UNAVAILABLE", "Symbol filter metadata is missing."));
            return new(false, violations);
        }

        var priceCount = 0;
        var lotCount = 0;
        var minNotionalCount = 0;
        var notionalCount = 0;
        foreach (var filter in filters)
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FilterType))
            {
                violations.Add(new("FILTER_METADATA_UNAVAILABLE", "Symbol filter metadata is incomplete."));
                continue;
            }

            switch (filter.FilterType)
            {
                case "PRICE_FILTER": priceCount++; break;
                case "LOT_SIZE": lotCount++; break;
                case "MIN_NOTIONAL": minNotionalCount++; break;
                case "NOTIONAL": notionalCount++; break;
                // These filters apply to order kinds or features not submitted by this boundary.
                case "MARKET_LOT_SIZE":
                case "ICEBERG_PARTS":
                case "MAX_NUM_ORDERS":
                case "MAX_NUM_ALGO_ORDERS":
                case "MAX_NUM_ICEBERG_ORDERS":
                case "MAX_NUM_ORDER_AMENDS":
                case "MAX_NUM_ORDER_LISTS":
                case "TRAILING_DELTA":
                    break;
                // Position limits are evaluated against account state below;
                // SELL is not gated by MAX_POSITION per the venue definition.
                case "MAX_POSITION":
                    break;
                case "PERCENT_PRICE":
                case "PERCENT_PRICE_BY_SIDE":
                    violations.AddRange(BinancePercentPriceEvaluator.Evaluate(
                        filter, request.Side, request.Price!.Value, referencePrice));
                    break;
                default:
                    violations.Add(new("UNSUPPORTED_APPLICABLE_FILTER", "An advertised filter cannot be verified for this order."));
                    break;
            }
        }

        if (priceCount != 1 || lotCount != 1 || minNotionalCount > 1 || notionalCount > 1 || minNotionalCount + notionalCount == 0)
        {
            violations.Add(new("FILTER_METADATA_UNAVAILABLE", "Required symbol filter metadata is missing or ambiguous."));
        }

        return new(violations.Count == 0, violations);
    }

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
