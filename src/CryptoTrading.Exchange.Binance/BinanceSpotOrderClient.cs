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

/// <summary>Testnet-only credentials and safety switch for order mutation.</summary>
public sealed class BinanceSpotOrderClientOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;
    public Uri? RestBaseAddress { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public long RecvWindowMilliseconds { get; set; } = 5_000;

    public TimeSpan ClockDriftTolerance { get; set; } = TimeSpan.FromSeconds(5);

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

    private async Task SynchronizeClockAsync(CancellationToken cancellationToken)
    {
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
}
