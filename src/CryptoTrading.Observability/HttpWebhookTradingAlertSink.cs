using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Observability;

/// <summary>
/// Explicit opt-in policy for an HTTPS alert webhook. Hosts are allowlisted so
/// an accidental configuration cannot redirect operational data to an arbitrary URL.
/// </summary>
public sealed record TradingAlertWebhookOptions
{
    public bool Enabled { get; init; }

    public Uri? Endpoint { get; init; }

    public IReadOnlySet<string> AllowedHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Webhook timeout must be greater than zero and at most two minutes.");
        }

        if (!Enabled)
        {
            return;
        }

        if (Endpoint is not { IsAbsoluteUri: true } endpoint ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Port != 443 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("Enabled alert webhooks require an HTTPS endpoint on port 443 without credentials or query tokens.");
        }

        if (AllowedHosts.Count == 0 || !AllowedHosts.Any(host => string.Equals(host, endpoint.Host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The enabled webhook host must be present in the explicit allowlist.");
        }

        foreach (var host in AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host) ||
                host.Contains('/', StringComparison.Ordinal) ||
                host.Contains(':', StringComparison.Ordinal) ||
                host.Contains('*', StringComparison.Ordinal) ||
                Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new ArgumentException("Webhook allowlist entries must be concrete host names.", nameof(AllowedHosts));
            }
        }
    }
}

/// <summary>
/// Sends only the redacted operational alert envelope. It never adds API keys,
/// authorization headers, or query-string tokens and is disabled by default.
/// </summary>
public sealed class HttpWebhookTradingAlertSink : ITradingAlertSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly TradingAlertWebhookOptions options;

    public HttpWebhookTradingAlertSink(HttpClient httpClient, TradingAlertWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.httpClient = httpClient;
        this.options = options;
    }

    public async ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Validate();
        if (!options.Enabled)
        {
            throw new InvalidOperationException("External alert webhook delivery is disabled.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(alert, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Alert webhook returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }
    }
}
