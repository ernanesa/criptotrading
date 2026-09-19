using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Exchange.Paper;

public sealed record PaperExchangeOptions
{
    public Percentage FeeRate { get; init; } = new(0.001m);

    public Percentage SlippageRate { get; init; } = new(0m);

    public int MaxOpenOrders { get; init; } = 100;

    public int MaxStoredFills { get; init; } = 10_000;

    /// <summary>Time between accepting a submit command and making the order executable.</summary>
    public TimeSpan SubmissionLatency { get; init; } = TimeSpan.Zero;

    /// <summary>Optional hard bound for a single order quantity.</summary>
    public decimal? MaxOrderQuantity { get; init; }

    /// <summary>Optional hard bound for a single order's reference notional.</summary>
    public decimal? MaxOrderNotional { get; init; }

    /// <summary>Reject a tick when its bid/ask spread exceeds this fraction of the midpoint.</summary>
    public Percentage MaxSpreadRate { get; init; } = new(1m);

    public void Validate()
    {
        if (FeeRate.Value >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(FeeRate), "FeeRate must be lower than 100%.");
        }

        if (SlippageRate.Value >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(SlippageRate), "SlippageRate must be lower than 100%.");
        }

        if (MaxOpenOrders is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOpenOrders), "MaxOpenOrders must be between 1 and 100,000.");
        }

        if (MaxStoredFills is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStoredFills), "MaxStoredFills must be between 1 and 1,000,000.");
        }

        if (SubmissionLatency < TimeSpan.Zero || SubmissionLatency > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(SubmissionLatency), "SubmissionLatency must be between zero and one day.");
        }

        if (MaxOrderQuantity is <= 0m or > 1_000_000_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOrderQuantity), "MaxOrderQuantity must be positive when supplied.");
        }

        if (MaxOrderNotional is <= 0m or > 1_000_000_000_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOrderNotional), "MaxOrderNotional must be positive when supplied.");
        }
    }
}

public enum PaperRejectionReason
{
    QuantityLimit,
    NotionalLimit,
    SpreadLimit,
}

public sealed record PaperOrderRequest(
    ClientOrderId ClientOrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity Quantity,
    Price? LimitPrice = null);

public sealed record PaperMarketTick
{
    public PaperMarketTick(
        Symbol symbol,
        Price bid,
        Price ask,
        Price last,
        DateTimeOffset occurredAtUtc,
        Quantity? availableQuantity = null)
    {
        if (bid.Value > ask.Value)
        {
            throw new ArgumentException("Bid cannot be greater than ask.", nameof(bid));
        }

        Symbol = symbol;
        Bid = bid;
        Ask = ask;
        Last = last;
        OccurredAtUtc = occurredAtUtc;
        if (availableQuantity is { Value: <= 0m })
        {
            throw new ArgumentOutOfRangeException(nameof(availableQuantity), "Available quantity must be greater than zero when supplied.");
        }

        AvailableQuantity = availableQuantity;
    }

    public Symbol Symbol { get; }

    public Price Bid { get; }

    public Price Ask { get; }

    public Price Last { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Relative bid/ask spread, measured against the quote midpoint.</summary>
    public decimal SpreadRate => (Ask.Value - Bid.Value) / ((Ask.Value + Bid.Value) / 2m);

    /// <summary>Optional executable quantity on this tick; omitting it fills the remaining order quantity.</summary>
    public Quantity? AvailableQuantity { get; }
}

public sealed record PaperFill(
    long Sequence,
    OrderId OrderId,
    ClientOrderId ClientOrderId,
    Symbol Symbol,
    OrderSide Side,
    Quantity Quantity,
    Price Price,
    Money Fee,
    decimal SlippageAmount,
    DateTimeOffset OccurredAtUtc);

public sealed record PaperOrderSnapshot(
    OrderId OrderId,
    ClientOrderId ClientOrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity RequestedQuantity,
    Quantity FilledQuantity,
    Price? LimitPrice,
    OrderStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    PaperRejectionReason? RejectionReason = null);

public sealed record PaperSubmitResult(PaperOrderSnapshot Order, bool AlreadyProcessed);

public sealed record PaperCancelResult(PaperOrderSnapshot Order, bool AlreadyProcessed);

public interface IPaperExchange
{
    PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey);

    PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey);

    IReadOnlyList<PaperFill> Apply(PaperMarketTick tick);

    IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks);

    PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId);

    IReadOnlyCollection<PaperOrderSnapshot> OpenOrders { get; }

    IReadOnlyCollection<PaperFill> Fills { get; }
}

public sealed class PaperExchange : IPaperExchange
{
    private readonly TradingClock clock;
    private readonly PaperExchangeOptions options;
    private readonly Dictionary<ClientOrderId, PaperOrderState> orders = [];
    private readonly Dictionary<IdempotencyKey, CommandResult> commands = [];
    private readonly List<PaperFill> fills = [];
    private long fillSequence;

    public PaperExchange(PaperExchangeOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.options = options ?? new PaperExchangeOptions();
        this.options.Validate();
        clock = new TradingClock(timeProvider ?? TimeProvider.System);
    }

    public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders =>
        orders.Values.Where(order => !IsTerminal(order.Order.Status)).Select(ToSnapshot).ToArray();

    public IReadOnlyCollection<PaperFill> Fills => fills.AsReadOnly();

    public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (commands.TryGetValue(idempotencyKey, out var previous))
        {
            if (previous.RequestFingerprint != Fingerprint(request) || previous.Kind != CommandKind.Submit)
            {
                throw new InvalidOperationException($"Idempotency key '{idempotencyKey}' was already used for another command.");
            }

            return new PaperSubmitResult((PaperOrderSnapshot)previous.Result, true);
        }

        if (orders.ContainsKey(request.ClientOrderId))
        {
            throw new InvalidOperationException($"An order with client ID '{request.ClientOrderId}' already exists.");
        }

        if (OpenOrders.Count >= options.MaxOpenOrders)
        {
            throw new InvalidOperationException("The paper exchange open-order bound has been reached.");
        }

        var now = clock.UtcNow;
        var order = Order.Create(OrderId.New(), request.ClientOrderId, request.Symbol, request.Side, request.Type,
            request.Quantity, request.LimitPrice, idempotencyKey, now);
        order.TransitionTo(OrderStatus.Validated, new IdempotencyKey($"{idempotencyKey}-validated"), now);
        order.TransitionTo(OrderStatus.Submitted, new IdempotencyKey($"{idempotencyKey}-submitted"), now);
        var state = new PaperOrderState(order, now + options.SubmissionLatency);
        orders.Add(request.ClientOrderId, state);
        var snapshot = ToSnapshot(state);
        commands.Add(idempotencyKey, new CommandResult(CommandKind.Submit, Fingerprint(request), snapshot));
        return new PaperSubmitResult(snapshot, false);
    }

    public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey)
    {
        if (commands.TryGetValue(idempotencyKey, out var previous))
        {
            if (previous.RequestFingerprint != clientOrderId.Value || previous.Kind != CommandKind.Cancel)
            {
                throw new InvalidOperationException($"Idempotency key '{idempotencyKey}' was already used for another command.");
            }

            return new PaperCancelResult((PaperOrderSnapshot)previous.Result, true);
        }

        if (!orders.TryGetValue(clientOrderId, out var state))
        {
            throw new KeyNotFoundException($"Paper order '{clientOrderId}' was not found.");
        }

        if (!IsTerminal(state.Order.Status))
        {
            if (state.Order.Status == OrderStatus.Submitted)
            {
                state.Order.TransitionTo(OrderStatus.Acknowledged,
                    new IdempotencyKey($"paper-cancel-ack-{state.Order.Id}-{idempotencyKey}"), clock.UtcNow);
            }

            state.Order.TransitionTo(OrderStatus.Cancelled, idempotencyKey, clock.UtcNow);
        }

        var snapshot = ToSnapshot(state);
        commands.Add(idempotencyKey, new CommandResult(CommandKind.Cancel, clientOrderId.Value, snapshot));
        return new PaperCancelResult(snapshot, false);
    }

    public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var created = new List<PaperFill>();

        foreach (var state in orders.Values.Where(candidate => candidate.Order.Symbol == tick.Symbol).ToArray())
        {
            if (IsTerminal(state.Order.Status) || state.Remaining.Value <= 0m || !CanFill(state.Order, tick))
            {
                continue;
            }

            if (tick.OccurredAtUtc < state.ExecutableAtUtc)
            {
                continue;
            }

            if (tick.SpreadRate > options.MaxSpreadRate.Value)
            {
                Reject(state, PaperRejectionReason.SpreadLimit, tick.OccurredAtUtc);
                continue;
            }

            if (options.MaxOrderQuantity is { } maxQuantity && state.Order.Quantity.Value > maxQuantity)
            {
                Reject(state, PaperRejectionReason.QuantityLimit, tick.OccurredAtUtc);
                continue;
            }

            var referencePriceForLimit = state.Order.Side == OrderSide.Buy ? tick.Ask.Value : tick.Bid.Value;
            if (options.MaxOrderNotional is { } maxNotional && state.Order.Quantity.Value * referencePriceForLimit > maxNotional)
            {
                Reject(state, PaperRejectionReason.NotionalLimit, tick.OccurredAtUtc);
                continue;
            }

            var quantity = tick.AvailableQuantity is { } available
                ? new Quantity(Math.Min(state.Remaining.Value, available.Value))
                : state.Remaining;
            var referencePrice = state.Order.Side == OrderSide.Buy ? tick.Ask : tick.Bid;
            var executionPrice = state.Order.Side == OrderSide.Buy
                ? referencePrice.Value * (1m + options.SlippageRate.Value)
                : referencePrice.Value * (1m - options.SlippageRate.Value);
            var price = new Price(executionPrice);
            var fee = new Money(quantity.Value * price.Value * options.FeeRate.Value, new Asset("USDT"));
            var slippage = Math.Abs(price.Value - referencePrice.Value) * quantity.Value;

            if (state.Order.Status == OrderStatus.Submitted)
            {
                state.Order.TransitionTo(OrderStatus.Acknowledged,
                    new IdempotencyKey($"paper-ack-{state.Order.Id}-{tick.OccurredAtUtc:O}"), tick.OccurredAtUtc);
            }

            var target = state.FilledQuantity.Value + quantity.Value >= state.Order.Quantity.Value
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;
            state.Order.TransitionTo(target,
                new IdempotencyKey($"paper-fill-{state.Order.Id}-{tick.OccurredAtUtc:O}"), tick.OccurredAtUtc);
            state.FilledQuantity += quantity;
            var fill = new PaperFill(++fillSequence, state.Order.Id, state.Order.ClientOrderId, state.Order.Symbol,
                state.Order.Side, quantity, price, fee, slippage, tick.OccurredAtUtc);
            fills.Add(fill);
            created.Add(fill);
        }

        if (fills.Count > options.MaxStoredFills)
        {
            fills.RemoveRange(0, fills.Count - options.MaxStoredFills);
        }

        return created;
    }

    public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        var replayed = new List<PaperFill>();
        foreach (var tick in ticks)
        {
            replayed.AddRange(Apply(tick));
        }

        return replayed;
    }

    public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) =>
        orders.TryGetValue(clientOrderId, out var state)
            ? ToSnapshot(state)
            : throw new KeyNotFoundException($"Paper order '{clientOrderId}' was not found.");

    private static bool CanFill(Order order, PaperMarketTick tick) => order.Type switch
    {
        OrderType.Market => true,
        OrderType.Limit when order.LimitPrice is { } limit => order.Side == OrderSide.Buy
            ? tick.Ask.Value <= limit.Value
            : tick.Bid.Value >= limit.Value,
        _ => false,
    };

    private static bool IsTerminal(OrderStatus status) => status is OrderStatus.Filled or OrderStatus.Cancelled
        or OrderStatus.Rejected or OrderStatus.Expired;

    private static void ValidateRequest(PaperOrderRequest request)
    {
        if (request.Quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Order quantity must be greater than zero.");
        }

        if (request.Type == OrderType.Limit && request.LimitPrice is null)
        {
            throw new ArgumentException("Limit orders require a limit price.", nameof(request));
        }

        if (request.Type == OrderType.Market && request.LimitPrice is not null)
        {
            throw new ArgumentException("Market orders cannot have a limit price.", nameof(request));
        }
    }

    private static string Fingerprint(PaperOrderRequest request) =>
        $"{request.ClientOrderId}|{request.Symbol}|{request.Side}|{request.Type}|{request.Quantity}|{request.LimitPrice}";

    private static PaperOrderSnapshot ToSnapshot(PaperOrderState state) => new(state.Order.Id, state.Order.ClientOrderId,
        state.Order.Symbol, state.Order.Side, state.Order.Type, state.Order.Quantity, state.FilledQuantity,
        state.Order.LimitPrice, state.Order.Status, state.Order.CreatedAtUtc, state.Order.UpdatedAtUtc,
        state.RejectionReason);

    private static void Reject(PaperOrderState state, PaperRejectionReason reason, DateTimeOffset occurredAtUtc)
    {
        if (state.Order.Status == OrderStatus.Submitted)
        {
            state.Order.TransitionTo(OrderStatus.Rejected,
                new IdempotencyKey($"paper-reject-{state.Order.Id}-{reason}-{occurredAtUtc:O}"), occurredAtUtc);
        }

        state.RejectionReason = reason;
    }

    private sealed class PaperOrderState(Order order, DateTimeOffset executableAtUtc)
    {
        public Order Order { get; } = order;

        public DateTimeOffset ExecutableAtUtc { get; } = executableAtUtc;

        public PaperRejectionReason? RejectionReason { get; set; }

        public Quantity FilledQuantity { get; set; }

        public Quantity Remaining => new(Order.Quantity.Value - FilledQuantity.Value);
    }

    private sealed record CommandResult(CommandKind Kind, string RequestFingerprint, object Result);

    private enum CommandKind
    {
        Submit,
        Cancel,
    }
}
