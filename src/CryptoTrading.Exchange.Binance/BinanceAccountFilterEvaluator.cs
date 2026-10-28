using System.Globalization;

namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Evaluates the account-level evidence returned by <c>GET /api/v3/myFilters</c>
/// together with current order counts. It is deliberately conservative: any
/// filter this adapter cannot interpret, and any missing input, produces a
/// violation instead of an implicit approval.
/// </summary>
public static class BinanceAccountFilterEvaluator
{
    public static IReadOnlyList<BinanceOrderFilterViolation> Evaluate(
        BinanceAccountFilters? accountFilters,
        string baseAsset,
        string quoteAsset,
        BinanceSpotOrderType orderType,
        decimal quantity,
        decimal? price,
        long? openOrderCount,
        BinanceSpotOrderSide side = BinanceSpotOrderSide.Buy,
        decimal? basePosition = null,
        decimal? maxPosition = null,
        long? symbolOpenOrderCount = null,
        long? maxSymbolOrders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteAsset);

        if (accountFilters is null)
        {
            return [new("ACCOUNT_FILTERS_UNAVAILABLE", "Account filter evidence was not retrieved.")];
        }

        var violations = new List<BinanceOrderFilterViolation>();
        if (!accountFilters.HasOnlyKnownFilterTypes)
        {
            violations.Add(new("ACCOUNT_FILTERS_UNSUPPORTED", "An account filter type cannot be verified by this adapter."));
        }

        foreach (var filter in accountFilters.ExchangeFilters ?? [])
        {
            switch (filter.FilterType)
            {
                case "EXCHANGE_MAX_NUM_ORDERS":
                    if (filter.MaxNumOrders is not { } maximum)
                    {
                        violations.Add(new("ACCOUNT_FILTERS_UNSUPPORTED", "EXCHANGE_MAX_NUM_ORDERS has no usable limit."));
                        break;
                    }

                    if (openOrderCount is not { } open)
                    {
                        violations.Add(new("ACCOUNT_ORDER_COUNT_UNAVAILABLE", "Open order count is required to verify EXCHANGE_MAX_NUM_ORDERS."));
                        break;
                    }

                    if (open + 1 > maximum)
                    {
                        violations.Add(new("EXCHANGE_MAX_NUM_ORDERS_EXCEEDED",
                            string.Create(CultureInfo.InvariantCulture, $"Open orders {open} plus this order exceed {maximum}.")));
                    }

                    break;

                // The adapter never submits algorithmic orders, so these limits do
                // not gate a plain LIMIT/MARKET request.
                case "EXCHANGE_MAX_NUM_ALGO_ORDERS":
                    break;

                default:
                    violations.Add(new("ACCOUNT_FILTERS_UNSUPPORTED", "An exchange filter type cannot be verified by this adapter."));
                    break;
            }
        }

        foreach (var filter in accountFilters.AssetFilters ?? [])
        {
            if (filter.FilterType is not "MAX_ASSET")
            {
                violations.Add(new("ACCOUNT_FILTERS_UNSUPPORTED", "An asset filter type cannot be verified by this adapter."));
                continue;
            }

            if (!TryParseLimit(filter.Limit, out var limit) || limit <= 0m || string.IsNullOrWhiteSpace(filter.Asset))
            {
                violations.Add(new("ACCOUNT_FILTERS_UNSUPPORTED", "MAX_ASSET evidence is incomplete."));
                continue;
            }

            if (string.Equals(filter.Asset, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                if (quantity > limit)
                {
                    violations.Add(new("MAX_ASSET_EXCEEDED",
                        string.Create(CultureInfo.InvariantCulture, $"Order quantity {quantity} exceeds MAX_ASSET limit {limit} for {filter.Asset}.")));
                }

                continue;
            }

            if (string.Equals(filter.Asset, quoteAsset, StringComparison.OrdinalIgnoreCase))
            {
                if (orderType is BinanceSpotOrderType.Market || price is not { } referencePrice || referencePrice <= 0m)
                {
                    violations.Add(new("MAX_ASSET_NOTIONAL_UNVERIFIABLE", "MAX_ASSET in quote asset requires a reference price."));
                    continue;
                }

                var notional = quantity * referencePrice;
                if (notional > limit)
                {
                    violations.Add(new("MAX_ASSET_EXCEEDED",
                        string.Create(CultureInfo.InvariantCulture, $"Order notional {notional} exceeds MAX_ASSET limit {limit} for {filter.Asset}.")));
                }

                continue;
            }

            // The filter targets an asset unrelated to this symbol's base/quote.
            // It cannot be attributed to this order, so it must not be ignored.
            violations.Add(new("MAX_ASSET_ASSET_UNRELATED", "MAX_ASSET references an asset that does not match this symbol."));
        }

        // MAX_NUM_ORDERS limits open orders per symbol, counting algo orders too.
        if (maxSymbolOrders is { } symbolLimit)
        {
            if (symbolLimit <= 0)
            {
                violations.Add(new("FILTER_METADATA_UNAVAILABLE", "MAX_NUM_ORDERS evidence is incomplete."));
            }
            else if (symbolOpenOrderCount is not { } symbolOpen)
            {
                violations.Add(new("SYMBOL_ORDER_COUNT_UNAVAILABLE", "Per-symbol open order count is required to verify MAX_NUM_ORDERS."));
            }
            else if (symbolOpen + 1 > symbolLimit)
            {
                violations.Add(new("MAX_NUM_ORDERS_EXCEEDED",
                    string.Create(CultureInfo.InvariantCulture, $"Open orders {symbolOpen} plus this order exceed {symbolLimit}.")));
            }
        }

        // MAX_POSITION rejects BUY orders whose resulting position would exceed
        // the limit: position = free + locked + open BUY quantity.
        if (maxPosition is { } positionLimit && side is BinanceSpotOrderSide.Buy)
        {
            if (positionLimit <= 0m)
            {
                violations.Add(new("FILTER_METADATA_UNAVAILABLE", "MAX_POSITION evidence is incomplete."));
            }
            else if (basePosition is not { } current)
            {
                violations.Add(new("MAX_POSITION_UNAVAILABLE", "Current base-asset position is required to verify MAX_POSITION."));
            }
            else if (current > positionLimit || current + quantity > positionLimit)
            {
                violations.Add(new("MAX_POSITION_EXCEEDED",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Position {current} plus order {quantity} exceeds MAX_POSITION {positionLimit}.")));
            }
        }

        return violations;
    }

    private static bool TryParseLimit(string? raw, out decimal limit) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out limit);
}
