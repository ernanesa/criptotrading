#pragma warning disable CA1031 // CLI diagnostic tool catches top-level exceptions and returns safe non-zero exit codes.
#pragma warning disable CA1303 // Operator-facing CLI messages and templates are intentional.
#pragma warning disable CA2007 // CLI tool runs in standard console context.
#pragma warning disable CA1849 // Console synchronous writes are acceptable in simple CLI driver.

using System.Globalization;
using CryptoTrading.Configuration;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
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
await store.ApplyMigrationsAsync();

var now = DateTimeOffset.UtcNow;
Console.WriteLine($"[1/3] Connected to PostgreSQL at {now:u}. Checking RiskPolicy...");

var existingPolicy = await store.GetActiveRiskPolicyAsync(now);
if (existingPolicy is not null)
{
    Console.WriteLine($"Active RiskPolicy already exists: {existingPolicy.Policy.Version} (hash: {existingPolicy.DocumentHash})");
}
else
{
    try
    {
        var activated = await store.ActivateRiskPolicyAsync("v1.0.0-autonomy", DateTimeOffset.UtcNow);
        Console.WriteLine($"Activated existing RiskPolicy: {activated.Policy.Version} at {DateTimeOffset.UtcNow:u}");
    }
    catch
    {
        var policy = new RiskPolicy(
            version: "v1.0.0-autonomy",
            maxOrderNotional: 100m,
            maxExposureNotional: 500m,
            maxDailyLoss: 50m,
            maxDrawdown: 100m,
            maxMarketDataAge: TimeSpan.FromSeconds(60),
            withdrawalsEnabled: false,
            maxSlippageBps: 50m,
            maxVolatilityBps: 200m,
            minLiquidityNotional: 1000m);

        var draft = RiskPolicyDocument.CreateDraft(
            policy,
            effectiveFromUtc: now.AddMinutes(-5),
            expiresAtUtc: now.AddDays(30),
            createdBy: "risk-officer-author");

        await store.SaveRiskPolicyAsync(draft);
        var approved = await store.ApproveRiskPolicyAsync(
            draft.Policy.Version,
            approvalId: "approval-testnet-init-001",
            approver: "trading-director-approver",
            approvedAtUtc: now);

        var activated = await store.ActivateRiskPolicyAsync(
            approved.Policy.Version,
            activatedAtUtc: now.AddSeconds(1));

        Console.WriteLine($"Created, approved and activated RiskPolicy: {activated.Policy.Version} (hash: {activated.DocumentHash})");
    }
}

Console.WriteLine("[2/3] Checking Ledger Balances for account 'exchange-wallet'...");
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

Console.WriteLine("[3/4] Ensuring Position Cost Basis and reconciling unrecorded trade executions...");
var btcBalance = await store.GetBalanceAsync(new AccountId("exchange-wallet"), new Asset("BTC"));
if (btcBalance.Amount > 0)
{
    // If position_cost_basis is empty, seed it with the initial balance
    await using var conn = new Npgsql.NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new Npgsql.NpgsqlCommand(
        """
        INSERT INTO trading.position_cost_basis (symbol, base_asset, quote_asset, quantity, cost_quote)
        VALUES ('BTCUSDT', 'BTC', 'USDT', @quantity, @cost)
        ON CONFLICT (symbol) DO NOTHING;
        """, conn);
    cmd.Parameters.AddWithValue("quantity", btcBalance.Amount);
    cmd.Parameters.AddWithValue("cost", btcBalance.Amount * 84290.01m);
    var rows = await cmd.ExecuteNonQueryAsync();
    if (rows > 0)
    {
        Console.WriteLine($"Seeded trading.position_cost_basis with {btcBalance.Amount} BTC at 84290.01 USDT.");
    }
}

// Check if trade 1787584 is already known
var knownTrades = await store.GetKnownTradeIdsAsync();
if (!knownTrades.Contains(1787584))
{
    Console.WriteLine("Trade 1787584 not yet recorded in execution fills. Recording now...");
    var executionId = "trade:1787584";
    var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(executionId));
    var ledgerId = new LedgerEntryId(new Guid(digest.AsSpan(0, 16)));
    var correlationId = new CorrelationId(new Guid(digest.AsSpan(16, 16)));
    var idempotency = new IdempotencyKey($"account-execution-{Convert.ToHexString(digest[..16])}");
    var cmdRecord = new ExecutionAccountingCommand(
        executionId,
        1787584,
        6799063,
        new Symbol("BTCUSDT"),
        new Asset("BTC"),
        new Asset("USDT"),
        OrderSide.Sell,
        new Quantity(0.00038000m),
        new Price(84290.01000000m),
        new Money(0m, new Asset("USDT")),
        new AccountId("exchange-wallet"),
        new AccountId("exchange-wallet:trading-fees"),
        ledgerId,
        correlationId,
        idempotency,
        DateTimeOffset.FromUnixTimeMilliseconds(1790461461213),
        "ctp-F8A123C4150DA89A11CE72B17854BC27");

    var res = await store.RecordExecutionAsync(cmdRecord);
    Console.WriteLine($"Successfully recorded execution 1787584: Realized PnL: {res.RealizedPnlQuote}, Avg Cost: {res.AverageCostQuote}");
}

Console.WriteLine("[4/4] Verification:");
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
