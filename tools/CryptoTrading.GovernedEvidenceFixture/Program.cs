#pragma warning disable CA2007 // Top-level fixture has no synchronization context.

using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Research;
using CryptoTrading.Persistence;

var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is required.");
}

var arguments = Arguments.Parse(args);
using var recorder = new MarketDataRecorder(arguments.InputDirectory);
var replay = await recorder.ReadReplayAsync().ConfigureAwait(false);
var loaded = PublicKlineDatasetLoader.Load(
    replay,
    arguments.DatasetId,
    arguments.DatasetVersion,
    arguments.DatasetSource,
    arguments.Symbol,
    DateTimeOffset.UtcNow);
var parameters = TrendFollowingParameters.BtcUsdtBaseline;
var registration = new ExperimentPreregistration(
    ExperimentId.New(),
    "cli-governed-evidence-fixture",
    "baseline is evaluated after declared costs",
    "baseline-only",
    ["baseline"],
    1,
    "net_return",
    "reject if net return is not positive",
    "NO_TRADE/cash and buy-and-hold",
    new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
    "integration-fixture",
    IdempotencyKey.New(),
    parameters.Version,
    "fixture",
    loaded.Metadata.Version,
    loaded.Metadata.ContentHash,
    "holdout-v1",
    "metrics-v1",
    "local-fixture",
    Hash("fixture-dependencies"),
    42,
    loaded.Records[0].Bar.CloseTimeUtc,
    loaded.Records[^1].Bar.CloseTimeUtc.AddDays(1));
await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
await store.ApplyMigrationsAsync().ConfigureAwait(false);
await store.AppendExperimentPreregistrationAsync(registration).ConfigureAwait(false);
var trial = new ExperimentTrial(
    1,
    "baseline",
    "baseline-v1",
    Hash("fixture-trial"),
    ExperimentDecision.Rejected,
    registration.PreregisteredAtUtc,
    "integration-fixture",
    IdempotencyKey.New());
await store.AppendExperimentTrialAsync(registration.ExperimentId, trial).ConfigureAwait(false);
await Console.Out.WriteLineAsync(registration.ExperimentId.Value.ToString("D")).ConfigureAwait(false);
return 0;

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

internal sealed record Arguments(string InputDirectory, string DatasetId, string DatasetVersion, string DatasetSource, string Symbol)
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
            Required(values, "dataset-id"),
            Required(values, "dataset-version"),
            Required(values, "dataset-source"),
            Required(values, "symbol").ToUpperInvariant());
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"Missing --{name}.", nameof(values));
}
