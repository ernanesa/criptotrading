using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoTrading.Observability;

namespace CryptoTrading.Exchange.Binance;

/// <summary>Explicit Spot order side accepted by the isolated order adapter.</summary>
public enum BinanceSpotOrderSide
{
    Buy,
    Sell,
}

/// <summary>Explicit Spot order type accepted by the isolated order adapter.</summary>
public enum BinanceSpotOrderType
{
    Limit,
    Market,
}

/// <summary>
/// Validated order intent. It has no exchange side effects and can be safely
/// constructed while the adapter remains disabled.
/// </summary>
public sealed class BinanceSpotOrderRequest
{
    public BinanceSpotOrderRequest(
        string symbol,
        BinanceSpotOrderSide side,
        BinanceSpotOrderType type,
        decimal quantity,
        string clientOrderId,
        decimal? price = null,
        string timeInForce = "GTC")
    {
        Symbol = NormalizeSymbol(symbol);
        if (side is not (BinanceSpotOrderSide.Buy or BinanceSpotOrderSide.Sell))
        {
            throw new ArgumentOutOfRangeException(nameof(side), "Order side is not supported.");
        }
        if (type is not (BinanceSpotOrderType.Limit or BinanceSpotOrderType.Market))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Order type is not supported.");
        }
        Side = side;
        Type = type;
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Order quantity must be greater than zero.");
        }

        Quantity = quantity;
        ClientOrderId = NormalizeClientOrderId(clientOrderId);
        if (type == BinanceSpotOrderType.Limit)
        {
            if (price is not > 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(price), "Limit orders require a positive price.");
            }

            Price = price;
            TimeInForce = NormalizeTimeInForce(timeInForce);
        }
        else
        {
            if (price is not null)
            {
                throw new ArgumentException("Market orders cannot specify a limit price.", nameof(price));
            }

            Price = null;
            TimeInForce = null;
        }
    }

    public string Symbol { get; }
    public BinanceSpotOrderSide Side { get; }
    public BinanceSpotOrderType Type { get; }
    public decimal Quantity { get; }
    public decimal? Price { get; }
    public string ClientOrderId { get; }
    public string? TimeInForce { get; }

    internal static string NormalizeSymbol(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > 20 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Symbol must contain only ASCII letters and digits and be at most 20 characters.", nameof(value));
        }

        return normalized;
    }

    internal static string NormalizeClientOrderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 36 || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Client order ID must be 1-36 ASCII letters, digits, '-' or '_'.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeTimeInForce(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "GTC" or "IOC" or "FOK"
            ? normalized
            : throw new ArgumentException("Limit time-in-force must be GTC, IOC or FOK.", nameof(value));
    }
}

/// <summary>Minimal validated order snapshot returned by submit/query/cancel.</summary>
public sealed record BinanceSpotOrderResult(
    long OrderId,
    string Symbol,
    string ClientOrderId,
    string Status,
    decimal Price,
    decimal OriginalQuantity,
    decimal ExecutedQuantity,
    long TransactTimeMilliseconds);

/// <summary>
/// Validated request for Binance Spot <c>PUT /api/v3/order/amend/keepPriority</c> or WS <c>order.amend.keepPriority</c>.
/// Reduces an open order's quantity in-place without losing queue priority on the matching engine.
/// </summary>
public sealed class BinanceSpotAmendKeepPriorityRequest
{
    public BinanceSpotAmendKeepPriorityRequest(
        string symbol,
        string origClientOrderId,
        decimal newQuantity,
        string? newClientOrderId = null)
    {
        Symbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        OrigClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(origClientOrderId);
        if (newQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(newQuantity), "Amended order quantity must be greater than zero.");
        }

        NewQuantity = newQuantity;
        NewClientOrderId = newClientOrderId is null ? null : BinanceSpotOrderRequest.NormalizeClientOrderId(newClientOrderId);
    }

    public string Symbol { get; }
    public string OrigClientOrderId { get; }
    public decimal NewQuantity { get; }
    public string? NewClientOrderId { get; }
}

public sealed record BinanceSpotAmendKeepPriorityResult(
    long TransactTimeMilliseconds,
    long ExecutionId,
    BinanceSpotOrderResult AmendedOrder);

/// <summary>
/// Validated request for Binance Spot <c>POST /api/v3/order/cancelReplace</c> or WS <c>order.cancelReplace</c>.
/// Atomically cancels an open order and places a replacement order in a single matching-engine step.
/// </summary>
public sealed class BinanceSpotCancelReplaceRequest
{
    public BinanceSpotCancelReplaceRequest(
        string symbol,
        BinanceSpotOrderSide side,
        BinanceSpotOrderType type,
        string cancelOrigClientOrderId,
        string newClientOrderId,
        decimal quantity,
        decimal? price = null,
        string timeInForce = "GTC",
        string cancelReplaceMode = "STOP_ON_FAILURE",
        string? cancelRestrictions = null)
    {
        Symbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        Side = side;
        Type = type;
        CancelOrigClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(cancelOrigClientOrderId);
        NewClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(newClientOrderId);
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Replacement quantity must be greater than zero.");
        }

        Quantity = quantity;
        if (type == BinanceSpotOrderType.Limit)
        {
            if (price is not > 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(price), "Limit replacement orders require a positive price.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(timeInForce);
            Price = price;
            TimeInForce = timeInForce.Trim().ToUpperInvariant() is "GTC" or "IOC" or "FOK"
                ? timeInForce.Trim().ToUpperInvariant()
                : throw new ArgumentException("Limit time-in-force must be GTC, IOC or FOK.", nameof(timeInForce));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cancelReplaceMode);
        CancelReplaceMode = cancelReplaceMode.Trim().ToUpperInvariant() is "STOP_ON_FAILURE" or "ALLOW_FAILURE"
            ? cancelReplaceMode.Trim().ToUpperInvariant()
            : throw new ArgumentException("CancelReplaceMode must be STOP_ON_FAILURE or ALLOW_FAILURE.", nameof(cancelReplaceMode));

        if (cancelRestrictions is not null)
        {
            var normalizedRestriction = cancelRestrictions.Trim().ToUpperInvariant();
            if (normalizedRestriction is not ("ONLY_NEW" or "ONLY_PARTIALLY_FILLED"))
            {
                throw new ArgumentException("CancelRestrictions must be ONLY_NEW or ONLY_PARTIALLY_FILLED.", nameof(cancelRestrictions));
            }

            CancelRestrictions = normalizedRestriction;
        }
    }

    public string Symbol { get; }
    public BinanceSpotOrderSide Side { get; }
    public BinanceSpotOrderType Type { get; }
    public string CancelOrigClientOrderId { get; }
    public string NewClientOrderId { get; }
    public decimal Quantity { get; }
    public decimal? Price { get; }
    public string? TimeInForce { get; }
    public string CancelReplaceMode { get; }
    public string? CancelRestrictions { get; }
}

public sealed record BinanceSpotCancelReplaceResult(
    string CancelResult,
    string NewOrderResult,
    BinanceSpotOrderResult CancelResponse,
    BinanceSpotOrderResult NewOrderResponse);

/// <summary>
/// Validated request for exchange-native protective OCO (<c>POST /api/v3/orderList/oco</c> / WS <c>orderList.place.oco</c>)
/// pairing a <c>LIMIT_MAKER</c> take-profit leg above with a <c>STOP_LOSS_LIMIT</c> protective stop leg below.
/// </summary>
public sealed class BinanceSpotOcoOrderRequest
{
    public BinanceSpotOcoOrderRequest(
        string symbol,
        BinanceSpotOrderSide side,
        decimal quantity,
        string listClientOrderId,
        string aboveClientOrderId,
        decimal abovePrice,
        string belowClientOrderId,
        decimal belowStopPrice,
        decimal belowLimitPrice,
        string belowTimeInForce = "GTC")
    {
        Symbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        Side = side;
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "OCO quantity must be greater than zero.");
        }

        if (abovePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(abovePrice), "OCO above (take-profit) price must be positive.");
        }

        if (belowStopPrice <= 0m || belowLimitPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(belowStopPrice), "OCO below (stop-loss) trigger and limit prices must be positive.");
        }

        if (side == BinanceSpotOrderSide.Sell && abovePrice <= belowStopPrice)
        {
            throw new ArgumentException("For SELL OCO, above take-profit price must exceed below stop-loss trigger price.");
        }

        Quantity = quantity;
        ListClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(listClientOrderId);
        AboveClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(aboveClientOrderId);
        AbovePrice = abovePrice;
        BelowClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(belowClientOrderId);
        BelowStopPrice = belowStopPrice;
        BelowLimitPrice = belowLimitPrice;
        ArgumentException.ThrowIfNullOrWhiteSpace(belowTimeInForce);
        BelowTimeInForce = belowTimeInForce.Trim().ToUpperInvariant() is "GTC" or "IOC" or "FOK"
            ? belowTimeInForce.Trim().ToUpperInvariant()
            : throw new ArgumentException("Below time-in-force must be GTC, IOC or FOK.", nameof(belowTimeInForce));
    }

    public string Symbol { get; }
    public BinanceSpotOrderSide Side { get; }
    public decimal Quantity { get; }
    public string ListClientOrderId { get; }
    public string AboveClientOrderId { get; }
    public decimal AbovePrice { get; }
    public string BelowClientOrderId { get; }
    public decimal BelowStopPrice { get; }
    public decimal BelowLimitPrice { get; }
    public string BelowTimeInForce { get; }
}

/// <summary>
/// Validated request for exchange-native OTOCO (<c>POST /api/v3/orderList/otoco</c> / WS <c>orderList.place.otoco</c>)
/// submitting a working entry order that automatically triggers a paired OCO take-profit + stop-loss on fill.
/// </summary>
public sealed class BinanceSpotOtocoOrderRequest
{
    public BinanceSpotOtocoOrderRequest(
        string symbol,
        string listClientOrderId,
        BinanceSpotOrderSide workingSide,
        decimal workingPrice,
        decimal workingQuantity,
        string workingClientOrderId,
        BinanceSpotOrderSide pendingSide,
        decimal pendingQuantity,
        string pendingAboveClientOrderId,
        decimal pendingAbovePrice,
        string pendingBelowClientOrderId,
        decimal pendingBelowStopPrice,
        decimal pendingBelowLimitPrice,
        string workingTimeInForce = "GTC",
        string pendingBelowTimeInForce = "GTC")
    {
        Symbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        ListClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(listClientOrderId);
        WorkingSide = workingSide;
        if (workingPrice <= 0m || workingQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(workingPrice), "Working order price and quantity must be positive.");
        }

        if (pendingQuantity <= 0m || pendingAbovePrice <= 0m || pendingBelowStopPrice <= 0m || pendingBelowLimitPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingQuantity), "Pending OCO leg prices and quantity must be positive.");
        }

        if (pendingSide == BinanceSpotOrderSide.Sell && pendingAbovePrice <= pendingBelowStopPrice)
        {
            throw new ArgumentException("For SELL OTOCO pending legs, above take-profit price must exceed below stop-loss trigger price.");
        }

        WorkingPrice = workingPrice;
        WorkingQuantity = workingQuantity;
        WorkingClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(workingClientOrderId);
        PendingSide = pendingSide;
        PendingQuantity = pendingQuantity;
        PendingAboveClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(pendingAboveClientOrderId);
        PendingAbovePrice = pendingAbovePrice;
        PendingBelowClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(pendingBelowClientOrderId);
        PendingBelowStopPrice = pendingBelowStopPrice;
        PendingBelowLimitPrice = pendingBelowLimitPrice;
        ArgumentException.ThrowIfNullOrWhiteSpace(workingTimeInForce);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingBelowTimeInForce);
        WorkingTimeInForce = workingTimeInForce.Trim().ToUpperInvariant() is "GTC" or "IOC" or "FOK"
            ? workingTimeInForce.Trim().ToUpperInvariant()
            : throw new ArgumentException("Working time-in-force must be GTC, IOC or FOK.", nameof(workingTimeInForce));
        PendingBelowTimeInForce = pendingBelowTimeInForce.Trim().ToUpperInvariant() is "GTC" or "IOC" or "FOK"
            ? pendingBelowTimeInForce.Trim().ToUpperInvariant()
            : throw new ArgumentException("Pending below time-in-force must be GTC, IOC or FOK.", nameof(pendingBelowTimeInForce));
    }

    public string Symbol { get; }
    public string ListClientOrderId { get; }
    public BinanceSpotOrderSide WorkingSide { get; }
    public decimal WorkingPrice { get; }
    public decimal WorkingQuantity { get; }
    public string WorkingClientOrderId { get; }
    public BinanceSpotOrderSide PendingSide { get; }
    public decimal PendingQuantity { get; }
    public string PendingAboveClientOrderId { get; }
    public decimal PendingAbovePrice { get; }
    public string PendingBelowClientOrderId { get; }
    public decimal PendingBelowStopPrice { get; }
    public decimal PendingBelowLimitPrice { get; }
    public string WorkingTimeInForce { get; }
    public string PendingBelowTimeInForce { get; }
}

public sealed record BinanceSpotOrderListResult(
    long OrderListId,
    string ContingencyType,
    string ListStatusType,
    string ListOrderStatus,
    string ListClientOrderId,
    long TransactionTimeMilliseconds,
    string Symbol,
    IReadOnlyList<BinanceSpotOrderResult> OrderReports);

/// <summary>Testnet-only credentials and safety switch for order mutation.</summary>
public sealed class BinanceSpotOrderClientOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;
    public Uri? RestBaseAddress { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public long RecvWindowMilliseconds { get; set; } = 5_000;

    public TimeSpan ClockDriftTolerance { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional TTL for reusing a recently observed exchange clock offset before
    /// issuing another GET /api/v3/time round-trip. Defaults to zero (always sync).
    /// </summary>
    public TimeSpan ClockSyncCacheDuration { get; set; } = TimeSpan.Zero;

    /// <summary>Must be explicitly enabled by a controlled Testnet configuration.</summary>
    public bool EnableOrderExecution { get; set; }

    internal Uri GetRestBaseAddress()
    {
        if (Environment != BinanceEnvironment.Testnet)
        {
            throw new InvalidOperationException("Spot order execution is restricted to Binance Testnet.");
        }

        var address = RestBaseAddress ?? new Uri("https://testnet.binance.vision/api/");
        return BinanceEndpointPolicy.ValidateRest(address, "testnet.binance.vision", "/api", "Spot order Testnet REST address");
    }

    internal void Validate()
    {
        _ = GetRestBaseAddress();
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API key is required and cannot contain whitespace.", nameof(ApiKey));
        }

        if (string.IsNullOrWhiteSpace(ApiSecret) || ApiSecret.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API secret is required and cannot contain whitespace.", nameof(ApiSecret));
        }

        if (RecvWindowMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RecvWindowMilliseconds));
        }

        if (ClockDriftTolerance < TimeSpan.Zero || ClockDriftTolerance > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ClockDriftTolerance));
        }
    }
}

public interface IBinanceSpotOrderClient
{
    Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default);

    Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<BinanceSpotOrderResult> CancelAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<BinanceSpotAmendKeepPriorityResult> AmendKeepPriorityAsync(
        BinanceSpotAmendKeepPriorityRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Order amend keepPriority is not supported by this order client implementation.");

    Task<BinanceSpotCancelReplaceResult> CancelReplaceAsync(
        BinanceSpotCancelReplaceRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Order cancelReplace is not supported by this order client implementation.");

    Task<BinanceSpotOrderListResult> SubmitOcoOrderListAsync(
        BinanceSpotOcoOrderRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("OCO order list submission is not supported by this order client implementation.");

    Task<BinanceSpotOrderListResult> SubmitOtocoOrderListAsync(
        BinanceSpotOtocoOrderRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("OTOCO order list submission is not supported by this order client implementation.");
}

public sealed class BinanceOrderSubmissionAmbiguousException : IOException
{
    public BinanceOrderSubmissionAmbiguousException()
        : this(string.Empty, new IOException("Binance order submission outcome is ambiguous."))
    {
    }

    public BinanceOrderSubmissionAmbiguousException(string message)
        : base(message)
    {
        ClientOrderId = string.Empty;
    }

    public BinanceOrderSubmissionAmbiguousException(string clientOrderId, Exception innerException)
        : base($"Binance order submission for clientOrderId '{clientOrderId}' is ambiguous; query the order before deciding whether to resubmit.", innerException)
    {
        ClientOrderId = clientOrderId;
    }

    public string ClientOrderId { get; }
}

/// <summary>
/// Indicates that Binance definitively rejected a submission before creating an
/// order. It is terminal for the submitted command and must not be retried.
/// </summary>
public sealed class BinanceOrderRejectedException : InvalidOperationException
{
    public BinanceOrderRejectedException()
        : this("unknown", new BinanceApiException())
    {
    }

    public BinanceOrderRejectedException(string message)
        : base(message)
    {
        ClientOrderId = "unknown";
    }

    public BinanceOrderRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ClientOrderId = "unknown";
    }

    public BinanceOrderRejectedException(string clientOrderId, BinanceApiException cause)
        : base($"Binance rejected order submission for clientOrderId '{clientOrderId}' with HTTP {(int?)RequireCause(cause).StatusCode}.", cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ClientOrderId = clientOrderId;
        StatusCode = RequireCause(cause).StatusCode;
    }

    public string ClientOrderId { get; }

    public System.Net.HttpStatusCode? StatusCode { get; }

    private static BinanceApiException RequireCause(BinanceApiException? cause) =>
        cause ?? throw new ArgumentNullException(nameof(cause));
}

/// <summary>
/// Isolated signed Spot order adapter. It is deliberately not registered by
/// the application host. Submit has no retry path: transport uncertainty is
/// surfaced as an ambiguous result requiring a client-order-id lookup.
/// </summary>
public sealed class BinanceSpotOrderClient : IBinanceSpotOrderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly BinanceSpotOrderClientOptions options;
    private readonly Uri restBaseAddress;
    private readonly TimeProvider timeProvider;
    private readonly IBinanceClockDriftEstimator clock;

    public BinanceSpotOrderClient(
        HttpClient httpClient,
        BinanceSpotOrderClientOptions options,
        TimeProvider? timeProvider = null,
        IBinanceClockDriftEstimator? clock = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
        restBaseAddress = this.options.GetRestBaseAddress();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.clock = clock ?? new BinanceClockDriftEstimator(this.timeProvider);
    }

    public async Task<BinanceSpotOrderResult> SubmitAsync(
        BinanceSpotOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureExecutionEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartFinancialActivity("submit");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", request.Symbol),
            new("side", request.Side.ToString().ToUpperInvariant()),
            new("type", request.Type.ToString().ToUpperInvariant()),
            new("quantity", FormatDecimal(request.Quantity)),
            new("newClientOrderId", request.ClientOrderId)
        };
        if (request.Price is { } price)
        {
            parameters.Add(new("price", FormatDecimal(price)));
            parameters.Add(new("timeInForce", request.TimeInForce!));
        }

        try
        {
            return await SendSignedAsync(HttpMethod.Post, "v3/order", parameters, request.Symbol, request.ClientOrderId, cancellationToken).ConfigureAwait(false);
        }
        catch (BinanceApiException exception) when (IsDefinitiveSubmissionRejection(exception))
        {
            MarkActivityError(activity, exception);
            throw new BinanceOrderRejectedException(request.ClientOrderId, exception);
        }
        catch (BinanceApiException exception) when ((int?)exception.StatusCode >= 500)
        {
            MarkActivityError(activity, exception);
            throw new BinanceOrderSubmissionAmbiguousException(request.ClientOrderId, exception);
        }
        catch (OperationCanceledException exception)
        {
            MarkActivityError(activity, exception);
            throw new BinanceOrderSubmissionAmbiguousException(request.ClientOrderId, exception);
        }
        catch (HttpRequestException exception)
        {
            MarkActivityError(activity, exception);
            throw new BinanceOrderSubmissionAmbiguousException(request.ClientOrderId, exception);
        }
        catch (IOException exception)
        {
            MarkActivityError(activity, exception);
            throw new BinanceOrderSubmissionAmbiguousException(request.ClientOrderId, exception);
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        var normalizedClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(clientOrderId);
        using var activity = StartFinancialActivity("query");
        try
        {
            return await SendSignedAsync(
                HttpMethod.Get,
                "v3/order",
                [new("symbol", normalizedSymbol), new("origClientOrderId", normalizedClientOrderId)],
                normalizedSymbol,
                normalizedClientOrderId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotOrderResult> CancelAsync(
        string symbol,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        EnsureExecutionEnabled();
        var normalizedSymbol = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        var normalizedClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(clientOrderId);
        using var activity = StartFinancialActivity("cancel");
        try
        {
            return await SendSignedAsync(
                HttpMethod.Delete,
                "v3/order",
                [new("symbol", normalizedSymbol), new("origClientOrderId", normalizedClientOrderId)],
                normalizedSymbol,
                normalizedClientOrderId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotAmendKeepPriorityResult> AmendKeepPriorityAsync(
        BinanceSpotAmendKeepPriorityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureExecutionEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartFinancialActivity("amend_keep_priority");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", request.Symbol),
            new("origClientOrderId", request.OrigClientOrderId),
            new("newQty", FormatDecimal(request.NewQuantity))
        };
        if (request.NewClientOrderId is not null)
        {
            parameters.Add(new("newClientOrderId", request.NewClientOrderId));
        }

        try
        {
            await SynchronizeClockAsync(cancellationToken).ConfigureAwait(false);
            var wire = await SendSignedRawAsync<BinanceAmendKeepPriorityWire>(
                HttpMethod.Put, "v3/order/amend/keepPriority", parameters, cancellationToken).ConfigureAwait(false);
            var expectedId = request.NewClientOrderId ?? request.OrigClientOrderId;
            return new BinanceSpotAmendKeepPriorityResult(
                wire.TransactTime,
                wire.ExecutionId,
                ToResult(wire.AmendedOrder, request.Symbol, expectedId));
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotCancelReplaceResult> CancelReplaceAsync(
        BinanceSpotCancelReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureExecutionEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartFinancialActivity("cancel_replace");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", request.Symbol),
            new("side", request.Side.ToString().ToUpperInvariant()),
            new("type", request.Type.ToString().ToUpperInvariant()),
            new("cancelReplaceMode", request.CancelReplaceMode),
            new("cancelOrigClientOrderId", request.CancelOrigClientOrderId),
            new("newClientOrderId", request.NewClientOrderId),
            new("quantity", FormatDecimal(request.Quantity))
        };
        if (request.Price is { } price)
        {
            parameters.Add(new("price", FormatDecimal(price)));
            parameters.Add(new("timeInForce", request.TimeInForce!));
        }

        if (request.CancelRestrictions is not null)
        {
            parameters.Add(new("cancelRestrictions", request.CancelRestrictions));
        }

        try
        {
            await SynchronizeClockAsync(cancellationToken).ConfigureAwait(false);
            var wire = await SendSignedRawAsync<BinanceCancelReplaceWire>(
                HttpMethod.Post, "v3/order/cancelReplace", parameters, cancellationToken).ConfigureAwait(false);
            return new BinanceSpotCancelReplaceResult(
                RequireText(wire.CancelResult, nameof(wire.CancelResult)),
                RequireText(wire.NewOrderResult, nameof(wire.NewOrderResult)),
                ToResult(wire.CancelResponse, request.Symbol, request.CancelOrigClientOrderId),
                ToResult(wire.NewOrderResponse, request.Symbol, request.NewClientOrderId));
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotOrderListResult> SubmitOcoOrderListAsync(
        BinanceSpotOcoOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureExecutionEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartFinancialActivity("submit_oco");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", request.Symbol),
            new("listClientOrderId", request.ListClientOrderId),
            new("side", request.Side.ToString().ToUpperInvariant()),
            new("quantity", FormatDecimal(request.Quantity)),
            new("aboveType", "LIMIT_MAKER"),
            new("aboveClientOrderId", request.AboveClientOrderId),
            new("abovePrice", FormatDecimal(request.AbovePrice)),
            new("belowType", "STOP_LOSS_LIMIT"),
            new("belowClientOrderId", request.BelowClientOrderId),
            new("belowStopPrice", FormatDecimal(request.BelowStopPrice)),
            new("belowPrice", FormatDecimal(request.BelowLimitPrice)),
            new("belowTimeInForce", request.BelowTimeInForce)
        };

        try
        {
            await SynchronizeClockAsync(cancellationToken).ConfigureAwait(false);
            var wire = await SendSignedRawAsync<BinanceOrderListWire>(
                HttpMethod.Post, "v3/orderList/oco", parameters, cancellationToken).ConfigureAwait(false);
            return ToOrderListResult(wire, request.Symbol, request.ListClientOrderId);
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    public async Task<BinanceSpotOrderListResult> SubmitOtocoOrderListAsync(
        BinanceSpotOtocoOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureExecutionEnabled();
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = StartFinancialActivity("submit_otoco");

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("symbol", request.Symbol),
            new("listClientOrderId", request.ListClientOrderId),
            new("workingType", "LIMIT"),
            new("workingSide", request.WorkingSide.ToString().ToUpperInvariant()),
            new("workingClientOrderId", request.WorkingClientOrderId),
            new("workingPrice", FormatDecimal(request.WorkingPrice)),
            new("workingQuantity", FormatDecimal(request.WorkingQuantity)),
            new("workingTimeInForce", request.WorkingTimeInForce),
            new("pendingSide", request.PendingSide.ToString().ToUpperInvariant()),
            new("pendingQuantity", FormatDecimal(request.PendingQuantity)),
            new("pendingAboveType", "LIMIT_MAKER"),
            new("pendingAboveClientOrderId", request.PendingAboveClientOrderId),
            new("pendingAbovePrice", FormatDecimal(request.PendingAbovePrice)),
            new("pendingBelowType", "STOP_LOSS_LIMIT"),
            new("pendingBelowClientOrderId", request.PendingBelowClientOrderId),
            new("pendingBelowStopPrice", FormatDecimal(request.PendingBelowStopPrice)),
            new("pendingBelowPrice", FormatDecimal(request.PendingBelowLimitPrice)),
            new("pendingBelowTimeInForce", request.PendingBelowTimeInForce)
        };

        try
        {
            await SynchronizeClockAsync(cancellationToken).ConfigureAwait(false);
            var wire = await SendSignedRawAsync<BinanceOrderListWire>(
                HttpMethod.Post, "v3/orderList/otoco", parameters, cancellationToken).ConfigureAwait(false);
            return ToOrderListResult(wire, request.Symbol, request.ListClientOrderId);
        }
        catch (Exception exception)
        {
            MarkActivityError(activity, exception);
            throw;
        }
    }

    private static Activity? StartFinancialActivity(string operation)
    {
        var activity = TradingTelemetry.ActivitySource.StartActivity("cryptotrading.binance.order", ActivityKind.Client);
        activity?.SetTag("crypto.exchange", "binance");
        activity?.SetTag("crypto.operation", operation);
        return activity;
    }

    private static void MarkActivityError(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", exception.GetType().Name);
    }

    private async Task<BinanceSpotOrderResult> SendSignedAsync(
        HttpMethod method,
        string endpoint,
        List<KeyValuePair<string, string>> parameters,
        string expectedSymbol,
        string expectedClientOrderId,
        CancellationToken cancellationToken)
    {
        await SynchronizeClockAsync(cancellationToken).ConfigureAwait(false);
        return await SendSignedCoreAsync(method, endpoint, parameters, expectedSymbol, expectedClientOrderId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BinanceSpotOrderResult> SendSignedCoreAsync(
        HttpMethod method,
        string endpoint,
        List<KeyValuePair<string, string>> parameters,
        string expectedSymbol,
        string expectedClientOrderId,
        CancellationToken cancellationToken)
    {
        var queryParameters = new List<KeyValuePair<string, string>>(parameters.Count + 2);
        queryParameters.AddRange(parameters);
        queryParameters.Add(new("recvWindow", options.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)));
        queryParameters.Add(new("timestamp", clock.EstimateServerNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        var query = BuildQuery(queryParameters);
        var signature = BinanceSpotAccountClient.Sign(query, options.ApiSecret);
        using var request = new HttpRequestMessage(
            method,
            new Uri(restBaseAddress, $"{endpoint}?{query}&signature={signature}"));
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", options.ApiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new BinanceApiException(response.StatusCode, endpoint);
        }

        var wire = await response.Content.ReadFromJsonAsync<BinanceOrderWire>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Binance returned an empty response for '{endpoint}'.");
        return ToResult(wire, expectedSymbol, expectedClientOrderId);
    }

    private async Task<TWire> SendSignedRawAsync<TWire>(
        HttpMethod method,
        string endpoint,
        List<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        var queryParameters = new List<KeyValuePair<string, string>>(parameters.Count + 2);
        queryParameters.AddRange(parameters);
        queryParameters.Add(new("recvWindow", options.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)));
        queryParameters.Add(new("timestamp", clock.EstimateServerNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        var query = BuildQuery(queryParameters);
        var signature = BinanceSpotAccountClient.Sign(query, options.ApiSecret);
        using var request = new HttpRequestMessage(
            method,
            new Uri(restBaseAddress, $"{endpoint}?{query}&signature={signature}"));
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", options.ApiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new BinanceApiException(response.StatusCode, endpoint);
        }

        return await response.Content.ReadFromJsonAsync<TWire>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Binance returned an empty response for '{endpoint}'.");
    }

    private static BinanceSpotOrderListResult ToOrderListResult(
        BinanceOrderListWire wire,
        string expectedSymbol,
        string expectedListClientOrderId)
    {
        var symbol = BinanceSpotOrderRequest.NormalizeSymbol(wire.Symbol);
        var listClientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(wire.ListClientOrderId);
        if (!string.Equals(symbol, expectedSymbol, StringComparison.Ordinal) ||
            !string.Equals(listClientOrderId, expectedListClientOrderId, StringComparison.Ordinal))
        {
            throw new JsonException("Binance orderList response identity did not match the requested symbol/listClientOrderId.");
        }

        var reports = new List<BinanceSpotOrderResult>(wire.OrderReports?.Count ?? 0);
        if (wire.OrderReports is not null)
        {
            foreach (var report in wire.OrderReports)
            {
                var reportClientId = BinanceSpotOrderRequest.NormalizeClientOrderId(report.ClientOrderId);
                reports.Add(ToResult(report, expectedSymbol, reportClientId));
            }
        }

        return new BinanceSpotOrderListResult(
            wire.OrderListId,
            RequireText(wire.ContingencyType, nameof(wire.ContingencyType)),
            RequireText(wire.ListStatusType, nameof(wire.ListStatusType)),
            RequireText(wire.ListOrderStatus, nameof(wire.ListOrderStatus)),
            listClientOrderId,
            wire.TransactionTime,
            symbol,
            reports);
    }

    private async Task SynchronizeClockAsync(CancellationToken cancellationToken)
    {
        if (options.ClockSyncCacheDuration > TimeSpan.Zero &&
            clock.HasFreshObservation(options.ClockSyncCacheDuration, options.ClockDriftTolerance))
        {
            return;
        }

        var sentAt = timeProvider.GetUtcNow();
        using var response = await httpClient.GetAsync(new Uri(restBaseAddress, "v3/time"), cancellationToken).ConfigureAwait(false);
        var receivedAt = timeProvider.GetUtcNow();
        if (!response.IsSuccessStatusCode)
        {
            throw new BinanceApiException(response.StatusCode, "v3/time");
        }

        var serverTime = await response.Content.ReadFromJsonAsync<BinanceServerTime>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Binance returned an empty server-time response.");
        clock.Observe(serverTime.ServerTime, sentAt, receivedAt);
        if (!clock.IsWithinTolerance(options.ClockDriftTolerance))
        {
            throw new BinanceClockDriftException(clock.Offset, options.ClockDriftTolerance);
        }
    }

    private void EnsureExecutionEnabled()
    {
        if (!options.EnableOrderExecution)
        {
            throw new InvalidOperationException("Binance Spot order execution is disabled; set EnableOrderExecution=true only in an explicitly approved Testnet configuration.");
        }
    }

    private static bool IsDefinitiveSubmissionRejection(BinanceApiException exception) =>
        exception.StatusCode is { } status && (int)status is >= 400 and < 500 && status != System.Net.HttpStatusCode.TooManyRequests;

    private static BinanceSpotOrderResult ToResult(BinanceOrderWire wire, string expectedSymbol, string expectedClientOrderId)
    {
        var symbol = BinanceSpotOrderRequest.NormalizeSymbol(wire.Symbol);
        var clientOrderId = BinanceSpotOrderRequest.NormalizeClientOrderId(wire.ClientOrderId);
        var origClientOrderId = string.IsNullOrWhiteSpace(wire.OrigClientOrderId)
            ? null
            : BinanceSpotOrderRequest.NormalizeClientOrderId(wire.OrigClientOrderId);
        var matchedClientOrderId = string.Equals(clientOrderId, expectedClientOrderId, StringComparison.Ordinal)
            ? clientOrderId
            : string.Equals(origClientOrderId, expectedClientOrderId, StringComparison.Ordinal)
                ? origClientOrderId!
                : null;
        if (!string.Equals(symbol, expectedSymbol, StringComparison.Ordinal) || matchedClientOrderId is null)
        {
            throw new JsonException("Binance order response identity did not match the requested symbol/clientOrderId.");
        }

        return new(
            wire.OrderId,
            symbol,
            matchedClientOrderId,
            RequireText(wire.Status, nameof(wire.Status)),
            ParseNonNegative(wire.Price, nameof(wire.Price)),
            ParsePositive(wire.OriginalQuantity, nameof(wire.OriginalQuantity)),
            ParseNonNegative(wire.ExecutedQuantity, nameof(wire.ExecutedQuantity)),
            wire.TransactTime != 0 ? wire.TransactTime : (wire.UpdateTime ?? 0));
    }

    private static decimal ParsePositive(string value, string fieldName)
    {
        var parsed = ParseNonNegative(value, fieldName);
        return parsed > 0m ? parsed : throw new JsonException($"Binance order response field '{fieldName}' must be positive.");
    }

    private static decimal ParseNonNegative(string value, string fieldName) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0m
            ? parsed
            : throw new JsonException($"Binance order response field '{fieldName}' was not a non-negative decimal.");

    private static string RequireText(string value, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length <= 64 && !value.Any(char.IsControl)
            ? value
            : throw new JsonException($"Binance order response field '{fieldName}' is invalid.");
    }

    private static string FormatDecimal(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static string BuildQuery(IReadOnlyList<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

    private sealed record BinanceOrderWire(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("orderId")] long OrderId,
        [property: JsonPropertyName("clientOrderId")] string ClientOrderId,
        [property: JsonPropertyName("price")] string Price,
        [property: JsonPropertyName("origQty")] string OriginalQuantity,
        [property: JsonPropertyName("executedQty")] string ExecutedQuantity,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("transactTime")] long TransactTime,
        [property: JsonPropertyName("origClientOrderId")] string? OrigClientOrderId = null,
        [property: JsonPropertyName("updateTime")] long? UpdateTime = null);

    private sealed record BinanceAmendKeepPriorityWire(
        [property: JsonPropertyName("transactTime")] long TransactTime,
        [property: JsonPropertyName("executionId")] long ExecutionId,
        [property: JsonPropertyName("amendedOrder")] BinanceOrderWire AmendedOrder);

    private sealed record BinanceCancelReplaceWire(
        [property: JsonPropertyName("cancelResult")] string CancelResult,
        [property: JsonPropertyName("newOrderResult")] string NewOrderResult,
        [property: JsonPropertyName("cancelResponse")] BinanceOrderWire CancelResponse,
        [property: JsonPropertyName("newOrderResponse")] BinanceOrderWire NewOrderResponse);

    private sealed record BinanceOrderListWire(
        [property: JsonPropertyName("orderListId")] long OrderListId,
        [property: JsonPropertyName("contingencyType")] string ContingencyType,
        [property: JsonPropertyName("listStatusType")] string ListStatusType,
        [property: JsonPropertyName("listOrderStatus")] string ListOrderStatus,
        [property: JsonPropertyName("listClientOrderId")] string ListClientOrderId,
        [property: JsonPropertyName("transactionTime")] long TransactionTime,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("orderReports")] IReadOnlyList<BinanceOrderWire>? OrderReports = null);
}

/// <summary>
/// Deterministic, zero-side-effect builder for Binance Spot WebSocket API (<c>ws-api/v3</c>)
/// order placement, priority-preserving quantity amendment (<c>order.amend.keepPriority</c>),
/// atomic <c>order.cancelReplace</c>, and protective <c>orderList.place.oco</c> / <c>orderList.place.otoco</c>.
/// Signs parameters in strict ASCII key order per Binance WebSocket API specification.
/// </summary>
public static class BinanceSpotWebSocketOrderPayloadBuilder
{
    public static string BuildOrderPlacePayload(
        Guid requestId,
        BinanceSpotOrderRequest request,
        string apiKey,
        string apiSecret,
        long timestampMilliseconds,
        long recvWindowMilliseconds = 5_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = RequireNonBlank(apiKey, nameof(apiKey)),
            ["newClientOrderId"] = request.ClientOrderId,
            ["quantity"] = FormatDecimal(request.Quantity),
            ["recvWindow"] = recvWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["side"] = request.Side.ToString().ToUpperInvariant(),
            ["symbol"] = request.Symbol,
            ["timestamp"] = timestampMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["type"] = request.Type.ToString().ToUpperInvariant()
        };
        if (request.Price is { } price)
        {
            parameters["price"] = FormatDecimal(price);
            parameters["timeInForce"] = request.TimeInForce!;
        }

        return SerializeSignedWebSocketRequest(requestId, "order.place", parameters, apiSecret);
    }

    public static string BuildAmendKeepPriorityPayload(
        Guid requestId,
        BinanceSpotAmendKeepPriorityRequest request,
        string apiKey,
        string apiSecret,
        long timestampMilliseconds,
        long recvWindowMilliseconds = 5_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = RequireNonBlank(apiKey, nameof(apiKey)),
            ["newQty"] = FormatDecimal(request.NewQuantity),
            ["origClientOrderId"] = request.OrigClientOrderId,
            ["recvWindow"] = recvWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["symbol"] = request.Symbol,
            ["timestamp"] = timestampMilliseconds.ToString(CultureInfo.InvariantCulture)
        };
        if (request.NewClientOrderId is not null)
        {
            parameters["newClientOrderId"] = request.NewClientOrderId;
        }

        return SerializeSignedWebSocketRequest(requestId, "order.amend.keepPriority", parameters, apiSecret);
    }

    public static string BuildCancelReplacePayload(
        Guid requestId,
        BinanceSpotCancelReplaceRequest request,
        string apiKey,
        string apiSecret,
        long timestampMilliseconds,
        long recvWindowMilliseconds = 5_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = RequireNonBlank(apiKey, nameof(apiKey)),
            ["cancelOrigClientOrderId"] = request.CancelOrigClientOrderId,
            ["cancelReplaceMode"] = request.CancelReplaceMode,
            ["newClientOrderId"] = request.NewClientOrderId,
            ["quantity"] = FormatDecimal(request.Quantity),
            ["recvWindow"] = recvWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["side"] = request.Side.ToString().ToUpperInvariant(),
            ["symbol"] = request.Symbol,
            ["timestamp"] = timestampMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["type"] = request.Type.ToString().ToUpperInvariant()
        };
        if (request.Price is { } price)
        {
            parameters["price"] = FormatDecimal(price);
            parameters["timeInForce"] = request.TimeInForce!;
        }

        if (request.CancelRestrictions is not null)
        {
            parameters["cancelRestrictions"] = request.CancelRestrictions;
        }

        return SerializeSignedWebSocketRequest(requestId, "order.cancelReplace", parameters, apiSecret);
    }

    public static string BuildOcoOrderListPayload(
        Guid requestId,
        BinanceSpotOcoOrderRequest request,
        string apiKey,
        string apiSecret,
        long timestampMilliseconds,
        long recvWindowMilliseconds = 5_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["aboveClientOrderId"] = request.AboveClientOrderId,
            ["abovePrice"] = FormatDecimal(request.AbovePrice),
            ["aboveType"] = "LIMIT_MAKER",
            ["apiKey"] = RequireNonBlank(apiKey, nameof(apiKey)),
            ["belowClientOrderId"] = request.BelowClientOrderId,
            ["belowPrice"] = FormatDecimal(request.BelowLimitPrice),
            ["belowStopPrice"] = FormatDecimal(request.BelowStopPrice),
            ["belowTimeInForce"] = request.BelowTimeInForce,
            ["belowType"] = "STOP_LOSS_LIMIT",
            ["listClientOrderId"] = request.ListClientOrderId,
            ["quantity"] = FormatDecimal(request.Quantity),
            ["recvWindow"] = recvWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["side"] = request.Side.ToString().ToUpperInvariant(),
            ["symbol"] = request.Symbol,
            ["timestamp"] = timestampMilliseconds.ToString(CultureInfo.InvariantCulture)
        };

        return SerializeSignedWebSocketRequest(requestId, "orderList.place.oco", parameters, apiSecret);
    }

    private static string SerializeSignedWebSocketRequest(
        Guid requestId,
        string method,
        SortedDictionary<string, string> sortedParameters,
        string apiSecret)
    {
        var secret = RequireNonBlank(apiSecret, nameof(apiSecret));
        var canonicalQuery = string.Join('&', sortedParameters.Select(static kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        var signature = BinanceSpotAccountClient.Sign(canonicalQuery, secret);
        sortedParameters["signature"] = signature;

        return JsonSerializer.Serialize(new
        {
            id = requestId.ToString("D"),
            method,
            @params = sortedParameters
        });
    }

    private static string RequireNonBlank(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string FormatDecimal(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
