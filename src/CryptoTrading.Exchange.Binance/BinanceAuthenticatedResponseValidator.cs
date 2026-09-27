using System.Globalization;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Validates authenticated Binance payloads at the trust boundary.
/// A syntactically valid JSON response is not considered trustworthy until all
/// required fields and non-negative numeric invariants have been checked.
/// </summary>
internal static class BinanceAuthenticatedResponseValidator
{
    private static readonly HashSet<string> ReadOnlyPermissions = ["SPOT"];

    public static BinanceAccountSnapshot Validate(BinanceAccountSnapshot? account, bool allowWithdrawCapability = false)
    {
        if (account is null)
        {
            throw Invalid("account response cannot be null");
        }

        RequireText(account.AccountType, "accountType");
        RequireNonNegative(account.MakerCommission, "makerCommission");
        RequireNonNegative(account.TakerCommission, "takerCommission");
        RequireNonNegative(account.BuyerCommission, "buyerCommission");
        RequireNonNegative(account.SellerCommission, "sellerCommission");
        RequireNonNegative(account.UpdateTime, "updateTime");
        if (account.CanWithdraw && !allowWithdrawCapability)
        {
            throw Invalid("account canWithdraw must be false for a read-only integration");
        }

        if (account.Balances is null)
        {
            throw Invalid("balances is required");
        }

        var assets = new HashSet<string>(StringComparer.Ordinal);
        var validBalances = new List<BinanceBalance>(account.Balances.Count);
        foreach (var balance in account.Balances)
        {
            if (balance is null)
            {
                throw Invalid("balance cannot be null");
            }

            ParseNonNegativeDecimal(balance.Free, "free");
            ParseNonNegativeDecimal(balance.Locked, "locked");

            if (allowWithdrawCapability)
            {
                // In Binance Testnet, skip test tokens with non-ASCII or single-letter names (e.g. Chinese test tokens)
                if (string.IsNullOrWhiteSpace(balance.Asset) ||
                    balance.Asset.Length < 2 ||
                    balance.Asset.Length > 20 ||
                    balance.Asset.Any(static character => !char.IsAsciiLetterOrDigit(character)))
                {
                    continue;
                }
            }

            var normalizedAsset = NormalizeAsset(balance.Asset, "asset");
            if (!assets.Add(normalizedAsset))
            {
                throw Invalid("balances cannot contain duplicate assets");
            }

            validBalances.Add(balance);
        }

        if (account.Permissions is not null)
        {
            var permissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in account.Permissions)
            {
                var normalizedPermission = RequireText(permission, "permission").ToUpperInvariant();
                if (!ReadOnlyPermissions.Contains(normalizedPermission))
                {
                    throw Invalid($"permission '{normalizedPermission}' is not allowed for a read-only integration");
                }

                if (!permissions.Add(normalizedPermission))
                {
                    throw Invalid("permissions cannot contain duplicates");
                }
            }
        }

        return validBalances.Count == account.Balances.Count
            ? account
            : account with { Balances = validBalances };
    }

    public static BinanceBalance Validate(BinanceBalance? balance)
    {
        if (balance is null)
        {
            throw Invalid("balance cannot be null");
        }

        NormalizeAsset(balance.Asset, "asset");
        ParseNonNegativeDecimal(balance.Free, "free");
        ParseNonNegativeDecimal(balance.Locked, "locked");
        return balance;
    }

    public static IReadOnlyList<BinanceOpenOrder> Validate(IReadOnlyList<BinanceOpenOrder>? orders)
    {
        if (orders is null)
        {
            throw Invalid("open orders response cannot be null");
        }

        var orderIds = new HashSet<long>();
        foreach (var order in orders)
        {
            Validate(order);
            if (!orderIds.Add(order.OrderId))
            {
                throw Invalid("open orders cannot contain duplicate orderId values");
            }
        }

        return orders;
    }

    public static BinanceOpenOrder Validate(BinanceOpenOrder? order)
    {
        if (order is null)
        {
            throw Invalid("open order cannot be null");
        }

        NormalizeSymbol(order.Symbol, "symbol");
        RequireNonNegative(order.OrderId, "orderId");
        RequireText(order.ClientOrderId, "clientOrderId");
        var originalQuantity = ParseNonNegativeDecimal(order.OriginalQuantity, "origQty");
        var executedQuantity = ParseNonNegativeDecimal(order.ExecutedQuantity, "executedQty");
        ParseNonNegativeDecimal(order.Price, "price");
        ParseNonNegativeDecimal(order.CumulativeQuoteQuantity, "cummulativeQuoteQty");
        if (executedQuantity > originalQuantity)
        {
            throw Invalid("executedQty cannot exceed origQty");
        }

        RequireText(order.Status, "status");
        RequireText(order.Type, "type");
        RequireText(order.Side, "side");
        RequireNonNegative(order.Time, "time");
        RequireNonNegative(order.UpdateTime, "updateTime");
        if (order.TimeInForce is not null)
        {
            RequireText(order.TimeInForce, "timeInForce");
        }

        return order;
    }

    public static IReadOnlyList<BinanceTrade> Validate(IReadOnlyList<BinanceTrade>? trades)
    {
        if (trades is null)
        {
            throw Invalid("trades response cannot be null");
        }

        var tradeIds = new HashSet<long>();
        foreach (var trade in trades)
        {
            Validate(trade);
            if (!tradeIds.Add(trade.Id))
            {
                throw Invalid("trades cannot contain duplicate id values");
            }
        }

        return trades;
    }

    public static BinanceCommissionResponse Validate(BinanceCommissionResponse? response, string expectedSymbol)
    {
        if (response is null || response.StandardCommission is null || response.SpecialCommission is null ||
            response.TaxCommission is null || response.Discount is null)
        {
            throw Invalid("commission response is incomplete");
        }

        var symbol = NormalizeSymbol(response.Symbol, "symbol");
        if (!string.Equals(symbol, NormalizeSymbol(expectedSymbol, "expectedSymbol"), StringComparison.Ordinal))
        {
            throw Invalid("commission response symbol does not match the request");
        }

        Validate(response.StandardCommission, "standardCommission");
        Validate(response.SpecialCommission, "specialCommission");
        Validate(response.TaxCommission, "taxCommission");
        Validate(response.Discount);
        return response;
    }

    private static void Validate(BinanceCommissionRateSet? rates, string fieldName)
    {
        if (rates is null)
        {
            throw Invalid($"{fieldName} is required");
        }

        ParseNonNegativeDecimal(rates.Maker, $"{fieldName}.maker");
        ParseNonNegativeDecimal(rates.Taker, $"{fieldName}.taker");
        ParseNonNegativeDecimal(rates.Buyer, $"{fieldName}.buyer");
        ParseNonNegativeDecimal(rates.Seller, $"{fieldName}.seller");
    }

    private static void Validate(BinanceCommissionDiscount? discount)
    {
        if (discount is null)
        {
            throw Invalid("discount is required");
        }

        if (discount.EnabledForAccount && discount.EnabledForSymbol)
        {
            if (!string.IsNullOrWhiteSpace(discount.DiscountAsset))
            {
                NormalizeAsset(discount.DiscountAsset, "discountAsset");
            }

            if (!string.IsNullOrWhiteSpace(discount.Discount))
            {
                var rate = ParseNonNegativeDecimal(discount.Discount, "discount");
                if (rate > 1m)
                {
                    throw Invalid("discount must be between zero and one");
                }

                if (rate > 0m && string.IsNullOrWhiteSpace(discount.DiscountAsset))
                {
                    throw Invalid("discountAsset is required when discount is positive");
                }
            }
        }
    }

    public static BinanceTrade Validate(BinanceTrade? trade)
    {
        if (trade is null)
        {
            throw Invalid("trade cannot be null");
        }

        NormalizeSymbol(trade.Symbol, "symbol");
        RequireNonNegative(trade.Id, "id");
        RequireNonNegative(trade.OrderId, "orderId");
        ParseNonNegativeDecimal(trade.Price, "price");
        ParseNonNegativeDecimal(trade.Quantity, "qty");
        ParseNonNegativeDecimal(trade.QuoteQuantity, "quoteQty");
        ParseNonNegativeDecimal(trade.Commission, "commission");
        NormalizeAsset(trade.CommissionAsset, "commissionAsset");
        RequireNonNegative(trade.Time, "time");
        return trade;
    }

    internal static string NormalizeSymbol(string? value, string fieldName)
    {
        var normalized = RequireText(value, fieldName).ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw Invalid($"{fieldName} must contain only ASCII letters and digits and be at most 20 characters");
        }

        return normalized;
    }

    private static string NormalizeAsset(string? value, string fieldName)
    {
        try
        {
            return BinanceAssetNormalizer.Normalize(value, fieldName);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(exception.Message);
        }
    }

    private static decimal ParseNonNegativeDecimal(string? value, string fieldName)
    {
        var text = RequireText(value, fieldName);
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0m)
        {
            throw Invalid($"{fieldName} must be a non-negative invariant decimal");
        }

        return parsed;
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{fieldName} is required");
        }

        return value.Trim();
    }

    private static void RequireNonNegative(long value, string fieldName)
    {
        if (value < 0)
        {
            throw Invalid($"{fieldName} cannot be negative");
        }
    }

    private static void RequireNonNegative(int value, string fieldName)
    {
        if (value < 0)
        {
            throw Invalid($"{fieldName} cannot be negative");
        }
    }

    private static JsonException Invalid(string message) => new($"Binance authenticated response validation failed: {message}.");
}
