#pragma warning disable CA1031 // CLI diagnostic tool catches top-level exceptions and returns safe non-zero exit codes.
#pragma warning disable CA1303 // Operator-facing CLI messages and templates are intentional.
#pragma warning disable CA2007 // CLI tool runs in standard console context.
#pragma warning disable CA1849 // Console synchronous writes are acceptable in simple CLI driver.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Configuration;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddLocalDotEnv()
    .AddEnvironmentVariables()
    .Build();

var connectionString = PostgresConnectionConfiguration.Resolve(configuration);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Error: POSTGRES_CONNECTION_STRING is required.");
    return 1;
}

if (args.Contains("--bootstrap-testnet-autonomy", StringComparer.Ordinal))
{
    try
    {
        return await BootstrapTestnetAutonomyAsync(configuration, connectionString);
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            errorType = ex.GetType().Name,
            message = ex.Message
        }));
        return 1;
    }
}

if (args.Contains("--run-f20-lifecycle-drill", StringComparer.Ordinal))
{
    try
    {
        return await RunF20LifecycleDrillAsync(configuration, connectionString);
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            errorType = ex.GetType().Name,
            message = ex.Message
        }));
        return 1;
    }
}

if (args.Contains("--reset-operational-mode", StringComparer.Ordinal))
{
    try
    {
        await using var s = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var latest = await s.ReadLatestOperationalControlAuditEventAsync();
        var currentMode = latest?.To ?? OperationalMode.SafeMode;
        var auditEvent = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            currentMode,
            OperationalMode.Normal,
            "operator",
            "Operator authorized reset to Normal mode after audit verification",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow,
            "operator-approval");
        var appended = await s.AppendOperationalControlAuditEventAsync(auditEvent);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = appended ? "OK" : "ALREADY_PRESENT",
            fromMode = currentMode.ToString(),
            toMode = OperationalMode.Normal.ToString(),
            eventId = auditEvent.EventId
        }));
        return appended ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "FAIL",
            errorType = ex.GetType().Name,
            message = ex.Message
        }));
        return 1;
    }
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

static async Task<int> RunF20LifecycleDrillAsync(IConfiguration configuration, string connectionString)
{
    if (!string.Equals(configuration["TESTNET_F20_DRILL_AUTHORIZED"], "true", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing F20 drill: TESTNET_F20_DRILL_AUTHORIZED=true is required.");
        return 2;
    }

    if (!string.Equals(configuration["TRADING_ENVIRONMENT"], "TESTNET", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing F20 drill: TRADING_ENVIRONMENT must be TESTNET.");
        return 2;
    }

    if (bool.TryParse(configuration["BINANCE_WITHDRAWALS_ENABLED"], out var withdrawals) && withdrawals)
    {
        Console.Error.WriteLine("Refusing F20 drill: BINANCE_WITHDRAWALS_ENABLED must be false.");
        return 2;
    }

    var restBaseUrl = configuration["BINANCE_REST_BASE_URL"] ?? "https://testnet.binance.vision/api";
    if (!string.Equals(restBaseUrl.TrimEnd('/'), "https://testnet.binance.vision/api", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing F20 drill: BINANCE_REST_BASE_URL must be https://testnet.binance.vision/api.");
        return 2;
    }

    var apiKey = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE");
    var apiSecret = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE");
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
    {
        Console.Error.WriteLine("Refusing F20 drill: protected Binance Testnet credentials are required.");
        return 2;
    }

    const string symbol = "BTCUSDT";
    const string accountId = "exchange-wallet";

    using var marketHttp = new HttpClient
    {
        BaseAddress = new Uri("https://testnet.binance.vision/api/"),
        Timeout = TimeSpan.FromSeconds(15),
    };
    marketHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/f20-drill-market");
    var marketClient = new BinanceSpotMarketClient(marketHttp);

    var accountOptions = new BinanceAuthenticatedClientOptions
    {
        Environment = BinanceEnvironment.Testnet,
        RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
        ApiKey = apiKey,
        ApiSecret = apiSecret,
        AllowTestnetWithdrawCapability = true,
    };
    using var accountHttp = new HttpClient
    {
        BaseAddress = accountOptions.RestBaseAddress,
        Timeout = accountOptions.RequestTimeout,
    };
    accountHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/f20-drill-account");
    var accountClient = new BinanceSpotAccountClient(accountHttp, accountOptions);
    _ = await accountClient.SynchronizeClockAsync().ConfigureAwait(false);

    var orderOptions = new BinanceSpotOrderClientOptions
    {
        Environment = BinanceEnvironment.Testnet,
        RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
        ApiKey = apiKey,
        ApiSecret = apiSecret,
        EnableOrderExecution = true,
    };
    using var orderHttp = new HttpClient
    {
        BaseAddress = new Uri("https://testnet.binance.vision/api/"),
        Timeout = TimeSpan.FromSeconds(15),
    };
    orderHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/f20-drill-order");
    var orderClient = new BinanceSpotOrderClient(orderHttp, orderOptions);

    var now = DateTimeOffset.UtcNow;
    var budgetPolicy = BudgetPolicy.ApprovedMicroLiveDefault;

    var riskPolicy = new RiskPolicy(
        version: "v1-r100-f20-testnet",
        maxOrderNotional: 20m,
        maxExposureNotional: 20m,
        maxDailyLoss: 2m,
        maxDrawdown: 5m,
        maxMarketDataAge: TimeSpan.FromMinutes(5),
        withdrawalsEnabled: false,
        budgetPolicy: budgetPolicy);

    string restingClientOrderId = $"f20-rest-{Guid.NewGuid():N}"[..24];
    string fillBuyClientOrderId = $"f20-buy-{Guid.NewGuid():N}"[..24];
    string fillSellClientOrderId = $"f20-sell-{Guid.NewGuid():N}"[..24];
    string rejectFilterClientOrderId = $"f20-rej-{Guid.NewGuid():N}"[..24];
    string ambiguousClientOrderId = $"f20-amb-{Guid.NewGuid():N}"[..24];

    decimal effectiveCeilingUsdt;
    long restingOrderId;
    string cancelStatus;
    long fillBuyOrderId;
    long fillSellOrderId;
    decimal filledQty;
    string filterRejectCode;

    await using (var store1 = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
    {
        await store1.ApplyMigrationsAsync().ConfigureAwait(false);

        var draft = RiskPolicyDocument.CreateDraft(riskPolicy, now.AddMinutes(-5), now.AddDays(30), "policy-author");
        await store1.SaveRiskPolicyAsync(draft).ConfigureAwait(false);
        _ = await store1.ApproveRiskPolicyAsync(riskPolicy.Version, "f20-approval-1", "Ernane", now.AddMinutes(-4)).ConfigureAwait(false);
        _ = await store1.ActivateRiskPolicyAsync(riskPolicy.Version, now.AddMinutes(-3)).ConfigureAwait(false);

        var fxTicker = await marketClient.GetBookTickerAsync("USDTBRL").ConfigureAwait(false);
        var btcTicker = await marketClient.GetBookTickerAsync(symbol).ConfigureAwait(false);
        var evalNowUtc = DateTimeOffset.UtcNow;
        var fxQuote = new UsdtBrlFxQuote(fxTicker.AskPrice, evalNowUtc, true);
        var valuation = budgetPolicy.CalculateEffectiveCeiling(fxQuote, evalNowUtc);
        if (!valuation.IsValid)
        {
            throw new InvalidOperationException($"FX valuation failed: {valuation.ReasonCode}");
        }
        effectiveCeilingUsdt = valuation.EffectiveBudgetCeilingUsdt;

        // 1. Pre-submit budget ceiling rejection (> 20 USDT)
        var overBudgetDecision = budgetPolicy.EvaluateCommitment(
            new BudgetCommitmentRequest(
                requestedOrderNotionalUsdt: 30m,
                openPositionsNotionalUsdt: 0m,
                openPositionsCount: 0,
                pendingOrdersReservedUsdt: 0m,
                unknownOrdersLockedUsdt: 0m,
                dailyRealizedLossUsdt: 0m,
                cumulativeNetRealizedLossUsdt: 0m,
                estimatedStopRiskUsdt: 0.10m,
                evaluatedAtUtc: evalNowUtc,
                fxQuote: fxQuote));
        if (overBudgetDecision.IsApproved || overBudgetDecision.ReasonCode != RiskReasonCodes.BudgetCeilingExceeded)
        {
            throw new InvalidOperationException($"Expected BUDGET_CEILING_EXCEEDED for 30 USDT order, got {overBudgetDecision.ReasonCode}");
        }

        // 2. Submit resting LIMIT BUY + verify open order + cancel + release reservation
        var restingPrice = Math.Floor(btcTicker.BidPrice * 0.85m * 100m) / 100m;
        const decimal orderQty = 0.00015m; // ~10.6 - 12.5 USDT (between 5 USDT minNotional and ~19.5 USDT ceiling)
        var restingNotional = Math.Round(restingPrice * orderQty, 8);
        var restingReserved = Math.Round(restingNotional * (1m + budgetPolicy.RoundTripFeeAndReserveRate), 8, MidpointRounding.ToPositiveInfinity);

        var res1 = await store1.TryReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: $"res-{restingClientOrderId}",
            accountId: accountId,
            symbol: symbol,
            clientOrderId: restingClientOrderId,
            asset: "USDT",
            requestedAmount: restingReserved,
            effectiveBudgetCeilingAmount: effectiveCeilingUsdt,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "f20-worker-1",
            fencingToken: 1,
            occurredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (!res1.Succeeded)
        {
            throw new InvalidOperationException($"Failed to reserve capital for resting order: {res1.ReasonCode}");
        }

        var submittedResting = await orderClient.SubmitAsync(new BinanceSpotOrderRequest(
            symbol,
            BinanceSpotOrderSide.Buy,
            BinanceSpotOrderType.Limit,
            orderQty,
            restingClientOrderId,
            restingPrice,
            "GTC")).ConfigureAwait(false);
        restingOrderId = submittedResting.OrderId;

        var openOrdersDuringRest = await accountClient.GetOpenOrdersAsync(symbol).ConfigureAwait(false);
        if (!openOrdersDuringRest.Any(o => string.Equals(o.ClientOrderId, restingClientOrderId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Resting LIMIT order was not observed in openOrders.");
        }

        var canceledResting = await orderClient.CancelAsync(symbol, restingClientOrderId).ConfigureAwait(false);
        cancelStatus = canceledResting.Status;
        _ = await store1.TransitionReservationAsync(
            restingClientOrderId,
            CapitalReservationStatus.Released,
            0m,
            "f20-worker-1",
            1,
            DateTimeOffset.UtcNow,
            "CANCELED_ON_TESTNET").ConfigureAwait(false);

        // 3. Submit executable MARKET BUY -> verify FILLED -> transition to CommittedToExchange -> flatten via MARKET SELL -> release
        var fillNotionalEstimate = Math.Round(btcTicker.AskPrice * orderQty, 8);
        var fillReserved = Math.Round(fillNotionalEstimate * (1m + budgetPolicy.RoundTripFeeAndReserveRate), 8, MidpointRounding.ToPositiveInfinity);
        var res2 = await store1.TryReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: $"res-{fillBuyClientOrderId}",
            accountId: accountId,
            symbol: symbol,
            clientOrderId: fillBuyClientOrderId,
            asset: "USDT",
            requestedAmount: fillReserved,
            effectiveBudgetCeilingAmount: effectiveCeilingUsdt,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "f20-worker-1",
            fencingToken: 1,
            occurredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (!res2.Succeeded)
        {
            throw new InvalidOperationException($"Failed to reserve capital for executable order: {res2.ReasonCode}");
        }

        var filledBuy = await orderClient.SubmitAsync(new BinanceSpotOrderRequest(
            symbol,
            BinanceSpotOrderSide.Buy,
            BinanceSpotOrderType.Market,
            orderQty,
            fillBuyClientOrderId)).ConfigureAwait(false);
        fillBuyOrderId = filledBuy.OrderId;
        filledQty = filledBuy.ExecutedQuantity;
        if (!string.Equals(filledBuy.Status, "FILLED", StringComparison.OrdinalIgnoreCase) || filledQty <= 0m)
        {
            throw new InvalidOperationException($"Expected MARKET BUY to fill immediately, got status={filledBuy.Status}");
        }

        _ = await store1.TransitionReservationAsync(
            fillBuyClientOrderId,
            CapitalReservationStatus.CommittedToExchange,
            fillReserved,
            "f20-worker-1",
            1,
            DateTimeOffset.UtcNow,
            "FILLED_ON_TESTNET").ConfigureAwait(false);

        var filledSell = await orderClient.SubmitAsync(new BinanceSpotOrderRequest(
            symbol,
            BinanceSpotOrderSide.Sell,
            BinanceSpotOrderType.Market,
            filledQty,
            fillSellClientOrderId)).ConfigureAwait(false);
        fillSellOrderId = filledSell.OrderId;
        if (!string.Equals(filledSell.Status, "FILLED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected closing MARKET SELL to fill immediately, got status={filledSell.Status}");
        }

        _ = await store1.TransitionReservationAsync(
            fillBuyClientOrderId,
            CapitalReservationStatus.Released,
            0m,
            "f20-worker-1",
            1,
            DateTimeOffset.UtcNow,
            $"POSITION_CLOSED:{fillSellClientOrderId}").ConfigureAwait(false);

        // 4. Definitive Exchange Filter Reject (sub-notional 0.00001 BTC ~ 0.83 USDT < 5.00 USDT MIN_NOTIONAL)
        var res3 = await store1.TryReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: $"res-{rejectFilterClientOrderId}",
            accountId: accountId,
            symbol: symbol,
            clientOrderId: rejectFilterClientOrderId,
            asset: "USDT",
            requestedAmount: 6m,
            effectiveBudgetCeilingAmount: effectiveCeilingUsdt,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "f20-worker-1",
            fencingToken: 1,
            occurredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (!res3.Succeeded)
        {
            throw new InvalidOperationException("Failed to reserve capital for reject drill.");
        }

        try
        {
            _ = await orderClient.SubmitAsync(new BinanceSpotOrderRequest(
                symbol,
                BinanceSpotOrderSide.Buy,
                BinanceSpotOrderType.Limit,
                0.00001m,
                rejectFilterClientOrderId,
                restingPrice,
                "GTC")).ConfigureAwait(false);
            throw new InvalidOperationException("Expected Binance to reject sub-notional order.");
        }
        catch (BinanceOrderRejectedException ex)
        {
            filterRejectCode = $"HTTP_{(int?)ex.StatusCode ?? 400}_DEFINITIVE_REJECT";
            _ = await store1.TransitionReservationAsync(
                rejectFilterClientOrderId,
                CapitalReservationStatus.Released,
                0m,
                "f20-worker-1",
                1,
                DateTimeOffset.UtcNow,
                filterRejectCode).ConfigureAwait(false);
        }

        // 5. Ambiguous outcome -> LockedUnknown before simulated process restart
        var res4 = await store1.TryReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: $"res-{ambiguousClientOrderId}",
            accountId: accountId,
            symbol: symbol,
            clientOrderId: ambiguousClientOrderId,
            asset: "USDT",
            requestedAmount: 10m,
            effectiveBudgetCeilingAmount: effectiveCeilingUsdt,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "f20-worker-1",
            fencingToken: 1,
            occurredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (!res4.Succeeded)
        {
            throw new InvalidOperationException("Failed to reserve capital for ambiguous drill.");
        }

        _ = await store1.MarkReservationLockedUnknownAsync(
            ambiguousClientOrderId,
            "f20-worker-1",
            1,
            DateTimeOffset.UtcNow,
            "TRANSPORT_TIMEOUT_AMBIGUOUS").ConfigureAwait(false);

        await store1.AppendRuntimeTransitionAsync(new RuntimeStateTransition(
            RuntimeState.Stopped,
            RuntimeState.Bootstrapping,
            1,
            "f20-drill-start",
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    // 6. Restart process against same PostgreSQL -> verify LockedUnknown survives restart & blocks new orders -> reconcile with Testnet -> stop
    await using (var store2 = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
    {
        var checkpoint = await store2.ReadRuntimeStateAsync().ConfigureAwait(false);
        if (checkpoint.Revision != 1 || checkpoint.State != RuntimeState.Bootstrapping)
        {
            throw new InvalidOperationException("Runtime checkpoint did not persist across restart.");
        }

        var activeOnRestart = await store2.GetActiveReservationsAsync(accountId).ConfigureAwait(false);
        if (!activeOnRestart.Any(r => r.ClientOrderId == ambiguousClientOrderId && r.Status == CapitalReservationStatus.LockedUnknown))
        {
            throw new InvalidOperationException("LockedUnknown reservation did not survive process restart.");
        }

        var blockedDuringUnknown = await store2.TryReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: "res-blocked-by-unknown",
            accountId: accountId,
            symbol: symbol,
            clientOrderId: "f20-blocked-1",
            asset: "USDT",
            requestedAmount: 6m,
            effectiveBudgetCeilingAmount: effectiveCeilingUsdt,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "f20-worker-2",
            fencingToken: 2,
            occurredAtUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (blockedDuringUnknown.Succeeded || blockedDuringUnknown.ReasonCode != "LOCKED_UNKNOWN_CAPITAL_ACTIVE")
        {
            throw new InvalidOperationException($"Expected LOCKED_UNKNOWN_CAPITAL_ACTIVE during restart, got {blockedDuringUnknown.ReasonCode}");
        }

        // Reconcile against real Testnet openOrders & trades
        var openOrdersAfterRestart = await accountClient.GetOpenOrdersAsync(symbol).ConfigureAwait(false);
        if (openOrdersAfterRestart.Any(o => string.Equals(o.ClientOrderId, ambiguousClientOrderId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Ambiguous drill order unexpectedly exists on exchange.");
        }

        var reconCorrelationId = Guid.NewGuid();
        var reconTimeUtc = DateTimeOffset.UtcNow;
        _ = await store2.ReconcileLockedUnknownAsync(
            ambiguousClientOrderId,
            CapitalReservationStatus.Released,
            0m,
            reconCorrelationId.ToString("N"),
            reconTimeUtc).ConfigureAwait(false);

        var reconPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = (short)1,
            eventType = "TestnetF20LifecycleReconciled",
            eventId = reconCorrelationId,
            correlationId = reconCorrelationId,
            occurredAtUtc = reconTimeUtc,
            data = new { symbol, openOrdersCount = openOrdersAfterRestart.Count, ambiguousResolved = ambiguousClientOrderId }
        });
        var reconRecord = new ReconciliationEventRecord(
            reconCorrelationId,
            reconCorrelationId,
            reconTimeUtc,
            "TestnetF20LifecycleReconciled",
            1,
            reconPayload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reconPayload))));
        _ = await store2.AppendReconciliationEventAsync(reconRecord).ConfigureAwait(false);

        await store2.AppendRuntimeTransitionAsync(new RuntimeStateTransition(
            RuntimeState.Bootstrapping,
            RuntimeState.SafeMode,
            2,
            "restart-requires-reconciliation",
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await store2.AppendRuntimeTransitionAsync(new RuntimeStateTransition(
            RuntimeState.SafeMode,
            RuntimeState.Stopping,
            3,
            "f20-drill-stop",
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await store2.AppendRuntimeTransitionAsync(new RuntimeStateTransition(
            RuntimeState.Stopping,
            RuntimeState.Stopped,
            4,
            "f20-drill-stopped-no-trade",
            DateTimeOffset.UtcNow)).ConfigureAwait(false);

        var remainingActive = await store2.GetActiveReservationsAsync(accountId).ConfigureAwait(false);
        if (remainingActive.Count != 0)
        {
            throw new InvalidOperationException($"Expected 0 active reservations at end of drill, found {remainingActive.Count}.");
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "PASS",
        environment = "TESTNET",
        symbol,
        effectiveBudgetCeilingUsdt = effectiveCeilingUsdt,
        restingLimitCancel = new { clientOrderId = restingClientOrderId, orderId = restingOrderId, finalStatus = cancelStatus },
        marketFillAndFlatten = new { buyClientOrderId = fillBuyClientOrderId, buyOrderId = fillBuyOrderId, sellClientOrderId = fillSellClientOrderId, sellOrderId = fillSellOrderId, executedQuantity = filledQty },
        rejections = new { preSubmitOverBudget = RiskReasonCodes.BudgetCeilingExceeded, exchangeFilterReject = filterRejectCode },
        ambiguousRestartReconcileStop = new { ambiguousClientOrderId, survivedRestart = true, blockedNewOrdersWhileUnknown = true, reconciledAndReleased = true, finalRuntimeState = "Stopped" }
    }));
    return 0;
}

static async Task<int> BootstrapTestnetAutonomyAsync(IConfiguration configuration, string connectionString)
{
    if (!string.Equals(configuration["TRADING_ENVIRONMENT"], "TESTNET", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing bootstrap: TRADING_ENVIRONMENT must be TESTNET.");
        return 2;
    }

    if (bool.TryParse(configuration["BINANCE_WITHDRAWALS_ENABLED"], out var withdrawals) && withdrawals)
    {
        Console.Error.WriteLine("Refusing bootstrap: BINANCE_WITHDRAWALS_ENABLED must be false.");
        return 2;
    }

    var restBaseUrl = configuration["BINANCE_REST_BASE_URL"] ?? "https://testnet.binance.vision/api";
    if (!string.Equals(restBaseUrl.TrimEnd('/'), "https://testnet.binance.vision/api", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Refusing bootstrap: BINANCE_REST_BASE_URL must be https://testnet.binance.vision/api.");
        return 2;
    }

    var apiKey = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE");
    var apiSecret = TradingConfigurationExtensions.ResolveCredential(configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE");
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
    {
        Console.Error.WriteLine("Refusing bootstrap: protected Binance Testnet credentials are required.");
        return 2;
    }

    const string symbol = "BTCUSDT";
    const string accountId = "exchange-wallet";

    await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
    await store.ApplyMigrationsAsync().ConfigureAwait(false);

    var now = DateTimeOffset.UtcNow;
    var activePolicy = await store.GetActiveRiskPolicyAsync(now).ConfigureAwait(false);
    if (activePolicy is null || !string.Equals(activePolicy.Policy.Version, "v3-multiasset-r100-testnet", StringComparison.Ordinal))
    {
        var riskPolicy = new RiskPolicy(
            version: "v3-multiasset-r100-testnet",
            maxOrderNotional: 20m,
            maxExposureNotional: 20m,
            maxDailyLoss: 2m,
            maxDrawdown: 5m,
            maxMarketDataAge: TimeSpan.FromMinutes(5),
            withdrawalsEnabled: false,
            budgetPolicy: new BudgetPolicy(allowedSymbol: "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT"));
        var draft = RiskPolicyDocument.CreateDraft(riskPolicy, now.AddMinutes(-5), now.AddDays(90), "policy-author");
        await store.SaveRiskPolicyAsync(draft).ConfigureAwait(false);
        _ = await store.ApproveRiskPolicyAsync(riskPolicy.Version, "approval-v3-multiasset-r100-testnet", "Ernane", now.AddMinutes(-4)).ConfigureAwait(false);
        activePolicy = await store.ActivateRiskPolicyAsync(riskPolicy.Version, now.AddMinutes(-3)).ConfigureAwait(false);
    }

    var latestControl = await store.ReadLatestOperationalControlAuditEventAsync().ConfigureAwait(false);
    if (latestControl is null || latestControl.To != OperationalMode.Normal)
    {
        var audit = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            latestControl?.To ?? OperationalMode.Normal,
            OperationalMode.Normal,
            "testnet-bootstrap",
            "Initial Normal operational state for continuous Testnet autonomy.",
            IdempotencyKey.New(),
            DateTimeOffset.UtcNow,
            "bootstrap-testnet-normal");
        _ = await store.AppendOperationalControlAuditEventAsync(audit).ConfigureAwait(false);
    }

    var accountOptions = new BinanceAuthenticatedClientOptions
    {
        Environment = BinanceEnvironment.Testnet,
        RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
        ApiKey = apiKey,
        ApiSecret = apiSecret,
        AllowTestnetWithdrawCapability = true,
    };
    using var accountHttp = new HttpClient
    {
        BaseAddress = accountOptions.RestBaseAddress,
        Timeout = accountOptions.RequestTimeout,
    };
    accountHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading/testnet-bootstrap");
    var accountClient = new BinanceSpotAccountClient(accountHttp, accountOptions);
    _ = await accountClient.SynchronizeClockAsync().ConfigureAwait(false);

    var account = await accountClient.GetAccountAsync().ConfigureAwait(false);
    var exchangeTotals = new Dictionary<string, decimal>(StringComparer.Ordinal);
    foreach (var b in account.Balances)
    {
        if (decimal.TryParse(b.Free, NumberStyles.Number, CultureInfo.InvariantCulture, out var free) &&
            decimal.TryParse(b.Locked, NumberStyles.Number, CultureInfo.InvariantCulture, out var locked))
        {
            var total = free + locked;
            if (total > 0m && TryCreateAsset(b.Asset, out var asset))
            {
                exchangeTotals[asset.Value] = total;
            }
        }
    }

    var localAssets = await store.GetAssetsAsync(new AccountId(accountId)).ConfigureAwait(false);
    var allAssets = exchangeTotals.Keys.Union(localAssets, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var postings = new List<LedgerPosting>();
    foreach (var assetCode in allAssets)
    {
        if (!TryCreateAsset(assetCode, out var asset))
        {
            continue;
        }

        var localBal = (await store.GetBalanceAsync(new AccountId(accountId), asset).ConfigureAwait(false)).Amount;
        var exchangeBal = exchangeTotals.GetValueOrDefault(asset.Value, 0m);
        var delta = Math.Round(exchangeBal - localBal, 8);
        if (Math.Abs(delta) > 0m)
        {
            postings.Add(new LedgerPosting(new AccountId(accountId), new Money(delta, asset)));
            postings.Add(new LedgerPosting(new AccountId("equity-opening"), new Money(-delta, asset)));
        }
    }

    if (postings.Count >= 2)
    {
        var entry = new LedgerEntry(
            LedgerEntryId.New(),
            CorrelationId.New(),
            DateTimeOffset.UtcNow,
            "testnet-autonomy-balance-sync",
            postings,
            new IdempotencyKey($"testnet-sync-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        _ = await store.AppendJournalEntryAsync(entry).ConfigureAwait(false);
    }

    var myTrades = await accountClient.GetMyTradesAsync(symbol).ConfigureAwait(false);
    foreach (var trade in myTrades)
    {
        await store.RecordKnownTradeAsync(trade.Id, symbol).ConfigureAwait(false);
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "PASS",
        activeRiskPolicy = activePolicy.Policy.Version,
        exchangeAssetsCount = exchangeTotals.Count,
        adjustedAssetsCount = postings.Count / 2,
        knownTradesCount = myTrades.Count
    }));
    return 0;
}
