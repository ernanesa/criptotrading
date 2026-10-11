#pragma warning disable CA1031 // CLI diagnostic tool catches top-level exceptions and returns safe non-zero exit codes.
#pragma warning disable CA1303 // Operator-facing CLI messages and templates are intentional.
#pragma warning disable CA2007 // CLI tool runs in standard console context.
#pragma warning disable CA1849 // Console synchronous writes are acceptable in simple CLI driver.

using System.Globalization;
using CryptoTrading.Configuration;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddLocalDotEnv()
    .AddEnvironmentVariables()
    .Build();

var connectionString = configuration["POSTGRES_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Error: POSTGRES_CONNECTION_STRING is required.");
    return 1;
}

await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));

var now = DateTimeOffset.UtcNow;
Console.WriteLine($"[1/2] Connected to PostgreSQL at {now:u}. Checking RiskPolicy...");

var existingPolicy = await store.GetActiveRiskPolicyAsync(now);
if (existingPolicy is null)
{
    Console.Error.WriteLine("Error: an active RiskPolicy is required before Testnet balance reconciliation. This tool will not create, approve, or activate a policy.");
    return 1;
}

Console.WriteLine($"Active RiskPolicy: {existingPolicy.Policy.Version} (hash: {existingPolicy.DocumentHash})");

if (!(bool.TryParse(configuration["TESTNET_STATE_INITIALIZER_RECONCILE_BALANCES"], out var reconcileBalances) && reconcileBalances))
{
    Console.WriteLine("Testnet balance reconciliation is disabled. Set TESTNET_STATE_INITIALIZER_RECONCILE_BALANCES=true to explicitly enable it.");
    return 0;
}

if (!string.Equals(configuration["TRADING_ENVIRONMENT"], "TESTNET", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Error: TRADING_ENVIRONMENT must be TESTNET for balance reconciliation.");
    return 1;
}

Console.WriteLine("[2/2] Checking Ledger Balances for account 'exchange-wallet'...");
var existingAssets = await store.GetAssetsAsync(new AccountId("exchange-wallet"));
if (existingAssets.Count > 0)
{
    Console.WriteLine($"Ledger already has {existingAssets.Count} assets recorded in 'exchange-wallet': {string.Join(", ", existingAssets)}");
}
else
{
    Console.WriteLine("Ledger is empty for 'exchange-wallet'. Fetching live balances from Binance Spot Testnet...");
    var restBaseUrl = configuration["BINANCE_REST_BASE_URL"] ?? "https://testnet.binance.vision/api";
    var apiKey = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE");
    var apiSecret = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE");

    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
    {
        Console.Error.WriteLine("Error: Binance Testnet credentials are missing.");
        return 1;
    }

    var options = new BinanceAuthenticatedClientOptions
    {
        Environment = BinanceEnvironment.Testnet,
        RestBaseAddress = new Uri(restBaseUrl.EndsWith('/', StringComparison.Ordinal) ? restBaseUrl : restBaseUrl + "/"),
        ApiKey = apiKey,
        ApiSecret = apiSecret,
        // Accept the read-only account capability field; this client exposes only GET endpoints.
        AllowTestnetWithdrawCapability = true
    };

    using var httpClient = new HttpClient
    {
        BaseAddress = options.RestBaseAddress,
        Timeout = options.RequestTimeout
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/testnet-initializer");
    var client = new BinanceSpotAccountClient(httpClient, options);
    var account = await client.GetAccountAsync();

    var postings = new List<LedgerPosting>();
    foreach (var b in account.Balances)
    {
        if (decimal.TryParse(b.Free, NumberStyles.Number, CultureInfo.InvariantCulture, out var free) &&
            decimal.TryParse(b.Locked, NumberStyles.Number, CultureInfo.InvariantCulture, out var locked))
        {
            var total = free + locked;
            if (total > 0m && TryCreateAsset(b.Asset, out var asset))
            {
                postings.Add(new LedgerPosting(new AccountId("exchange-wallet"), new Money(total, asset)));
                postings.Add(new LedgerPosting(new AccountId("equity-opening"), new Money(-total, asset)));
            }
        }
    }

    if (postings.Count >= 2)
    {
        var entry = new LedgerEntry(
            LedgerEntryId.New(),
            CorrelationId.New(),
            now,
            "testnet-initial-opening-balance",
            postings,
            new IdempotencyKey("opening-balance-testnet-init"));

        await store.AppendJournalEntryAsync(entry);
        Console.WriteLine($"Successfully recorded balanced initial opening entry with {postings.Count / 2} assets.");
    }
    else
    {
        Console.WriteLine("No positive balances found on Binance Testnet to seed.");
    }
}

Console.WriteLine("Verification:");
var checkPolicy = await store.GetActiveRiskPolicyAsync(DateTimeOffset.UtcNow);
Console.WriteLine($"Active RiskPolicy: {checkPolicy?.Policy.Version ?? "NONE"} (Valid: {checkPolicy?.IsValidAt(DateTimeOffset.UtcNow) ?? false})");

var walletAssets = await store.GetAssetsAsync(new AccountId("exchange-wallet"));
Console.WriteLine($"Exchange wallet assets count: {walletAssets.Count}");
foreach (var assetName in walletAssets)
{
    var balance = await store.GetBalanceAsync(new AccountId("exchange-wallet"), new Asset(assetName));
    if (balance.Amount > 0)
    {
        Console.WriteLine($"  {assetName}: {balance.Amount}");
    }
}

Console.WriteLine("State initialization complete!");
return 0;

static bool TryCreateAsset(string value, out Asset asset)
{
    asset = default;
    if (string.IsNullOrWhiteSpace(value)) return false;
    var normalized = value.Trim().ToUpperInvariant();
    if (normalized.Length is < 2 or > 20 || !normalized.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        return false;
    asset = new Asset(normalized);
    return true;
}
