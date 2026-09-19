using System.Globalization;
using System.Collections.ObjectModel;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;

namespace CryptoTrading.Reconciliation;

public sealed record ReconciliationOptions
{
    public ReconciliationOptions(
        AccountId ledgerAccount,
        IEnumerable<string> symbols,
        decimal absoluteTolerance = 0.00000001m,
        TimeSpan? maxSnapshotAge = null,
        int maxTradePages = 100)
    {
        LedgerAccount = ledgerAccount;
        ArgumentNullException.ThrowIfNull(symbols);
        var normalized = symbols
            .Select(static symbol => NormalizeSymbol(symbol))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one symbol is required to reconcile fills.", nameof(symbols));
        }

        if (absoluteTolerance < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteTolerance), "Reconciliation tolerance cannot be negative.");
        }

        var snapshotAge = maxSnapshotAge ?? TimeSpan.FromSeconds(30);
        if (snapshotAge <= TimeSpan.Zero || snapshotAge > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotAge), "Snapshot age must be greater than zero and at most 24 hours.");
        }

        if (maxTradePages is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTradePages), "Maximum trade pages must be between 1 and 10,000.");
        }

        Symbols = Array.AsReadOnly(normalized);
        AbsoluteTolerance = absoluteTolerance;
        MaxSnapshotAge = snapshotAge;
        MaxTradePages = maxTradePages;
    }

    public AccountId LedgerAccount { get; }

    public IReadOnlyList<string> Symbols { get; }

    public decimal AbsoluteTolerance { get; }

    public TimeSpan MaxSnapshotAge { get; }

    public int MaxTradePages { get; }

    private static string NormalizeSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Symbols must contain only ASCII letters and digits and be at most 20 characters.", nameof(symbol));
        }

        return normalized;
    }
}

public sealed record ReconciliationBalance(string Asset, decimal Available, decimal Locked)
{
    public decimal Total => Available + Locked;

    public static ReconciliationBalance FromBinance(BinanceBalance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        return new(
            NormalizeAsset(balance.Asset),
            ParseDecimal(balance.Free, nameof(balance.Free)),
            ParseDecimal(balance.Locked, nameof(balance.Locked)));
    }

    private static string NormalizeAsset(string asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        var normalized = asset.Trim().ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Assets must contain only ASCII letters and digits and be at most 20 characters.", nameof(asset));
        }

        return normalized;
    }

    private static decimal ParseDecimal(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0m)
        {
            throw new FormatException($"Binance returned an invalid non-negative decimal for {parameterName}.");
        }

        return parsed;
    }
}

public sealed record ReconciliationFill(string Symbol, long TradeId, long OrderId)
{
    public static ReconciliationFill FromBinance(BinanceTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return new(trade.Symbol.Trim().ToUpperInvariant(), trade.Id, trade.OrderId);
    }
}

public sealed record ReconciliationOpenOrder(string Symbol, long OrderId, string ClientOrderId, string Status)
{
    public static ReconciliationOpenOrder FromBinance(BinanceOpenOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new(order.Symbol.Trim().ToUpperInvariant(), order.OrderId, order.ClientOrderId, order.Status);
    }
}

public sealed record ReconciliationSnapshot(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ReconciliationBalance> Balances,
    IReadOnlyList<ReconciliationFill> Fills,
    IReadOnlyList<ReconciliationOpenOrder> OpenOrders)
{
    public ReconciliationSnapshot(
        DateTimeOffset observedAtUtc,
        IEnumerable<ReconciliationBalance> balances,
        IEnumerable<ReconciliationFill> fills)
        : this(
            ValidateUtc(observedAtUtc),
            Materialize(balances, nameof(balances)),
            Materialize(fills, nameof(fills)),
            Array.AsReadOnly(Array.Empty<ReconciliationOpenOrder>()))
    {
    }

    public ReconciliationSnapshot(
        DateTimeOffset observedAtUtc,
        IEnumerable<ReconciliationBalance> balances,
        IEnumerable<ReconciliationFill> fills,
        IEnumerable<ReconciliationOpenOrder> openOrders)
        : this(
            ValidateUtc(observedAtUtc),
            Materialize(balances, nameof(balances)),
            Materialize(fills, nameof(fills)),
            Materialize(openOrders, nameof(openOrders)))
    {
    }

    private static DateTimeOffset ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Reconciliation timestamps must be UTC.", nameof(value));
        }

        return value;
    }

    private static ReadOnlyCollection<T> Materialize<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values.ToArray());
    }
}

public interface IReadOnlyAccountSnapshotProvider
{
    Task<ReconciliationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ILocalBalanceReader
{
    Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetAssetsAsync(AccountId account, CancellationToken cancellationToken = default);
}

public interface IKnownTradeReader
{
    Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default);
}

public interface ILocalOpenOrderReader
{
    Task<IReadOnlyList<ReconciliationOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);
}

public interface IReconciliationEventSink
{
    Task PublishAsync(ReconciliationEvent reconciliationEvent, CancellationToken cancellationToken = default);
}

public abstract record ReconciliationEvent(
    Guid EventId,
    CorrelationId CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record BalanceReconciled(
    Guid EventId,
    CorrelationId CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string Asset,
    decimal LocalBalance,
    decimal ExchangeBalance,
    decimal Delta,
    decimal Tolerance)
    : ReconciliationEvent(EventId, CorrelationId, OccurredAtUtc);

public sealed record ReconciliationMismatch(
    Guid EventId,
    CorrelationId CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string Asset,
    decimal LocalBalance,
    decimal ExchangeBalance,
    decimal Delta,
    decimal Tolerance,
    IReadOnlyList<long> UnknownTradeIds,
    string ReasonCode = "BALANCE_DRIFT")
    : ReconciliationEvent(EventId, CorrelationId, OccurredAtUtc);

public sealed record OpenOrderReconciliationMismatch(
    Guid EventId,
    CorrelationId CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string Symbol,
    string ClientOrderId,
    long? LocalOrderId,
    long? ExchangeOrderId,
    string? LocalStatus,
    string? ExchangeStatus,
    string ReasonCode)
    : ReconciliationEvent(EventId, CorrelationId, OccurredAtUtc);

public sealed record ReconciliationRunResult(
    CorrelationId CorrelationId,
    DateTimeOffset ObservedAtUtc,
    bool IsHealthy,
    IReadOnlyList<ReconciliationEvent> Events,
    IReadOnlyList<long> UnknownTradeIds,
    int OpenOrderCount = 0)
{
    public int MismatchCount => Events.Count(static item => item is ReconciliationMismatch or OpenOrderReconciliationMismatch);
}

/// <summary>
/// Compares the exchange account snapshot with the local balance projection.
/// Drift is fail-closed: it enters SafeMode and never resets it automatically.
/// </summary>
public sealed class AccountReconciliationService
{
    private const string Actor = "account-reconciliation";
    private readonly IReadOnlyAccountSnapshotProvider snapshotProvider;
    private readonly ILocalBalanceReader balanceReader;
    private readonly IKnownTradeReader knownTradeReader;
    private readonly ILocalOpenOrderReader localOpenOrderReader;
    private readonly IReconciliationEventSink eventSink;
    private readonly IOperationalControl operationalControl;
    private readonly ReconciliationOptions options;
    private readonly TimeProvider timeProvider;

    public AccountReconciliationService(
        IReadOnlyAccountSnapshotProvider snapshotProvider,
        ILocalBalanceReader balanceReader,
        IKnownTradeReader knownTradeReader,
        IReconciliationEventSink eventSink,
        IOperationalControl operationalControl,
        ReconciliationOptions options,
        TimeProvider? timeProvider = null,
        ILocalOpenOrderReader? localOpenOrderReader = null)
    {
        this.snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        this.balanceReader = balanceReader ?? throw new ArgumentNullException(nameof(balanceReader));
        this.knownTradeReader = knownTradeReader ?? throw new ArgumentNullException(nameof(knownTradeReader));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        this.operationalControl = operationalControl ?? throw new ArgumentNullException(nameof(operationalControl));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localOpenOrderReader = localOpenOrderReader ?? new NoLocalOpenOrdersReader();
    }

    public async Task<ReconciliationRunResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshotProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var knownTradeIds = await knownTradeReader.GetKnownTradeIdsAsync(cancellationToken).ConfigureAwait(false);
        var localOpenOrders = await localOpenOrderReader.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationId.New();
        var now = timeProvider.GetUtcNow();
        var snapshotIsFresh = snapshot.ObservedAtUtc <= now
            && now - snapshot.ObservedAtUtc <= options.MaxSnapshotAge;
        var snapshotReason = snapshotIsFresh ? "BALANCE_DRIFT" : "STALE_SNAPSHOT";
        var unknownTradeIds = snapshot.Fills
            .Select(static fill => fill.TradeId)
            .Where(tradeId => !knownTradeIds.Contains(tradeId))
            .Distinct()
            .Order()
            .ToArray();
        var events = new List<ReconciliationEvent>();

        var externalOpenOrders = snapshot.OpenOrders.ToDictionary(static order => order.ClientOrderId, StringComparer.Ordinal);
        var localOpenOrdersByClientId = localOpenOrders.ToDictionary(static order => order.ClientOrderId, StringComparer.Ordinal);
        foreach (var clientOrderId in externalOpenOrders.Keys.Union(localOpenOrdersByClientId.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            externalOpenOrders.TryGetValue(clientOrderId, out var exchangeOrder);
            localOpenOrdersByClientId.TryGetValue(clientOrderId, out var localOrder);
            var reason = exchangeOrder is null
                ? "LOCAL_OPEN_ORDER_MISSING_ON_EXCHANGE"
                : localOrder is null
                    ? "EXCHANGE_OPEN_ORDER_UNKNOWN_LOCALLY"
                    : !string.Equals(exchangeOrder.Symbol, localOrder.Symbol, StringComparison.Ordinal)
                        ? "OPEN_ORDER_SYMBOL_DRIFT"
                        : !string.Equals(exchangeOrder.Status, localOrder.Status, StringComparison.OrdinalIgnoreCase)
                            ? "OPEN_ORDER_STATUS_DRIFT"
                            : null;
            if (reason is null)
            {
                continue;
            }

            var mismatch = new OpenOrderReconciliationMismatch(
                Guid.NewGuid(), correlationId, timeProvider.GetUtcNow(),
                exchangeOrder?.Symbol ?? localOrder!.Symbol, clientOrderId,
                localOrder?.OrderId, exchangeOrder?.OrderId,
                localOrder?.Status, exchangeOrder?.Status, reason);
            events.Add(mismatch);
            await eventSink.PublishAsync(mismatch, cancellationToken).ConfigureAwait(false);
        }

        var exchangeBalances = snapshot.Balances.ToDictionary(static balance => balance.Asset, StringComparer.Ordinal);
        var localAssets = await balanceReader.GetAssetsAsync(options.LedgerAccount, cancellationToken).ConfigureAwait(false);
        var assets = exchangeBalances.Keys
            .Concat(localAssets)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            var localBalance = await balanceReader
                .GetBalanceAsync(options.LedgerAccount, asset, cancellationToken)
                .ConfigureAwait(false);
            var exchangeBalance = exchangeBalances.GetValueOrDefault(asset);
            var exchangeTotal = exchangeBalance?.Total ?? 0m;
            var delta = exchangeTotal - localBalance;
            var occurredAt = timeProvider.GetUtcNow();
            ReconciliationEvent reconciliationEvent;
            if (snapshotIsFresh && Math.Abs(delta) <= options.AbsoluteTolerance && unknownTradeIds.Length == 0)
            {
                reconciliationEvent = new BalanceReconciled(
                    Guid.NewGuid(), correlationId, occurredAt, asset,
                    localBalance, exchangeTotal, delta, options.AbsoluteTolerance);
            }
            else
            {
                reconciliationEvent = new ReconciliationMismatch(
                    Guid.NewGuid(), correlationId, occurredAt, asset,
                    localBalance, exchangeTotal, delta, options.AbsoluteTolerance, unknownTradeIds, snapshotReason);
            }

            events.Add(reconciliationEvent);
            await eventSink.PublishAsync(reconciliationEvent, cancellationToken).ConfigureAwait(false);
        }

        // An account may legitimately return no balance rows while the trade endpoint
        // still reveals an unknown fill. Preserve that evidence as an explicit mismatch
        // instead of allowing an empty event stream to look healthy.
        if ((!snapshotIsFresh || unknownTradeIds.Length > 0) && snapshot.Balances.Count == 0)
        {
            var occurredAt = timeProvider.GetUtcNow();
            var reconciliationEvent = new ReconciliationMismatch(
                Guid.NewGuid(), correlationId, occurredAt, "*",
                0m, 0m, 0m, options.AbsoluteTolerance, unknownTradeIds, snapshotReason);
            events.Add(reconciliationEvent);
            await eventSink.PublishAsync(reconciliationEvent, cancellationToken).ConfigureAwait(false);
        }

        var healthy = events.All(static item => item is BalanceReconciled)
            && unknownTradeIds.Length == 0
            && !events.Any(static item => item is OpenOrderReconciliationMismatch);
        if (!healthy)
        {
            var command = new OperationalControlCommand(
                Actor,
                $"Account reconciliation drift detected (correlation {correlationId}).",
                new IdempotencyKey($"reconciliation:{correlationId}"),
                timeProvider.GetUtcNow());
            _ = operationalControl.EnterSafeMode(command);
        }

        return new(correlationId, snapshot.ObservedAtUtc, healthy, events.AsReadOnly(), unknownTradeIds, snapshot.OpenOrders.Count);
    }
}

/// <summary>Read-only Binance adapter for the generic reconciliation contract.</summary>
public sealed class BinanceReadOnlySnapshotProvider : IReadOnlyAccountSnapshotProvider
{
    private readonly IBinanceSpotAccountClient accountClient;
    private readonly ReconciliationOptions options;
    private readonly TimeProvider timeProvider;

    public BinanceReadOnlySnapshotProvider(
        IBinanceSpotAccountClient accountClient,
        ReconciliationOptions options,
        TimeProvider? timeProvider = null)
    {
        this.accountClient = accountClient ?? throw new ArgumentNullException(nameof(accountClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReconciliationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        var trades = new Dictionary<long, ReconciliationFill>();
        var openOrders = new Dictionary<long, ReconciliationOpenOrder>();
        foreach (var symbol in options.Symbols)
        {
            var symbolOpenOrders = await accountClient.GetOpenOrdersAsync(symbol, cancellationToken).ConfigureAwait(false);
            foreach (var order in symbolOpenOrders)
            {
                openOrders.TryAdd(order.OrderId, ReconciliationOpenOrder.FromBinance(order));
            }

            long? fromId = null;
            var completed = false;
            for (var page = 0; page < options.MaxTradePages; page++)
            {
                var symbolTrades = await accountClient.GetMyTradesAsync(
                        symbol,
                        new BinanceTradeQuery(FromId: fromId, Limit: 1_000),
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var trade in symbolTrades)
                {
                    trades.TryAdd(trade.Id, ReconciliationFill.FromBinance(trade));
                }

                if (symbolTrades.Count < 1_000)
                {
                    completed = true;
                    break;
                }

                var nextFromId = symbolTrades.Max(static trade => trade.Id);
                if (nextFromId == long.MaxValue || (fromId is not null && nextFromId < fromId.Value))
                {
                    throw new InvalidOperationException($"Binance trade pagination did not advance for symbol '{symbol}'.");
                }

                fromId = nextFromId + 1;
            }

            if (!completed)
            {
                throw new InvalidOperationException(
                    $"Binance trade pagination exceeded the configured limit of {options.MaxTradePages} pages for symbol '{symbol}'.");
            }
        }

        return new(
            timeProvider.GetUtcNow(),
            account.Balances.Select(ReconciliationBalance.FromBinance),
            trades.Values.OrderBy(static trade => trade.TradeId).ToArray(),
            openOrders.Values.OrderBy(static order => order.OrderId).ToArray());
    }
}

public sealed class LedgerBalanceReader : ILocalBalanceReader
{
    private readonly ILedger ledger;

    public LedgerBalanceReader(ILedger ledger) => this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ledger.GetBalance(account, new Asset(asset)).Amount);
    }

    public Task<IReadOnlySet<string>> GetAssetsAsync(AccountId account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ledger.GetAssets(account));
    }
}

public sealed class NoKnownTradesReader : IKnownTradeReader
{
    public Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());
    }
}

public sealed class NoLocalOpenOrdersReader : ILocalOpenOrderReader
{
    public Task<IReadOnlyList<ReconciliationOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ReconciliationOpenOrder>>([]);
    }
}

public sealed class InMemoryReconciliationEventSink : IReconciliationEventSink
{
    private readonly List<ReconciliationEvent> events = [];

    public IReadOnlyList<ReconciliationEvent> Events => events.AsReadOnly();

    public Task PublishAsync(ReconciliationEvent reconciliationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliationEvent);
        cancellationToken.ThrowIfCancellationRequested();
        events.Add(reconciliationEvent);
        return Task.CompletedTask;
    }
}
