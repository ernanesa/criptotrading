#pragma warning disable CA1031 // The probe intentionally redacts all exception details and returns a safe exit code.
#pragma warning disable CA1303 // This is a small operator-facing diagnostic tool; messages are intentionally stable.
#pragma warning disable CA2007 // The top-level probe has no synchronization context and uses explicit ConfigureAwait below.

using CryptoTrading.Configuration;
using CryptoTrading.Exchange.Binance;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddLocalDotEnv()
    .AddEnvironmentVariables()
    .Build();

if (!string.Equals(configuration["TRADING_ENVIRONMENT"], "TESTNET", StringComparison.OrdinalIgnoreCase))
{
    return Fail("TRADING_ENVIRONMENT must be TESTNET.");
}

var restBaseUrl = configuration["BINANCE_REST_BASE_URL"];
if (!Uri.TryCreate(restBaseUrl, UriKind.Absolute, out var restUri) ||
    restUri.Scheme != Uri.UriSchemeHttps ||
    restUri.Port != 443 ||
    !string.IsNullOrEmpty(restUri.UserInfo) ||
    !string.Equals(restUri.Host, "testnet.binance.vision", StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(restUri.AbsolutePath.Trim('/'), "api", StringComparison.OrdinalIgnoreCase) ||
    !string.IsNullOrEmpty(restUri.Query) ||
    !string.IsNullOrEmpty(restUri.Fragment))
{
    return Fail("BINANCE_REST_BASE_URL must be the HTTPS Spot Testnet endpoint.");
}

var apiKey = configuration["BINANCE_API_KEY"];
var apiSecret = configuration["BINANCE_API_SECRET"];
if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
{
    return Fail("BINANCE_API_KEY and BINANCE_API_SECRET must be supplied only through the local environment.");
}

if (bool.TryParse(configuration["BINANCE_WITHDRAWALS_ENABLED"], out var withdrawalsEnabled) && withdrawalsEnabled)
{
    return Fail("BINANCE_WITHDRAWALS_ENABLED must remain false.");
}

var configuredSymbol = configuration["BINANCE_PROBE_SYMBOL"] ?? "BTCUSDT";
if (!TryNormalizeSymbol(configuredSymbol, out var symbol))
{
    return Fail("BINANCE_PROBE_SYMBOL must contain only 2-20 ASCII letters or digits.");
}
var options = new BinanceAuthenticatedClientOptions
{
    Environment = BinanceEnvironment.Testnet,
    RestBaseAddress = new Uri(restUri.AbsoluteUri.EndsWith('/', StringComparison.Ordinal) ? restUri.AbsoluteUri : restUri.AbsoluteUri + "/"),
    ApiKey = apiKey,
    ApiSecret = apiSecret
};

try
{
    using var httpClient = new HttpClient
    {
        BaseAddress = options.RestBaseAddress,
        Timeout = options.RequestTimeout
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/readonly-probe");
    var client = new BinanceSpotAccountClient(httpClient, options);
    var account = await client.GetAccountAsync().ConfigureAwait(false);
    var openOrders = await client.GetOpenOrdersAsync(symbol).ConfigureAwait(false);
    var trades = await client.GetMyTradesAsync(symbol, new BinanceTradeQuery(Limit: 100)).ConfigureAwait(false);

    Console.WriteLine("Binance Spot Testnet authenticated read-only probe: OK");
    Console.WriteLine($"Balances={account.Balances.Count}; OpenOrders={openOrders.Count}; Trades={trades.Count}; Symbol={symbol}");
    Console.WriteLine("No order, cancel, transfer, withdrawal, margin, or futures endpoint was called.");
    return 0;
}
catch (OperationCanceledException)
{
    return Fail("The read-only probe timed out or was cancelled.");
}
catch (Exception exception)
{
    // Never print exception messages from an authenticated exchange call: they
    // may contain request details. The type is sufficient for troubleshooting.
    return Fail($"Read-only probe failed with {exception.GetType().Name}.");
}

static int Fail(string message)
{
    Console.Error.WriteLine($"Read-only probe refused: {message}");
    return 2;
}

static bool TryNormalizeSymbol(string value, out string normalized)
{
    normalized = value.Trim().ToUpperInvariant();
    return normalized.Length is >= 2 and <= 20 && normalized.All(char.IsAsciiLetterOrDigit);
}
