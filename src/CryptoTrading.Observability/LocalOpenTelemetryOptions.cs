using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CryptoTrading.Observability;

/// <summary>
/// Explicit, local-first OTLP configuration. An empty endpoint keeps the SDK
/// available for the in-process instruments but never creates an exporter.
/// </summary>
public sealed class LocalOpenTelemetryOptions
{
    public string Endpoint { get; init; } = string.Empty;

    public string AllowedHosts { get; init; } = string.Empty;

    public string Protocol { get; init; } = "grpc";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    public Uri? ValidatedEndpoint { get; private set; }

    public OtlpExportProtocol ValidatedProtocol { get; private set; } = OtlpExportProtocol.Grpc;

    public void Validate()
    {
        ValidatedEndpoint = null;
        ValidatedProtocol = ParseProtocol(Protocol);
        if (!IsEnabled)
        {
            return;
        }

        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint is null ||
            endpoint.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("OTLP endpoint must be an absolute HTTP(S) URI without user info, query, or fragment.");
        }

        var expectedPort = ValidatedProtocol == OtlpExportProtocol.Grpc ? 4317 : 4318;
        if (endpoint.Port != -1 && endpoint.Port != expectedPort)
        {
            throw new InvalidOperationException($"OTLP {Protocol} endpoint must use port {expectedPort} when a port is specified.");
        }

        var host = endpoint.Host.TrimEnd('.').ToUpperInvariant();
        var isLoopback = string.Equals(host, "LOCALHOST", StringComparison.Ordinal) ||
                         (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        var allowedHosts = ParseAllowedHosts(AllowedHosts);
        if (!isLoopback && !allowedHosts.Contains(host))
        {
            throw new InvalidOperationException("Non-loopback OTLP endpoints require an explicit OTEL_EXPORTER_OTLP_ALLOWED_HOSTS entry.");
        }

        ValidatedEndpoint = endpoint;
    }

    public static LocalOpenTelemetryOptions FromEnvironment() => new()
    {
        Endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? string.Empty,
        AllowedHosts = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ALLOWED_HOSTS") ?? string.Empty,
        Protocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "grpc"
    };

    public static IServiceCollection AddLocalOpenTelemetry(
        IServiceCollection services,
        LocalOpenTelemetryOptions options,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        options.Validate();

        _ = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TradingTelemetry.MeterName);
                if (options.ValidatedEndpoint is not null)
                {
                    metrics.AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(TradingTelemetry.ActivitySourceName);
                if (options.ValidatedEndpoint is not null)
                {
                    tracing.AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
                }
            });

        return services;
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, LocalOpenTelemetryOptions options)
    {
        exporter.Endpoint = options.ValidatedEndpoint ?? throw new InvalidOperationException("OTLP endpoint was not validated.");
        exporter.Protocol = options.ValidatedProtocol;
    }

    private static OtlpExportProtocol ParseProtocol(string? protocol) =>
        protocol?.Trim().ToUpperInvariant() switch
        {
            "GRPC" or "" or null => OtlpExportProtocol.Grpc,
            "HTTP/PROTOBUF" => OtlpExportProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException("OTEL_EXPORTER_OTLP_PROTOCOL must be 'grpc' or 'http/protobuf'.")
        };

    private static HashSet<string> ParseAllowedHosts(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static host => host.TrimEnd('.').ToUpperInvariant())
            .Where(static host => host.Length > 0 && !host.Contains('/', StringComparison.Ordinal) && !host.Contains(':', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
}
