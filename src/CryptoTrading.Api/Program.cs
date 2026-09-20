using CryptoTrading.Configuration;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
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
builder.Services.AddSingleton<IOperationalControl>(_ =>
    new InMemoryOperationalControl(new DenyApiOperationalControlResetAuthorizer()));
builder.Services.AddOptions<TradingOptions>().ValidateOnStart();
builder.Services.AddHealthChecks().AddCheck<TradingReadinessCheck>("trading-configuration", tags: ["ready"]);
var postgresConnectionString = builder.Configuration["POSTGRES_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    _ = builder.Services.AddPostgresTradingStore(postgresConnectionString);
}

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

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .WithName("GetLiveness");

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready", StringComparer.OrdinalIgnoreCase)
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
