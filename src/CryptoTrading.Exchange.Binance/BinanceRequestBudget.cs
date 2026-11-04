using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoTrading.Exchange.Binance;

public sealed record BinanceRateLimitDefinition(string RateLimitType, string Interval, int IntervalNum, long Limit);
public interface IBinanceBudgetClockIdentity
{
    string MonotonicOrigin { get; }
}
public sealed class BinanceRequestBudgetRejectedException : InvalidOperationException
{
    public BinanceRequestBudgetRejectedException() : base("RATE_BUDGET_REJECTED") { }
    public BinanceRequestBudgetRejectedException(string message) : base(message) { }
    public BinanceRequestBudgetRejectedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Conservative sliding-window admission shared by cooperating local processes.
/// Reservations are never refunded on transport ambiguity. Observed server peaks
/// are additive headroom protection, not permission to spend an inferred capacity.
/// </summary>
public sealed class BinanceRequestBudget
{
    private readonly string directory;
    private readonly TimeProvider timeProvider;
    private readonly BinanceRateLimitDefinition[]? initialLimits;
    private readonly string clockOrigin;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> inFlightGates = new(StringComparer.Ordinal);
    private const int MaximumJournalBytes = 131_072;
    public static BinanceRequestBudget Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CryptoTrading", "binance-request-budgets"));

    public BinanceRequestBudget(string directory, TimeProvider? timeProvider = null,
        IReadOnlyList<BinanceRateLimitDefinition>? initialLimits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.timeProvider.TimestampFrequency <= 0 || this.timeProvider.TimestampFrequency > long.MaxValue / 86_400 / 2)
            throw new ArgumentOutOfRangeException(nameof(timeProvider));
        clockOrigin = ReferenceEquals(this.timeProvider, TimeProvider.System) ? "system-stopwatch-v1"
            : this.timeProvider is IBinanceBudgetClockIdentity identified && !string.IsNullOrWhiteSpace(identified.MonotonicOrigin)
                ? identified.MonotonicOrigin : throw new ArgumentException("Custom quota clocks must declare a shared monotonic origin.", nameof(timeProvider));
        this.initialLimits = initialLimits?.ToArray();
        if (this.initialLimits is not null) _ = Windows(this.initialLimits);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new ArgumentException("A request URI is required.", nameof(request));
        uri = uri.IsAbsoluteUri ? uri : new Uri(client.BaseAddress ?? throw new InvalidOperationException("HTTP base address is missing."), uri);
        request.RequestUri = uri;
        var scope = Scope(uri);
        var gate = inFlightGates.GetOrAdd(scope, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cost = Cost(request.Method, uri.AbsolutePath);
            var discover = await ChangeAsync(scope, state =>
            {
                CheckBlocked(state);
                if (state.Windows.Count == 0 && initialLimits is not null) Install(state, initialLimits);
                if (state.Windows.Count != 0) { Charge(state, cost); return false; }
                var now = state.LastTimestamp;
                if (state.BootstrapAt is { } last && now - last < 30 * state.Frequency) Reject("RATE_METADATA_DISCOVERY_IN_PROGRESS");
                if (state.UnaccountedBootstrapStamps.Count >= 256) Reject("RATE_METADATA_DISCOVERY_BUDGET_UNCERTAIN");
                state.UnaccountedBootstrapStamps.Add(now);
                state.BootstrapAt = now;
                state.PendingObservation = true;
                return true;
            }, cancellationToken).ConfigureAwait(false);

            if (discover)
            {
                var discoveryUri = new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/api/v3/exchangeInfo").Uri;
                using var discovery = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
                using var response = await client.SendAsync(discovery, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                await ObserveAsync(scope, response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) Reject("RATE_METADATA_DISCOVERY_FAILED");
                var definitions = await ReadDefinitionsAsync(response, cancellationToken).ConfigureAwait(false);
                await ChangeAsync(scope, state =>
                {
                    CheckBlocked(state);
                    Install(state, definitions);
                    Observe(state, response);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                await ChangeAsync(scope, state => { CheckBlocked(state); Charge(state, cost); return true; }, cancellationToken).ConfigureAwait(false);
            }

            // A single dispatch: a timeout or 429 is never a reason to repeat POST.
            var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try { await ObserveAsync(scope, result).ConfigureAwait(false); }
            catch
            {
                result.Dispose();
                throw new HttpRequestException("RATE_BUDGET_OBSERVATION_FAILED_AFTER_DISPATCH");
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ObserveAsync(string scope, HttpResponseMessage response)
    {
        // Headers already received are safety evidence even when the caller has
        // canceled. Bound persistence independently rather than losing a ban.
        using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(2), timeProvider);
        try { await ChangeAsync(scope, state => { Observe(state, response); return true; }, persistence.Token).ConfigureAwait(false); }
        catch
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
            await BinanceQuotaJournal.PoisonAsync(directory, name).ConfigureAwait(false);
            throw;
        }
    }

    private void Observe(BudgetState state, HttpResponseMessage response)
    {
        var now = state.LastTimestamp;
        var headers = BinanceRateLimitHeaders.Parse(response.Headers, timeProvider.GetUtcNow());
        state.PendingObservation = false;
        foreach (var window in state.Windows)
            if (window.Header is { } header && headers.GetCounter(header) is { } value)
            {
                if (value < 0) { state.UnknownDeadline = true; continue; }
                window.ServerPeak = Math.Max(window.ServerPeak, value);
                window.ServerPeakAt = now;
            }
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)418)) return;
        var remaining = headers.RetryAfterUtc - timeProvider.GetUtcNow();
        if (remaining is not { Ticks: > 0 } delay) { state.UnknownDeadline = true; return; }
        var stamps = (decimal)delay.Ticks * state.Frequency / TimeSpan.TicksPerSecond;
        if (stamps > long.MaxValue - now) { state.UnknownDeadline = true; return; }
        state.BlockedUntil = Math.Max(state.BlockedUntil, now + (long)decimal.Ceiling(stamps));
    }

    private void CheckBlocked(BudgetState state)
    {
        if (state.UnknownDeadline) Reject("RATE_LIMIT_DEADLINE_UNKNOWN");
        if (state.PendingObservation) Reject("RATE_RESPONSE_OBSERVATION_PENDING");
        if (timeProvider.GetTimestamp() < state.BlockedUntil) Reject("RATE_LIMIT_COOLDOWN_ACTIVE");
    }

    private static void Charge(BudgetState state, RequestCost cost)
    {
        if (!state.Windows.Any(static window => window.Kind == "REQUEST_WEIGHT") ||
            cost.Orders > 0 && !state.Windows.Any(static window => window.Kind == "ORDERS"))
            Reject("RATE_LIMIT_METADATA_INCOMPLETE");
        var now = state.LastTimestamp;
        foreach (var window in state.Windows)
        {
            var amount = window.Kind switch { "REQUEST_WEIGHT" => cost.Weight, "RAW_REQUESTS" => 1, "ORDERS" => cost.Orders, _ => 0 };
            var used = window.Buckets.Sum(static bucket => bucket.Amount);
            if (used > window.Limit - window.ServerPeak || amount > window.Limit - window.ServerPeak - used)
                Reject("RATE_BUDGET_EXHAUSTED");
        }
        RecordCharge(state, cost, now);
        state.PendingObservation = true;
    }

    private void Install(BudgetState state, IReadOnlyList<BinanceRateLimitDefinition> definitions)
    {
        // A late bootstrap must never replace windows already used by peers.
        if (state.Windows.Count == 0) state.Windows = Windows(definitions);
        foreach (var stamp in state.UnaccountedBootstrapStamps) RecordCharge(state, new RequestCost(20, 0), stamp);
        state.UnaccountedBootstrapStamps.Clear();
    }

    private static void RecordCharge(BudgetState state, RequestCost cost, long now)
    {
        foreach (var window in state.Windows)
        {
            var amount = window.Kind switch { "REQUEST_WEIGHT" => cost.Weight, "RAW_REQUESTS" => 1, "ORDERS" => cost.Orders, _ => 0 };
            if (amount == 0) continue;
            var width = Width(window.Seconds, state.Frequency);
            if (state.LastTimestamp - now >= window.Seconds * state.Frequency + width) continue;
            var slot = now / width * width;
            var bucket = window.Buckets.FirstOrDefault(value => value.Stamp == slot);
            if (bucket is null) window.Buckets.Add(new BudgetBucket { Stamp = slot, Amount = amount });
            else bucket.Amount = checked(bucket.Amount + amount);
        }
    }

    private static long Width(long seconds, long frequency) => checked(seconds * frequency / 64 + 1);

    private async Task<T> ChangeAsync<T>(string scope, Func<BudgetState, T> change, CancellationToken token)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var journal = await BinanceQuotaJournal.OpenAsync(directory, name, timeProvider, token).ConfigureAwait(false);
        await using var lease = journal.ConfigureAwait(false);
        BudgetState state;
        try
        {
            var bytes = await journal.ReadAsync(MaximumJournalBytes, token).ConfigureAwait(false);
            state = bytes is null ? new BudgetState { Frequency = timeProvider.TimestampFrequency,
                BootIdentifier = BinanceQuotaJournal.BootIdentifier, ClockOrigin = clockOrigin,
                TimeNamespaceIdentifier = BinanceQuotaJournal.TimeNamespaceIdentifier }
                : JsonSerializer.Deserialize(bytes, BudgetJsonContext.Default.BudgetState) ?? throw new InvalidDataException();
            Validate(state);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new BinanceRequestBudgetRejectedException("RATE_BUDGET_JOURNAL_INVALID"); }
        var now = timeProvider.GetTimestamp();
        state.LastTimestamp = now;
        foreach (var window in state.Windows)
        {
            var lifetime = checked(window.Seconds * state.Frequency + Width(window.Seconds, state.Frequency));
            window.Buckets.RemoveAll(bucket => now - bucket.Stamp >= lifetime);
            if (now - window.ServerPeakAt >= lifetime) window.ServerPeak = 0;
        }
        var result = change(state);
        var output = JsonSerializer.SerializeToUtf8Bytes(state, BudgetJsonContext.Default.BudgetState);
        if (output.Length > MaximumJournalBytes) Reject("RATE_BUDGET_JOURNAL_TOO_LARGE");
        await journal.WriteAsync(output, token).ConfigureAwait(false);
        return result;
    }

    private void Validate(BudgetState state)
    {
        var now = timeProvider.GetTimestamp();
        if (state.Version != 4 || state.BootIdentifier != BinanceQuotaJournal.BootIdentifier || state.ClockOrigin != clockOrigin ||
            state.TimeNamespaceIdentifier != BinanceQuotaJournal.TimeNamespaceIdentifier ||
            state.Frequency != timeProvider.TimestampFrequency || state.LastTimestamp < 0 || now < state.LastTimestamp ||
            state.BlockedUntil < 0 || state.BootstrapAt is < 0 || state.BootstrapAt > state.LastTimestamp ||
            state.Windows is null || state.Windows.Count > 16 || state.UnaccountedBootstrapStamps is null ||
            state.UnaccountedBootstrapStamps.Count > 256 || state.UnaccountedBootstrapStamps.Any(stamp => stamp < 0 || stamp > state.LastTimestamp))
            Reject("RATE_BUDGET_STATE_OR_CLOCK_INVALID");
        HashSet<(string Kind, long Seconds)> kinds = [];
        foreach (var window in state.Windows)
        {
            if (window is null || window.Kind is not ("REQUEST_WEIGHT" or "RAW_REQUESTS" or "ORDERS") || window.Seconds is <= 0 or > 86_400 ||
                window.Seconds > long.MaxValue / state.Frequency / 2 || !kinds.Add((window.Kind, window.Seconds)) ||
                window.Limit <= 0 || window.ServerPeak < 0 || window.ServerPeakAt < 0 || window.ServerPeakAt > state.LastTimestamp ||
                !ValidHeader(window) || window.Buckets is null || window.Buckets.Count > 128) Reject("RATE_BUDGET_STATE_INVALID");
            var width = Width(window.Seconds, state.Frequency);
            long total = 0;
            HashSet<long> stamps = [];
            foreach (var bucket in window.Buckets)
            {
                if (bucket is null || bucket.Amount < 0 || bucket.Stamp < 0 || bucket.Stamp > state.LastTimestamp ||
                    bucket.Stamp % width != 0 || !stamps.Add(bucket.Stamp) || bucket.Amount > long.MaxValue - total)
                    Reject("RATE_BUDGET_BUCKET_INVALID");
                total += bucket.Amount;
            }
        }
    }

    private static bool ValidHeader(BudgetWindow window)
    {
        if (window.Kind == "RAW_REQUESTS") return window.Header is null;
        var prefix = window.Kind == "REQUEST_WEIGHT" ? "X-MBX-USED-WEIGHT-" : "X-MBX-ORDER-COUNT-";
        if (window.Header is not { } header || !header.StartsWith(prefix, StringComparison.Ordinal) || header.Length <= prefix.Length + 1)
            return false;
        var unit = header[^1] switch { 'S' => 1L, 'M' => 60L, 'H' => 3600L, 'D' => 86400L, _ => 0L };
        return unit > 0 && long.TryParse(header.AsSpan(prefix.Length, header.Length - prefix.Length - 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var count) && count > 0 && count <= 86_400 / unit && count * unit == window.Seconds;
    }

    [DoesNotReturn]
    private static void Reject(string code) => throw new BinanceRequestBudgetRejectedException(code);

    private List<BudgetWindow> Windows(IEnumerable<BinanceRateLimitDefinition> definitions)
    {
        List<BudgetWindow> windows = [];
        foreach (var definition in definitions)
        {
            var unit = definition.Interval switch { "SECOND" => 1L, "MINUTE" => 60L, "HOUR" => 3600L, "DAY" => 86400L, _ => 0L };
            var seconds = unit * definition.IntervalNum;
            if (definition.RateLimitType is not ("REQUEST_WEIGHT" or "RAW_REQUESTS" or "ORDERS") ||
                seconds is <= 0 or > 86400 || definition.Limit <= 0 || definition.IntervalNum <= 0 ||
                seconds > long.MaxValue / timeProvider.TimestampFrequency / 2 || windows.Count >= 16)
                Reject("RATE_LIMIT_DEFINITION_INVALID");
            var letter = definition.Interval switch { "SECOND" => "S", "MINUTE" => "M", "HOUR" => "H", _ => "D" };
            var suffix = definition.IntervalNum.ToString(CultureInfo.InvariantCulture) + letter;
            var header = definition.RateLimitType switch
            { "REQUEST_WEIGHT" => "X-MBX-USED-WEIGHT-" + suffix, "ORDERS" => "X-MBX-ORDER-COUNT-" + suffix, _ => null };
            if (windows.Any(window => window.Kind == definition.RateLimitType && window.Seconds == seconds)) Reject("RATE_LIMIT_DEFINITION_DUPLICATE");
            windows.Add(new BudgetWindow { Kind = definition.RateLimitType, Seconds = seconds, Limit = definition.Limit, Header = header });
        }
        if (!windows.Any(static window => window.Kind == "REQUEST_WEIGHT")) Reject("RATE_LIMIT_WEIGHT_METADATA_MISSING");
        return windows;
    }

    private static async Task<BinanceRateLimitDefinition[]> ReadDefinitionsAsync(HttpResponseMessage response, CancellationToken token)
    {
        var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var inputLifetime = input.ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > 8_388_608) Reject("RATE_METADATA_RESPONSE_TOO_LARGE");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        using var json = JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
        if (!json.RootElement.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            throw new BinanceRequestBudgetRejectedException("RATE_LIMIT_METADATA_MISSING");
        return limits.EnumerateArray().Select(item => new BinanceRateLimitDefinition(
            item.GetProperty("rateLimitType").GetString()!, item.GetProperty("interval").GetString()!,
            item.GetProperty("intervalNum").GetInt32(), item.GetProperty("limit").GetInt64())).ToArray();
    }

    private readonly record struct RequestCost(long Weight, long Orders);
    private static RequestCost Cost(HttpMethod method, string path) => (method.Method, path) switch
    {
        ("GET", "/api/v3/exchangeInfo") => new(20, 0), ("GET", "/api/v3/time" or "/api/v3/ping") => new(1, 0),
        ("GET", "/api/v3/klines" or "/api/v3/referencePrice") => new(2, 0),
        ("GET", "/api/v3/aggTrades" or "/api/v3/ticker/bookTicker" or "/api/v3/order") => new(4, 0),
        ("GET", "/api/v3/depth") => new(250, 0), ("GET", "/api/v3/openOrders") => new(80, 0),
        ("GET", "/api/v3/account" or "/api/v3/myTrades" or "/api/v3/account/commission") => new(20, 0),
        ("GET", "/api/v3/myFilters" or "/api/v3/rateLimit/order") => new(40, 0),
        ("POST", "/api/v3/order" or "/api/v3/order/cancelReplace") => new(1, 1),
        ("POST", "/api/v3/orderList/oco") => new(1, 2), ("POST", "/api/v3/orderList/otoco") => new(1, 3),
        ("PUT", "/api/v3/order/amend/keepPriority") => new(4, 0), ("DELETE", "/api/v3/order") => new(1, 0),
        _ => throw new BinanceRequestBudgetRejectedException("RATE_ENDPOINT_COST_UNVERIFIED")
    };

    private static string Scope(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)) Reject("RATE_ENDPOINT_AUTHORITY_UNVERIFIED");
        return uri.Host.ToUpperInvariant() switch
        {
            "API.BINANCE.COM" or "API1.BINANCE.COM" or "API2.BINANCE.COM" or "API3.BINANCE.COM" or "API4.BINANCE.COM" => "spot-live",
            "TESTNET.BINANCE.VISION" => "spot-testnet", "DEMO-API.BINANCE.COM" => "spot-demo",
            _ => throw new BinanceRequestBudgetRejectedException("RATE_ENDPOINT_HOST_UNVERIFIED")
        };
    }
}

internal sealed class BudgetState
{
    [JsonRequired] public int Version { get; set; } = 4;
    [JsonRequired] public string BootIdentifier { get; set; } = string.Empty;
    [JsonRequired] public string ClockOrigin { get; set; } = string.Empty;
    [JsonRequired] public string TimeNamespaceIdentifier { get; set; } = string.Empty;
    [JsonRequired] public long Frequency { get; set; }
    [JsonRequired] public long LastTimestamp { get; set; }
    [JsonRequired] public long BlockedUntil { get; set; }
    [JsonRequired] public bool UnknownDeadline { get; set; }
    [JsonRequired] public bool PendingObservation { get; set; }
    [JsonRequired] public long? BootstrapAt { get; set; }
    [JsonRequired] public List<long> UnaccountedBootstrapStamps { get; set; } = [];
    [JsonRequired] public List<BudgetWindow> Windows { get; set; } = [];
}
internal sealed class BudgetWindow
{
    [JsonRequired] public string Kind { get; set; } = string.Empty;
    [JsonRequired] public long Seconds { get; set; }
    [JsonRequired] public long Limit { get; set; }
    [JsonRequired] public string? Header { get; set; }
    [JsonRequired] public long ServerPeak { get; set; }
    [JsonRequired] public long ServerPeakAt { get; set; }
    [JsonRequired] public List<BudgetBucket> Buckets { get; set; } = [];
}
internal sealed class BudgetBucket
{
    [JsonRequired] public long Stamp { get; set; }
    [JsonRequired] public long Amount { get; set; }
}
[JsonSerializable(typeof(BudgetState))]
internal sealed partial class BudgetJsonContext : JsonSerializerContext;
