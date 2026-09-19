using System.Text.Json;
using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;

var arguments = Arguments.Parse(args);
if (Environment.GetEnvironmentVariable("BINANCE_API_KEY") is { Length: > 0 } ||
    Environment.GetEnvironmentVariable("BINANCE_API_SECRET") is { Length: > 0 })
{
    await Console.Error.WriteLineAsync("Refusing OOS run while Binance credentials are present.").ConfigureAwait(false);
    return 2;
}

using var recorder = new MarketDataRecorder(arguments.InputDirectory);
var replay = await recorder.ReadReplayAsync().ConfigureAwait(false);
var loaded = PublicKlineDatasetLoader.Load(
    replay,
    arguments.DatasetId,
    arguments.Version,
    arguments.Source,
    arguments.Symbol,
    DateTimeOffset.UtcNow);
var bars = loaded.Records.Select(record => record.Bar).ToArray();
if (bars.Length < 80)
{
    throw new InvalidDataException("At least 80 quality-gated bars are required for the baseline walk-forward run.");
}

var windows = WalkForwardPlan.Create(bars.Length, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);
var evaluation = WalkForwardEvaluator.Evaluate(
    loaded.Metadata,
    bars,
    TrendFollowingParameters.BtcUsdtBaseline,
    BacktestEvaluationOptions.DailyCrypto(),
    windows);
var summary = evaluation.GetBenchmarkSummary();
var report = new
{
    schema_version = "public-oos-report/v1",
    promotion_authorized = false,
    dataset = loaded.Metadata,
    quality = loaded.Quality,
    strategy = new
    {
        id = TrendFollowingParameters.BtcUsdtBaseline.StrategyId,
        version = TrendFollowingParameters.BtcUsdtBaseline.Version,
        configuration_fingerprint = TrendFollowingParameters.BtcUsdtBaseline.ConfigurationFingerprint
    },
    windows = evaluation.Windows.Select(window => new
    {
        window.Window,
        window.NetReturn,
        window.MaximumDrawdown,
        benchmark = window.Benchmark
    }),
    aggregate_benchmark_summary = summary,
    generated_at_utc = DateTimeOffset.UtcNow
};
if (File.Exists(arguments.OutputPath))
{
    throw new IOException("Refusing to overwrite an existing OOS report.");
}

Directory.CreateDirectory(Path.GetDirectoryName(arguments.OutputPath) ?? ".");
await File.WriteAllTextAsync(arguments.OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })).ConfigureAwait(false);
await Console.Out.WriteLineAsync($"OOS report written: {arguments.OutputPath}").ConfigureAwait(false);
return 0;

internal sealed record Arguments(
    string InputDirectory,
    string OutputPath,
    string DatasetId,
    string Version,
    string Source,
    string Symbol)
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

        return new(
            Path.GetFullPath(Required(values, "input")),
            Path.GetFullPath(Required(values, "output")),
            Required(values, "dataset-id"),
            Required(values, "version"),
            Required(values, "source"),
            Required(values, "symbol").ToUpperInvariant());
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"Missing --{name}.", nameof(values));
}
