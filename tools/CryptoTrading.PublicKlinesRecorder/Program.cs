using System.Globalization;
using System.Text.Json;
using CryptoTrading.Backtesting;
using CryptoTrading.Exchange.Binance;

var arguments = Arguments.Parse(args);
if (Environment.GetEnvironmentVariable("BINANCE_API_KEY") is { Length: > 0 } ||
    Environment.GetEnvironmentVariable("BINANCE_API_SECRET") is { Length: > 0 })
{
    await Console.Error.WriteLineAsync("Refusing public dataset collection while Binance credentials are present.").ConfigureAwait(false);
    return 2;
}

using var httpClient = new HttpClient
{
    BaseAddress = arguments.Source switch
    {
        "TESTNET" => new Uri("https://testnet.binance.vision/api/", UriKind.Absolute),
        "PRODUCTION-DATA" => new Uri("https://data-api.binance.vision/api/", UriKind.Absolute),
        _ => throw new InvalidOperationException("Unsupported public data source.")
    },
    Timeout = TimeSpan.FromSeconds(15)
};
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoTrading.PublicKlinesRecorder/1.0");
var marketClient = new BinanceSpotMarketClient(httpClient);
if (Directory.Exists(arguments.OutputDirectory) && Directory.EnumerateFileSystemEntries(arguments.OutputDirectory).Any())
{
    await Console.Error.WriteLineAsync("Refusing to write into a non-empty dataset directory.").ConfigureAwait(false);
    return 2;
}

var recorder = new MarketDataRecorder(arguments.OutputDirectory);

var nowUtc = DateTimeOffset.UtcNow;
var nextStart = arguments.StartUtc;
var recorded = 0;
var pages = 0;
while (recorded < arguments.MaxBars)
{
    var requested = Math.Min(1000, arguments.MaxBars - recorded);
    var batch = await marketClient.GetKlinesAsync(
        arguments.Symbol,
        arguments.Interval,
        nextStart,
        arguments.EndUtc,
        requested).ConfigureAwait(false);
    pages++;
    if (batch.Count == 0)
    {
        break;
    }

    var newestOpen = nextStart;
    foreach (var kline in batch)
    {
        newestOpen = kline.OpenTimeUtc.AddMilliseconds(1);
        if (kline.CloseTimeUtc > nowUtc)
        {
            continue;
        }

        await recorder.AppendAsync(
            kline.CloseTimeUtc,
            nowUtc,
            "kline-rest",
            $"rest:klines:{arguments.Interval}",
            arguments.Symbol,
            kline).ConfigureAwait(false);
        recorded++;
        if (recorded >= arguments.MaxBars)
        {
            break;
        }
    }

    if (batch.Count < requested || nextStart is null || newestOpen <= nextStart.Value || arguments.EndUtc is { } end && newestOpen > end)
    {
        break;
    }

    nextStart = newestOpen;
}

recorder.Dispose();
using var verifier = new MarketDataRecorder(arguments.OutputDirectory);
var replay = await verifier.ReadReplayAsync(arguments.StartUtc, arguments.EndUtc).ConfigureAwait(false);
var manifestPath = Path.Combine(arguments.OutputDirectory, "dataset-manifest.json");
var manifest = new
{
    schema_version = "public-klines-dataset/v1",
    source = arguments.Source == "TESTNET"
        ? "Binance Spot Testnet public REST /api/v3/klines"
        : "Binance Spot public market-data REST /api/v3/klines",
    source_id = arguments.Source,
    symbol = arguments.Symbol,
    interval = arguments.Interval,
    requested_start_utc = arguments.StartUtc,
    requested_end_utc = arguments.EndUtc,
    recorded_bars = recorded,
    replay_records = replay.Records.Count,
    pages,
    replay_fingerprint = replay.DatasetChecksum,
    generated_at_utc = nowUtc
};
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
await Console.Out.WriteLineAsync($"Recorded {replay.Records.Count} public klines. Manifest: {manifestPath}").ConfigureAwait(false);
return 0;

internal sealed record Arguments(
    string Symbol,
    string Interval,
    string OutputDirectory,
    string Source,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    int MaxBars)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException("Arguments require --name value pairs.");
            }

            values[args[index][2..]] = args[++index];
        }

        var symbol = Required(values, "symbol").ToUpperInvariant();
        var interval = Required(values, "interval");
        var output = Path.GetFullPath(Required(values, "output"));
        var source = values.TryGetValue("source", out var sourceValue) && !string.IsNullOrWhiteSpace(sourceValue)
            ? sourceValue.Trim().ToUpperInvariant()
            : "TESTNET";
        if (source is not ("TESTNET" or "PRODUCTION-DATA"))
        {
            throw new ArgumentException("--source must be 'testnet' or 'production-data'.", nameof(args));
        }
        var start = ParseUtc(values, "start");
        var end = ParseUtc(values, "end");
        if (start is { } from && end is { } through && from > through)
        {
            throw new ArgumentException("--start cannot exceed --end.");
        }

        var maxBars = values.TryGetValue("max-bars", out var maxText) && int.TryParse(maxText, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1000;
        if (maxBars is < 1 or > 100_000)
        {
            throw new ArgumentException("--max-bars must be between 1 and 100000.", nameof(args));
        }

        return new(symbol, interval, output, source, start, end, maxBars);
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"Missing --{name}.");

    private static DateTimeOffset? ParseUtc(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) || parsed.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"--{name} must be an ISO-8601 UTC timestamp.");
        }

        return parsed;
    }
}
