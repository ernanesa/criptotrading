#pragma warning disable CA1031 // Diagnostic endpoints catch general exceptions to return 502/503 responses.

using CryptoTrading.Configuration;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
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
builder.Services.AddOptions<TradingOptions>().ValidateOnStart();
builder.Services.AddHealthChecks().AddCheck<TradingReadinessCheck>("trading-configuration", tags: ["ready"]);
var postgresConnectionString = builder.Configuration["POSTGRES_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    _ = builder.Services.AddPostgresTradingStore(postgresConnectionString);
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
            context.Response.StatusCode,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
    }
});

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
        if (!LocalDiagnosticsAccessPolicy.IsLoopback(context.HttpContext.Connection.RemoteIpAddress))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context).ConfigureAwait(false);
    });

localDiagnostics.MapGet("/metrics", () => Results.Ok(TradingTelemetry.Snapshot()))
    .WithName("GetMetrics");

localDiagnostics.MapGet("/operational/status", (IOperationalControl operationalControl) =>
    Results.Ok(new
    {
        source = "api-process",
        mode = operationalControl.Snapshot.Mode.ToString(),
        blocksNewEntries = operationalControl.Snapshot.BlocksNewEntries,
        revision = operationalControl.Snapshot.Revision,
        changedAtUtc = operationalControl.Snapshot.ChangedAtUtc,
        changedBy = operationalControl.Snapshot.ChangedBy,
        reason = operationalControl.Snapshot.Reason,
        mutatingEndpointsEnabled = false
    }))
    .WithName("GetOperationalStatus");

localDiagnostics.MapGet("/operational/decisions/{decisionId}", async (
    string decisionId,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var commitStore = services.GetService<IAutonomyDecisionCommitStore>();
    if (commitStore is null)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(decisionId))
    {
        return Results.BadRequest(new { error = "decisionId is required." });
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
        : Results.NotFound(new { error = $"Decision '{decisionId}' was not found." });
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
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(scope))
    {
        return Results.BadRequest(new { error = "scope is required." });
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
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
        : Results.NotFound(new { error = "No active RiskPolicy found." });
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
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
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
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        decimal btcPrice = 0m;
        if (marketClient is not null)
        {
            try
            {
                var btcTicker = await marketClient.GetBookTickerAsync("BTCUSDT", cancellationToken).ConfigureAwait(false);
                btcPrice = (btcTicker.BidPrice + btcTicker.AskPrice) / 2m;
            }
            catch { }
        }

        var parsedBalances = account.Balances
            .Select(b =>
            {
                _ = decimal.TryParse(b.Free, System.Globalization.CultureInfo.InvariantCulture, out var free);
                _ = decimal.TryParse(b.Locked, System.Globalization.CultureInfo.InvariantCulture, out var locked);
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
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
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
        return Results.Ok(Array.Empty<object>());
    }

    try
    {
        var openOrders = await accountClient.GetOpenOrdersAsync(symbol?.Trim().ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
        return Results.Ok(openOrders.Select(o => new
        {
            symbol = o.Symbol,
            orderId = o.OrderId,
            clientOrderId = o.ClientOrderId,
            price = decimal.TryParse(o.Price, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0m,
            origQty = decimal.TryParse(o.OriginalQuantity, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0m,
            executedQty = decimal.TryParse(o.ExecutedQuantity, System.Globalization.CultureInfo.InvariantCulture, out var eq) ? eq : 0m,
            status = o.Status,
            type = o.Type,
            side = o.Side,
            timeUtc = DateTimeOffset.FromUnixTimeMilliseconds(o.Time)
        }));
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
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
        return Results.Ok(Array.Empty<object>());
    }

    try
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.Trim().ToUpperInvariant();
        var trades = await accountClient.GetMyTradesAsync(normalizedSymbol, null, cancellationToken).ConfigureAwait(false);
        return Results.Ok(trades.Select(t => new
        {
            symbol = t.Symbol,
            id = t.Id,
            orderId = t.OrderId,
            price = decimal.TryParse(t.Price, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0m,
            qty = decimal.TryParse(t.Quantity, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0m,
            quoteQty = decimal.TryParse(t.QuoteQuantity, System.Globalization.CultureInfo.InvariantCulture, out var qq) ? qq : 0m,
            commission = decimal.TryParse(t.Commission, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0m,
            commissionAsset = t.CommissionAsset,
            timeUtc = DateTimeOffset.FromUnixTimeMilliseconds(t.Time),
            isBuyer = t.IsBuyer,
            isMaker = t.IsMaker
        }));
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetMyTrades");

localDiagnostics.MapGet("/portfolio/performance", async (
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var accountClient = services.GetService<IBinanceSpotAccountClient>();
    var marketClient = services.GetService<IBinanceSpotMarketClient>();
    var performanceStore = services.GetService<IPortfolioPerformanceStore>();

    decimal totalNavUsd = 0m;
    decimal cashUsdt = 0m;
    decimal btcTotal = 0m;
    decimal btcPrice = 0m;

    if (accountClient is not null)
    {
        try
        {
            var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            if (marketClient is not null)
            {
                try
                {
                    var btcTicker = await marketClient.GetBookTickerAsync("BTCUSDT", cancellationToken).ConfigureAwait(false);
                    btcPrice = (btcTicker.BidPrice + btcTicker.AskPrice) / 2m;
                }
                catch { }
            }

            foreach (var b in account.Balances)
            {
                _ = decimal.TryParse(b.Free, System.Globalization.CultureInfo.InvariantCulture, out var free);
                _ = decimal.TryParse(b.Locked, System.Globalization.CultureInfo.InvariantCulture, out var locked);
                var total = free + locked;
                if (string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase))
                {
                    totalNavUsd += total;
                    cashUsdt += total;
                }
                else if (string.Equals(b.Asset, "BTC", StringComparison.OrdinalIgnoreCase))
                {
                    btcTotal += total;
                    totalNavUsd += total * btcPrice;
                }
            }
        }
        catch { }
    }

    AggregatedPortfolioPerformanceMetrics? metrics = null;
    if (performanceStore is not null)
    {
        try
        {
            metrics = await performanceStore.GetPerformanceMetricsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { }
    }

    var positionsValueUsd = Math.Round(btcTotal * btcPrice, 2);
    totalNavUsd = Math.Round(cashUsdt + positionsValueUsd, 2);

    var positionCost = metrics?.BtcPositionBasis?.CostQuote ?? 0m;
    var totalRealizedPnlUsd = metrics is not null ? Math.Round(metrics.TotalRealizedPnlQuote, 2) : 0m;
    var totalUnrealizedPnlUsd = metrics?.BtcPositionBasis is { Quantity: > 0m }
        ? Math.Round(positionsValueUsd - positionCost, 2)
        : 0m;

    var totalProfitUsd = totalRealizedPnlUsd + totalUnrealizedPnlUsd;
    var initialCapitalUsd = Math.Round(totalNavUsd - totalProfitUsd, 2);
    if (initialCapitalUsd <= 0m)
    {
        initialCapitalUsd = totalNavUsd;
    }

    var totalProfitPct = initialCapitalUsd > 0m
        ? Math.Round((totalProfitUsd / initialCapitalUsd) * 100m, 2)
        : 0m;

    var totalTradesCount = metrics?.TotalTradesCount ?? 0;
    var winRatePercentage = totalTradesCount > 0
        ? Math.Round(((decimal)metrics!.TotalWinningCount / totalTradesCount) * 100m, 2)
        : 0m;

    var profitFactor = 0m;
    if (metrics is not null)
    {
        if (metrics.TotalGrossLossQuote > 0m)
        {
            profitFactor = Math.Round(metrics.TotalGrossProfitQuote / metrics.TotalGrossLossQuote, 2);
        }
        else if (metrics.TotalGrossProfitQuote > 0m)
        {
            profitFactor = Math.Round(metrics.TotalGrossProfitQuote, 2);
        }
    }

    var totalFeesPaidUsd = metrics is not null ? Math.Round(metrics.TotalFeesPaidQuote, 2) : 0m;

    var periods = metrics?.Periods.Select(p =>
    {
        var realized = Math.Round(p.RealizedPnlQuote, 2);
        var unrealized = totalUnrealizedPnlUsd;
        var profit = realized + unrealized;
        var profitPct = initialCapitalUsd > 0m ? Math.Round((profit / initialCapitalUsd) * 100m, 2) : 0m;
        var winRate = p.TradesCount > 0 ? Math.Round(((decimal)p.WinningCount / p.TradesCount) * 100m, 2) : 0m;

        return new
        {
            periodName = p.PeriodName,
            profitUsd = profit,
            profitPercentage = profitPct,
            realizedProfitUsd = realized,
            unrealizedProfitUsd = unrealized,
            feesPaidUsd = Math.Round(p.FeesPaidQuote, 2),
            tradesCount = p.TradesCount,
            winRatePercentage = winRate
        };
    }).ToArray() ?? new[]
    {
        new { periodName = "Hoje (Dia / 24 Horas)", profitUsd = 0m, profitPercentage = 0m, realizedProfitUsd = 0m, unrealizedProfitUsd = 0m, feesPaidUsd = 0m, tradesCount = 0, winRatePercentage = 0m },
        new { periodName = "Semana (Últimos 7 Dias)", profitUsd = 0m, profitPercentage = 0m, realizedProfitUsd = 0m, unrealizedProfitUsd = 0m, feesPaidUsd = 0m, tradesCount = 0, winRatePercentage = 0m },
        new { periodName = "Mês (Últimos 30 Dias)", profitUsd = 0m, profitPercentage = 0m, realizedProfitUsd = 0m, unrealizedProfitUsd = 0m, feesPaidUsd = 0m, tradesCount = 0, winRatePercentage = 0m },
        new { periodName = "Ano (Acumulado / YTD)", profitUsd = 0m, profitPercentage = 0m, realizedProfitUsd = 0m, unrealizedProfitUsd = 0m, feesPaidUsd = 0m, tradesCount = 0, winRatePercentage = 0m }
    };

    return Results.Ok(new
    {
        totalNavUsd = totalNavUsd,
        initialCapitalUsd = initialCapitalUsd,
        cashBalanceUsd = Math.Round(cashUsdt, 2),
        positionsValueUsd = positionsValueUsd,
        totalProfitUsd = totalProfitUsd,
        totalProfitPct = totalProfitPct,
        totalRealizedPnlUsd = totalRealizedPnlUsd,
        totalUnrealizedPnlUsd = totalUnrealizedPnlUsd,
        totalFeesPaidUsd = totalFeesPaidUsd,
        totalTradesCount = totalTradesCount,
        winRatePercentage = winRatePercentage,
        profitFactor = profitFactor,
        periods = periods
    });
})
.WithName("GetPortfolioPerformance");

app.MapGet("/operational/regimes/current", async (
    IServiceProvider services,
    [FromQuery] string symbol = "BTCUSDT",
    CancellationToken cancellationToken = default) =>
{
    var regimeStore = services.GetService<IMarketRegimeStore>();
    if (regimeStore is null)
    {
        return Results.Ok(new
        {
            symbol,
            regime = "Unknown",
            confidence = 0m,
            trendSlopeRate = 0m,
            volatilityRate = 0m,
            reason = "REGIME_STORE_UNAVAILABLE",
            observedAtUtc = DateTimeOffset.UtcNow,
            favorsTrendFollowing = false,
            favorsMeanReversion = false,
            prohibitsNewExposure = false
        });
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

app.MapGet("/operational/regimes/history", async (
    IServiceProvider services,
    [FromQuery] string symbol = "BTCUSDT",
    [FromQuery] int limit = 20,
    CancellationToken cancellationToken = default) =>
{
    var regimeStore = services.GetService<IMarketRegimeStore>();
    if (regimeStore is null)
    {
        return Results.Ok(Array.Empty<object>());
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

app.MapGet("/operational/models", async (
    IServiceProvider services,
    [FromQuery] string strategyId = "trend-following",
    CancellationToken cancellationToken = default) =>
{
    var modelStore = services.GetService<IModelStrategyRegistryStore>();
    if (modelStore is null)
    {
        return Results.Ok(Array.Empty<object>());
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

app.MapGet("/operational/drift", async (
    IServiceProvider services,
    [FromQuery] string? strategyId = null,
    [FromQuery] int limit = 30,
    CancellationToken cancellationToken = default) =>
{
    var driftStore = services.GetService<IDriftObservationStore>();
    if (driftStore is null)
    {
        return Results.Ok(Array.Empty<object>());
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

app.MapGet("/operational/allocations", async (
    IServiceProvider services,
    CancellationToken cancellationToken = default) =>
{
    var performanceStore = services.GetService<IStrategyRegimePerformanceStore>();
    if (performanceStore is null)
    {
        return Results.Ok(Array.Empty<object>());
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

app.Run();

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The health check is created through dependency injection.")]
internal sealed class TradingReadinessCheck(IOptions<TradingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var trading = options.Value;
        var valid = string.Equals(trading.Environment, "TESTNET", StringComparison.OrdinalIgnoreCase) &&
                    trading.Binance.RestBaseUrl is { IsAbsoluteUri: true } &&
                    trading.Binance.WebSocketBaseUrl is { IsAbsoluteUri: true } &&
                    trading.Binance.ApiKey.Length > 0 &&
                    trading.Binance.ApiSecret.Length > 0 &&
                    !trading.Binance.WithdrawalsEnabled &&
                    trading.Binance.ParsedAllowedSymbols.Count > 0;

        var result = valid
            ? HealthCheckResult.Healthy("Trading configuration is loaded for the local testnet runtime.")
            : HealthCheckResult.Unhealthy("Trading configuration is incomplete or unsafe for the local runtime.");

        return Task.FromResult(result);
    }
}

internal static partial class ApiLog
{
    [LoggerMessage(LogLevel.Information, "HTTP request completed with status {StatusCode} in {DurationMilliseconds} ms")]
    public static partial void RequestCompleted(ILogger logger, int statusCode, double durationMilliseconds);
}

internal sealed class DenyApiOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    public bool IsAuthorized(
        OperationalControlSnapshot current,
        OperationalControlCommand resetCommand,
        OperationalControlResetAuthorization authorization) => false;
}

internal partial class Program { }

