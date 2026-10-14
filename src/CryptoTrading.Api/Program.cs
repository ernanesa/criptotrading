#pragma warning disable CA1031 // Diagnostic endpoints catch general exceptions to return 502/503 responses.

using CryptoTrading.Configuration;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// Emit sanitized error type and stack in our handler; framework exception messages may contain upstream payloads.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
builder.Configuration.AddLocalDotEnv();
LocalOpenTelemetryOptions.AddLocalOpenTelemetry(
    builder.Services,
    new LocalOpenTelemetryOptions
    {
        Endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? string.Empty,
        AllowedHosts = builder.Configuration["OTEL_EXPORTER_OTLP_ALLOWED_HOSTS"] ?? string.Empty,
        Protocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] ?? "grpc"
    },
    serviceName: $"{TradingTelemetry.ServiceName}.Api");
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddAspNetCoreInstrumentation());
builder.Services.AddTradingConfiguration(builder.Configuration);
builder.Services.AddBinanceSpotMarketClient();
var resolvedApiKey = TradingConfigurationExtensions.ResolveCredential(builder.Configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE");
var resolvedApiSecret = TradingConfigurationExtensions.ResolveCredential(builder.Configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE");
if (!string.IsNullOrWhiteSpace(resolvedApiKey) && !string.IsNullOrWhiteSpace(resolvedApiSecret))
{
    builder.Services.AddBinanceSpotAccountClientFromConfiguration(builder.Configuration);
}
builder.Services.AddSingleton<IOperationalControl>(_ =>
    new InMemoryOperationalControl(new DenyApiOperationalControlResetAuthorizer()));
builder.Services.AddHealthChecks()
    .AddCheck<TradingReadinessCheck>("trading-configuration", tags: ["ready"])
    .AddCheck<PersistenceConfigurationReadinessCheck>("persistence-configuration", tags: ["ready"]);
var postgresConnectionString = PostgresConnectionConfiguration.Resolve(builder.Configuration);
if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    _ = builder.Services.AddPostgresTradingStore(postgresConnectionString);
}

var internalAccessTokenFileConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["API_INTERNAL_ACCESS_TOKEN_FILE"]);
var internalAccessToken = TradingConfigurationExtensions.ResolveCredential(
    builder.Configuration,
    "__API_INTERNAL_ACCESS_TOKEN_DIRECT_VALUE_IS_NOT_SUPPORTED__",
    "API_INTERNAL_ACCESS_TOKEN_FILE");
if (internalAccessTokenFileConfigured && string.IsNullOrWhiteSpace(internalAccessToken))
{
    throw new InvalidOperationException("The internal API access-token file could not be loaded safely.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var suppliedCorrelationId) &&
                        suppliedCorrelationId.Count == 1 &&
                        suppliedCorrelationId[0] is { Length: > 0 and <= 128 } supplied
        ? supplied
        : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object?>
    {
        ["correlation_id"] = correlationId,
        ["service"] = TradingTelemetry.ServiceName,
        ["environment"] = app.Environment.EnvironmentName
    });

    try
    {
        await next().ConfigureAwait(false);
    }
    finally
    {
        stopwatch.Stop();
        TradingTelemetry.RecordApiRequest(context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
        ApiLog.RequestCompleted(
            app.Logger,
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
    }
});

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
{
    var correlationId = context.Response.Headers["X-Correlation-Id"].ToString();
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var errorType = error?.GetType().Name ?? "UNKNOWN";
    using var errorScope = app.Logger.BeginScope(new Dictionary<string, object?> { ["error_stack"] = error?.StackTrace });
    ApiLog.UnhandledRequestFailure(app.Logger, context.Request.Method, context.Request.Path, correlationId, errorType);
    return ApiProblem.WriteAsync(
        context,
        StatusCodes.Status500InternalServerError,
        "INTERNAL_ERROR",
        "The request could not be completed.");
}));

if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return JsonSerializer.SerializeAsync(context.Response.Body, new { status = report.Status.ToString() });
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthResponse
})
.WithName("GetLiveness");

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready", StringComparer.OrdinalIgnoreCase),
    ResponseWriter = WriteHealthResponse
})
.WithName("GetReadiness");

var localDiagnostics = app.MapGroup(string.Empty)
    .AddEndpointFilter(async (context, next) =>
    {
        var isLoopback = LocalDiagnosticsAccessPolicy.IsLoopback(context.HttpContext.Connection.RemoteIpAddress);
        var suppliedToken = context.HttpContext.Request.Headers["X-Internal-Access-Token"];
        var hasValidInternalToken = !string.IsNullOrWhiteSpace(internalAccessToken) &&
                                    suppliedToken.Count == 1 &&
                                    FixedTimeEquals(internalAccessToken, suppliedToken[0]);
        if (!isLoopback && !hasValidInternalToken)
        {
            return ApiProblem.Forbidden();
        }

        return await next(context).ConfigureAwait(false);
    });

static bool FixedTimeEquals(string expected, string? supplied)
{
    if (supplied is null)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected),
        Encoding.UTF8.GetBytes(supplied));
}

static bool TryParseExchangeDecimal(string? value, out decimal parsed) =>
    decimal.TryParse(
        value,
        System.Globalization.NumberStyles.Number,
        System.Globalization.CultureInfo.InvariantCulture,
        out parsed);

static decimal ParseExchangeDecimal(string value) =>
    TryParseExchangeDecimal(value, out var parsed)
        ? parsed
        : throw new FormatException("The exchange response contained an invalid numeric value.");

localDiagnostics.MapGet("/metrics", () => Results.Ok(TradingTelemetry.Snapshot()))
    .WithName("GetMetrics");

localDiagnostics.MapGet("/operational/status", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    OperationalControlAuditEvent? latest;
    try
    {
        var currentStateStore = services.GetService<IOperationalControlCurrentStateStore>();
        if (currentStateStore is null)
        {
            return ApiProblem.ServiceUnavailable("OPERATIONAL_STATE_UNAVAILABLE", "The durable Worker operational state is unavailable.");
        }

        latest = await currentStateStore.ReadLatestOperationalControlAuditEventAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        ApiLog.UnhandledRequestFailure(app.Logger, "GET", "/operational/status",
            Activity.Current?.TraceId.ToString() ?? string.Empty, exception.GetType().Name);
        return ApiProblem.ServiceUnavailable("OPERATIONAL_STATE_UNAVAILABLE", "The durable Worker operational state is unavailable.");
    }
    if (latest is null)
    {
        return ApiProblem.ServiceUnavailable("OPERATIONAL_STATE_UNAVAILABLE", "No durable Worker operational state has been recorded.");
    }

    var snapshot = new OperationalControlSnapshot(
        latest.To,
        0,
        latest.OccurredAtUtc,
        latest.Actor,
        latest.Reason);

    return Results.Ok(new
    {
        source = "worker-durable-audit",
        mode = snapshot.Mode.ToString(),
        blocksNewEntries = snapshot.BlocksNewEntries,
        revision = snapshot.Revision,
        changedAtUtc = snapshot.ChangedAtUtc,
        changedBy = snapshot.ChangedBy,
        reason = snapshot.Reason,
        mutatingEndpointsEnabled = false
    });
})
    .WithName("GetOperationalStatus");

localDiagnostics.MapGet("/operational/decisions/{decisionId}", async (
    string decisionId,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var commitStore = services.GetService<IAutonomyDecisionCommitStore>();
    if (commitStore is null)
    {
        return ApiProblem.ServiceUnavailable("DECISION_STORE_UNAVAILABLE", "The durable decision store is unavailable.");
    }

    if (string.IsNullOrWhiteSpace(decisionId))
    {
        return ApiProblem.BadRequest("INVALID_DECISION_ID", "A decision identifier is required.");
    }

    var feeStore = services.GetService<IExpectedExecutionFeeStore>();
    var submissionStore = services.GetService<IExecutionSubmissionContextStore>();
    var lifecycleStore = services.GetService<IOrderLifecycleStore>();

    var report = await AutonomyDecisionReconstructor.ReconstructAsync(
        decisionId.Trim(),
        commitStore,
        feeStore,
        submissionStore,
        lifecycleStore,
        cancellationToken).ConfigureAwait(false);

    return report is not null
        ? Results.Ok(report)
        : ApiProblem.NotFound("DECISION_NOT_FOUND", "The requested decision was not found.");
})
.WithName("GetOperationalDecision");

localDiagnostics.MapGet("/operational/checkpoints/{scope}", async (
    string scope,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var commitStore = services.GetService<IAutonomyDecisionCommitStore>();
    if (commitStore is null)
    {
        return ApiProblem.ServiceUnavailable("CHECKPOINT_STORE_UNAVAILABLE", "The durable checkpoint store is unavailable.");
    }

    if (string.IsNullOrWhiteSpace(scope))
    {
        return ApiProblem.BadRequest("INVALID_SCOPE", "A checkpoint scope is required.");
    }

    var lastProcessedClose = await commitStore.GetLastProcessedCloseAsync(scope.Trim(), cancellationToken).ConfigureAwait(false);
    return Results.Ok(new
    {
        scope = scope.Trim(),
        lastProcessedCloseUtc = lastProcessedClose
    });
})
.WithName("GetAutonomyCheckpoint");

localDiagnostics.MapGet("/operational/risk-policy", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var policyStore = services.GetService<IRiskPolicyStore>();
    if (policyStore is null)
    {
        return ApiProblem.ServiceUnavailable("RISK_POLICY_STORE_UNAVAILABLE", "The durable risk-policy store is unavailable.");
    }

    var policy = await policyStore.GetActiveRiskPolicyAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    return policy is not null
        ? Results.Ok(new
        {
            policy = policy.Policy,
            effectiveFromUtc = policy.EffectiveFromUtc,
            expiresAtUtc = policy.ExpiresAtUtc,
            createdBy = policy.CreatedBy,
            documentHash = policy.DocumentHash,
            lifecycle = policy.Lifecycle.ToString(),
            approvedBy = policy.ApprovedBy,
            approvalId = policy.ApprovalId,
            approvedAtUtc = policy.ApprovedAtUtc
        })
        : ApiProblem.NotFound("RISK_POLICY_NOT_FOUND", "No active risk policy was found.");
})
.WithName("GetActiveRiskPolicy");

localDiagnostics.MapGet("/market/ticker/{symbol}", async (
    string symbol,
    IBinanceSpotMarketClient marketClient,
    CancellationToken cancellationToken) =>
{
    var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.Trim().ToUpperInvariant();
    try
    {
        var ticker = await marketClient.GetBookTickerAsync(normalizedSymbol, cancellationToken).ConfigureAwait(false);
        var markPrice = Math.Round((ticker.BidPrice + ticker.AskPrice) / 2m, 2);
        var spread = ticker.AskPrice - ticker.BidPrice;
        var spreadBps = markPrice > 0 ? Math.Round((spread / markPrice) * 10000m, 2) : 0m;

        return Results.Ok(new
        {
            symbol = ticker.Symbol,
            bidPrice = ticker.BidPrice,
            bidQuantity = ticker.BidQuantity,
            askPrice = ticker.AskPrice,
            askQuantity = ticker.AskQuantity,
            markPrice = markPrice,
            spread = spread,
            spreadBps = spreadBps,
            observedAtUtc = ticker.ObservedAtUtc
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return ApiProblem.BadGateway("MARKET_DATA_UNAVAILABLE", "Market data could not be retrieved.");
    }
})
.WithName("GetMarketTicker");

localDiagnostics.MapGet("/portfolio/balances", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var accountClient = services.GetService<IBinanceSpotAccountClient>();
    var marketClient = services.GetService<IBinanceSpotMarketClient>();

    if (accountClient is null)
    {
        return ApiProblem.ServiceUnavailable("ACCOUNT_DATA_UNAVAILABLE", "Account data is unavailable because credentials are not configured.");
    }

    try
    {
        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        var hasBtcBalance = account.Balances.Any(balance =>
            string.Equals(balance.Asset, "BTC", StringComparison.OrdinalIgnoreCase) &&
            TryParseExchangeDecimal(balance.Free, out var free) &&
            TryParseExchangeDecimal(balance.Locked, out var locked) &&
            free + locked > 0m);
        decimal btcPrice = 0m;
        if (hasBtcBalance)
        {
            if (marketClient is null)
            {
                return ApiProblem.ServiceUnavailable("MARKET_DATA_UNAVAILABLE", "BTC valuation is unavailable because market data is not configured.");
            }

            try
            {
                var btcTicker = await marketClient.GetBookTickerAsync("BTCUSDT", cancellationToken).ConfigureAwait(false);
                btcPrice = (btcTicker.BidPrice + btcTicker.AskPrice) / 2m;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return ApiProblem.BadGateway("MARKET_DATA_UNAVAILABLE", "BTC valuation could not be retrieved.");
            }
        }

        if (account.Balances.Any(balance =>
                !TryParseExchangeDecimal(balance.Free, out _) ||
                !TryParseExchangeDecimal(balance.Locked, out _)))
        {
            return ApiProblem.BadGateway("ACCOUNT_DATA_INVALID", "Account balances contained an invalid numeric value.");
        }

        var parsedBalances = account.Balances
            .Select(b =>
            {
                _ = TryParseExchangeDecimal(b.Free, out var free);
                _ = TryParseExchangeDecimal(b.Locked, out var locked);
                var total = free + locked;
                decimal valueUsd = 0m;
                if (string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase))
                {
                    valueUsd = total;
                }
                else if (string.Equals(b.Asset, "BTC", StringComparison.OrdinalIgnoreCase))
                {
                    valueUsd = total * btcPrice;
                }

                return new
                {
                    asset = b.Asset,
                    free = free,
                    locked = locked,
                    total = total,
                    valueUsd = Math.Round(valueUsd, 2)
                };
            })
            .Where(b => b.total > 0m)
            .OrderByDescending(b => b.valueUsd)
            .ToList();

        var totalNavUsd = parsedBalances.Sum(b => b.valueUsd);
        var cashUsdt = parsedBalances.FirstOrDefault(b => b.asset.Equals("USDT", StringComparison.OrdinalIgnoreCase))?.total ?? 0m;
        var positionsUsd = totalNavUsd - cashUsdt;

        return Results.Ok(new
        {
            environment = "TESTNET",
            canTrade = account.CanTrade,
            totalNavUsd = Math.Round(totalNavUsd, 2),
            cashUsdt = Math.Round(cashUsdt, 2),
            positionsUsd = Math.Round(positionsUsd, 2),
            balances = parsedBalances,
            observedAtUtc = DateTimeOffset.UtcNow
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return ApiProblem.BadGateway("ACCOUNT_DATA_UNAVAILABLE", "Account balances could not be retrieved.");
    }
})
.WithName("GetPortfolioBalances");

localDiagnostics.MapGet("/trading/open-orders", async (
    IServiceProvider services,
    string? symbol,
    CancellationToken cancellationToken) =>
{
    var accountClient = services.GetService<IBinanceSpotAccountClient>();
    if (accountClient is null)
    {
        return ApiProblem.ServiceUnavailable("OPEN_ORDERS_UNAVAILABLE", "Open orders are unavailable because credentials are not configured.");
    }

    try
    {
        var openOrders = await accountClient.GetOpenOrdersAsync(symbol?.Trim().ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
        if (openOrders.Any(order =>
                !TryParseExchangeDecimal(order.Price, out _) ||
                !TryParseExchangeDecimal(order.OriginalQuantity, out _) ||
                !TryParseExchangeDecimal(order.ExecutedQuantity, out _)))
        {
            return ApiProblem.BadGateway("OPEN_ORDERS_INVALID", "Open orders contained an invalid numeric value.");
        }

        return Results.Ok(openOrders.Select(o => new
        {
            symbol = o.Symbol,
            orderId = o.OrderId,
            clientOrderId = o.ClientOrderId,
            price = ParseExchangeDecimal(o.Price),
            origQty = ParseExchangeDecimal(o.OriginalQuantity),
            executedQty = ParseExchangeDecimal(o.ExecutedQuantity),
            status = o.Status,
            type = o.Type,
            side = o.Side,
            timeUtc = DateTimeOffset.FromUnixTimeMilliseconds(o.Time)
        }));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return ApiProblem.BadGateway("OPEN_ORDERS_UNAVAILABLE", "Open orders could not be retrieved.");
    }
})
.WithName("GetOpenOrders");

localDiagnostics.MapGet("/trading/trades", async (
    IServiceProvider services,
    string? symbol,
    CancellationToken cancellationToken) =>
{
    var accountClient = services.GetService<IBinanceSpotAccountClient>();
    if (accountClient is null)
    {
        return ApiProblem.ServiceUnavailable("TRADE_HISTORY_UNAVAILABLE", "Trade history is unavailable because credentials are not configured.");
    }

    try
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.Trim().ToUpperInvariant();
        var trades = await accountClient.GetMyTradesAsync(normalizedSymbol, null, cancellationToken).ConfigureAwait(false);
        if (trades.Any(trade =>
                !TryParseExchangeDecimal(trade.Price, out _) ||
                !TryParseExchangeDecimal(trade.Quantity, out _) ||
                !TryParseExchangeDecimal(trade.QuoteQuantity, out _) ||
                !TryParseExchangeDecimal(trade.Commission, out _)))
        {
            return ApiProblem.BadGateway("TRADE_HISTORY_INVALID", "Trade history contained an invalid numeric value.");
        }

        return Results.Ok(trades.Select(t => new
        {
            symbol = t.Symbol,
            id = t.Id,
            orderId = t.OrderId,
            price = ParseExchangeDecimal(t.Price),
            qty = ParseExchangeDecimal(t.Quantity),
            quoteQty = ParseExchangeDecimal(t.QuoteQuantity),
            commission = ParseExchangeDecimal(t.Commission),
            commissionAsset = t.CommissionAsset,
            timeUtc = DateTimeOffset.FromUnixTimeMilliseconds(t.Time),
            isBuyer = t.IsBuyer,
            isMaker = t.IsMaker
        }));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return ApiProblem.BadGateway("TRADE_HISTORY_UNAVAILABLE", "Trade history could not be retrieved.");
    }
})
.WithName("GetMyTrades");

localDiagnostics.MapGet("/portfolio/performance", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var snapshotStore = services.GetService<IPortfolioNavSnapshotStore>();
    if (snapshotStore is null)
    {
        return ApiProblem.ServiceUnavailable("PORTFOLIO_HISTORY_UNAVAILABLE", "Durable portfolio snapshots are unavailable.");
    }

    var asOfUtc = DateTimeOffset.UtcNow;
    AggregatedPortfolioPerformanceMetrics metrics;
    try
    {
        metrics = await snapshotStore.GetPerformanceMetricsAsync(asOfUtc, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        return ApiProblem.ServiceUnavailable("PORTFOLIO_HISTORY_UNAVAILABLE", $"Durable portfolio snapshots could not be read: {ex.Message}");
    }

    var responseTimeUtc = DateTimeOffset.UtcNow;
    if (metrics.CurrentSnapshotObservedAtUtc is not { } observedAtUtc ||
        observedAtUtc > responseTimeUtc ||
        responseTimeUtc - observedAtUtc > TimeSpan.FromMinutes(2))
    {
        return ApiProblem.ServiceUnavailable("PORTFOLIO_SNAPSHOT_STALE", "The current portfolio snapshot is missing or older than two minutes.");
    }

    if (metrics.CurrentNetAssetValueQuote is not { } totalNavUsd ||
        metrics.CurrentUnrealizedPnlQuote is not { } totalUnrealizedPnlUsd)
    {
        return ApiProblem.ServiceUnavailable("PORTFOLIO_HISTORY_INSUFFICIENT", "Portfolio history is insufficient for an auditable P&L calculation.");
    }

    var totalRealizedPnlUsd = metrics.TotalRealizedPnlQuote;
    var totalProfitUsd = totalRealizedPnlUsd + totalUnrealizedPnlUsd;
    var totalTradesCount = metrics.TotalTradesCount;
    var winRatePercentage = totalTradesCount > 0
        ? Math.Round((decimal)metrics.TotalWinningCount / totalTradesCount * 100m, 2)
        : 0m;
    var profitFactor = metrics.TotalGrossLossQuote > 0m
        ? Math.Round(metrics.TotalGrossProfitQuote / metrics.TotalGrossLossQuote, 2)
        : (decimal?)null;

    return Results.Ok(new
    {
        totalNavUsd,
        initialCapitalUsd = (decimal?)null,
        initialCapitalAvailabilityReason = "UNKNOWN_UNTIL_BASELINE_AND_EXTERNAL_CASH_FLOWS_ARE_DURABLE",
        cashBalanceUsd = (decimal?)null,
        positionsValueUsd = (decimal?)null,
        totalProfitUsd,
        totalProfitPct = (decimal?)null,
        totalProfitPctAvailabilityReason = "UNKNOWN_UNTIL_BASELINE_AND_EXTERNAL_CASH_FLOWS_ARE_DURABLE",
        totalRealizedPnlUsd,
        totalUnrealizedPnlUsd,
        realisticNetProfitUsd = metrics.RealisticNetPnlQuote,
        averageReturnBps = metrics.AverageReturnBps,
        averageWinBps = metrics.AverageWinBps,
        averageLossBps = metrics.AverageLossBps,
        totalFeesPaidUsd = metrics.TotalFeesPaidQuote,
        totalTradesCount,
        winRatePercentage,
        profitFactor,
        profitFactorAvailabilityReason = profitFactor is null ? "UNKNOWN_WITHOUT_REALIZED_LOSSES" : null,
        scopeSymbol = metrics.ScopeSymbol,
        quoteAsset = metrics.QuoteAsset,
        asOfUtc = metrics.AsOfUtc,
        feesAreComplete = metrics.FeesAreComplete,
        unvaluedFeeAmounts = metrics.UnvaluedFeeAmounts,
        periods = metrics.Periods.Select(period => new
        {
            periodName = period.PeriodName,
            profitUsd = period.NetAssetValueDeltaQuote,
            profitPercentage = period.NetAssetValueDeltaQuote is { } periodProfit && totalNavUsd > 0m
                ? Math.Round(periodProfit / totalNavUsd * 100m, 2)
                : (decimal?)null,
            realizedProfitUsd = period.RealizedPnlQuote,
            realisticProfitUsd = period.RealisticNetProfitQuote,
            averageReturnBps = period.AverageReturnBps,
            averageWinBps = period.AverageWinBps,
            averageLossBps = period.AverageLossBps,
            unrealizedProfitUsd = period.UnrealizedPnlDeltaQuote,
            feesPaidUsd = period.FeesPaidQuote,
            tradesCount = period.TradesCount,
            winRatePercentage = period.TradesCount > 0
                ? Math.Round((decimal)period.WinningCount / period.TradesCount * 100m, 2)
                : 0m,
            snapshotAvailability = period.SnapshotAvailability.ToString()
        })
    });
})
.WithName("GetPortfolioPerformance");

localDiagnostics.MapGet("/operational/regimes/current", async (
    IServiceProvider services,
    [FromQuery] string symbol = "BTCUSDT",
    CancellationToken cancellationToken = default) =>
{
    var regimeStore = services.GetService<IMarketRegimeStore>();
    if (regimeStore is null)
    {
        return ApiProblem.ServiceUnavailable("REGIME_STORE_UNAVAILABLE", "The durable market-regime store is unavailable.");
    }

    var latest = await regimeStore.GetLatestMarketRegimeAsync(new CryptoTrading.Domain.Primitives.Symbol(symbol), cancellationToken).ConfigureAwait(false);
    if (latest is null)
    {
        return Results.Ok(new
        {
            symbol,
            regime = "Unknown",
            confidence = 0m,
            trendSlopeRate = 0m,
            volatilityRate = 0m,
            reason = "NO_REGIME_RECORDED_YET",
            observedAtUtc = DateTimeOffset.UtcNow,
            favorsTrendFollowing = false,
            favorsMeanReversion = false,
            prohibitsNewExposure = false
        });
    }

    return Results.Ok(new
    {
        symbol = latest.Symbol.Value,
        regime = latest.Regime.ToString(),
        confidence = latest.Confidence,
        trendSlopeRate = latest.TrendSlopeRate,
        volatilityRate = latest.VolatilityRate,
        reason = latest.Reason,
        observedAtUtc = latest.ObservedAtUtc,
        favorsTrendFollowing = latest.FavorsTrendFollowing,
        favorsMeanReversion = latest.FavorsMeanReversion,
        prohibitsNewExposure = latest.ProhibitsNewExposure
    });
})
.WithName("GetCurrentMarketRegime");

localDiagnostics.MapGet("/operational/regimes/history", async (
    IServiceProvider services,
    [FromQuery] string symbol = "BTCUSDT",
    [FromQuery] int limit = 20,
    CancellationToken cancellationToken = default) =>
{
    var regimeStore = services.GetService<IMarketRegimeStore>();
    if (regimeStore is null)
    {
        return ApiProblem.ServiceUnavailable("REGIME_STORE_UNAVAILABLE", "The durable market-regime store is unavailable.");
    }

    var history = await regimeStore.GetMarketRegimeHistoryAsync(new CryptoTrading.Domain.Primitives.Symbol(symbol), Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
    return Results.Ok(history.Select(h => new
    {
        symbol = h.Symbol.Value,
        regime = h.Regime.ToString(),
        confidence = h.Confidence,
        trendSlopeRate = h.TrendSlopeRate,
        volatilityRate = h.VolatilityRate,
        reason = h.Reason,
        observedAtUtc = h.ObservedAtUtc
    }));
})
.WithName("GetMarketRegimeHistory");

localDiagnostics.MapGet("/operational/models", async (
    IServiceProvider services,
    [FromQuery] string strategyId = "trend-following",
    CancellationToken cancellationToken = default) =>
{
    var modelStore = services.GetService<IModelStrategyRegistryStore>();
    if (modelStore is null)
    {
        return ApiProblem.ServiceUnavailable("MODEL_REGISTRY_UNAVAILABLE", "The durable model registry is unavailable.");
    }

    var models = await modelStore.ListModelsAsync(strategyId, cancellationToken).ConfigureAwait(false);
    return Results.Ok(models.Select(m => new
    {
        modelId = m.ModelId,
        strategyId = m.StrategyId,
        version = m.Version,
        codeCommitSha = m.CodeCommitSha,
        datasetHash = m.DatasetHash,
        status = m.Status.ToString(),
        registeredAtUtc = m.RegisteredAtUtc,
        registeredBy = m.RegisteredBy,
        promotionJustification = m.PromotionJustification,
        canonicalHash = m.CanonicalHash
    }));
})
.WithName("GetRegisteredModels");

localDiagnostics.MapGet("/operational/drift", async (
    IServiceProvider services,
    [FromQuery] string? strategyId = null,
    [FromQuery] int limit = 30,
    CancellationToken cancellationToken = default) =>
{
    var driftStore = services.GetService<IDriftObservationStore>();
    if (driftStore is null)
    {
        return ApiProblem.ServiceUnavailable("DRIFT_STORE_UNAVAILABLE", "The durable drift store is unavailable.");
    }

    var drifts = await driftStore.GetRecentDriftsAsync(strategyId, Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
    return Results.Ok(drifts.Select(d => new
    {
        observationId = d.ObservationId,
        strategyId = d.StrategyId,
        type = d.Type.ToString(),
        expectedValue = d.ExpectedValue,
        observedValue = d.ObservedValue,
        allowedDeviationThreshold = d.AllowedDeviationThreshold,
        absoluteDeviation = d.AbsoluteDeviation,
        relativeRate = d.RelativeRate,
        severity = d.Severity.ToString(),
        requiresQuarantine = d.RequiresQuarantine,
        message = d.Message,
        observedAtUtc = d.ObservedAtUtc
    }));
})
.WithName("GetRecentDrifts");

localDiagnostics.MapGet("/operational/allocations", async (
    IServiceProvider services,
    CancellationToken cancellationToken = default) =>
{
    var performanceStore = services.GetService<IStrategyRegimePerformanceStore>();
    if (performanceStore is null)
    {
        return ApiProblem.ServiceUnavailable("ALLOCATION_STORE_UNAVAILABLE", "The durable allocation store is unavailable.");
    }

    var performances = await performanceStore.GetPerformancesAsync(cancellationToken).ConfigureAwait(false);
    return Results.Ok(performances.Select(p => new
    {
        strategyId = p.StrategyId,
        regime = p.Regime.ToString(),
        observationCount = p.ObservationCount,
        cumulativeReward = p.CumulativeReward,
        averageReward = p.AverageReward
    }));
})
.WithName("GetStrategyAllocations");

var deliveredAlerts = new System.Collections.Concurrent.ConcurrentQueue<object>();
localDiagnostics.MapPost("/operational/alerts/webhook", async (
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    using var document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
    var receivedAtUtc = DateTimeOffset.UtcNow;
    var summary = new
    {
        receivedAtUtc,
        owner = "Ernane",
        payload = document.RootElement.Clone()
    };
    deliveredAlerts.Enqueue(summary);
    while (deliveredAlerts.Count > 100 && deliveredAlerts.TryDequeue(out _))
    {
    }

    return Results.Ok(new
    {
        status = "accepted",
        receivedAtUtc,
        owner = "Ernane"
    });
})
.WithName("ReceiveOperationalAlertWebhook");

localDiagnostics.MapGet("/operational/alerts/delivered", () =>
    Results.Ok(new
    {
        count = deliveredAlerts.Count,
        alerts = deliveredAlerts.ToArray()
    }))
.WithName("GetDeliveredOperationalAlerts");

app.Run();

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The health check is created through dependency injection.")]
internal sealed class TradingReadinessCheck(IOptions<TradingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var trading = options.Value;
        var hasApiKey = !string.IsNullOrWhiteSpace(trading.Binance.ApiKey);
        var hasApiSecret = !string.IsNullOrWhiteSpace(trading.Binance.ApiSecret);
        var valid = string.Equals(trading.Environment, "TESTNET", StringComparison.OrdinalIgnoreCase) &&
                    trading.Binance.RestBaseUrl is { IsAbsoluteUri: true } &&
                    trading.Binance.WebSocketBaseUrl is { IsAbsoluteUri: true } &&
                    !trading.Binance.WithdrawalsEnabled &&
                    trading.Binance.ParsedAllowedSymbols.Count > 0 &&
                    hasApiKey == hasApiSecret;

        var result = valid
            ? HealthCheckResult.Healthy("Trading configuration is loaded for the local testnet runtime.")
            : HealthCheckResult.Unhealthy("Trading configuration is incomplete or unsafe for the local runtime.");

        return Task.FromResult(result);
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The health check is created through dependency injection.")]
internal sealed class PersistenceConfigurationReadinessCheck(
    IConfiguration configuration,
    IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string resolved;
        try
        {
            resolved = PostgresConnectionConfiguration.Resolve(configuration);
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL configuration is invalid: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            return HealthCheckResult.Unhealthy("PostgreSQL configuration is required for readiness.");
        }

        var currentStateStore = services.GetService<IOperationalControlCurrentStateStore>();
        if (currentStateStore is not null)
        {
            try
            {
                var latest = await currentStateStore.ReadLatestOperationalControlAuditEventAsync(cancellationToken).ConfigureAwait(false);
                if (latest is not null && latest.To is OperationalMode.SafeMode or OperationalMode.KillSwitch)
                {
                    return HealthCheckResult.Degraded(
                        $"Operational state is {latest.To} (NO_TRADE financial readiness vs process liveness). Reason: {latest.Reason}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Degraded(
                    $"Unable to verify operational financial readiness state: {ex.Message}");
            }
        }

        return HealthCheckResult.Healthy("A PostgreSQL connection string is configured and operational financial readiness checks passed.");
    }
}

internal static partial class ApiLog
{
    [LoggerMessage(LogLevel.Information, "HTTP request {Method} {Path} completed with status {StatusCode} in {DurationMilliseconds} ms")]
    public static partial void RequestCompleted(
        ILogger logger,
        string method,
        string path,
        int statusCode,
        double durationMilliseconds);

    [LoggerMessage(LogLevel.Error, "HTTP request {Method} {Path} failed with an unhandled error type {ErrorType}. CorrelationId: {CorrelationId}")]
    public static partial void UnhandledRequestFailure(
        ILogger logger,
        string method,
        string path,
        string correlationId,
        string errorType);
}

internal static class ApiProblem
{
    public static IResult BadRequest(string code, string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, type: $"urn:cryptotrading:error:{code}");

    public static IResult Forbidden() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "This endpoint requires a local connection or authenticated private proxy.",
            type: "urn:cryptotrading:error:PRIVATE_ACCESS_REQUIRED");

    public static IResult NotFound(string code, string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title, type: $"urn:cryptotrading:error:{code}");

    public static IResult BadGateway(string code, string title) =>
        Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: title, type: $"urn:cryptotrading:error:{code}");

    public static IResult ServiceUnavailable(string code, string title) =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: title, type: $"urn:cryptotrading:error:{code}");

    public static Task WriteAsync(HttpContext context, int statusCode, string code, string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"urn:cryptotrading:error:{code}"
        });
    }
}

internal sealed class DenyApiOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    public bool IsAuthorized(
        OperationalControlSnapshot current,
        OperationalControlCommand resetCommand,
        OperationalControlResetAuthorization authorization) => false;
}

internal partial class Program { }
