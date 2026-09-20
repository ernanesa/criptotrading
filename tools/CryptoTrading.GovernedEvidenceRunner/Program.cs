#pragma warning disable CA2007 // The tool has no synchronization context; all network I/O awaits use ConfigureAwait below.

using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Persistence;

var arguments = Arguments.Parse(args);
if (Environment.GetEnvironmentVariable("BINANCE_API_KEY") is { Length: > 0 } ||
    Environment.GetEnvironmentVariable("BINANCE_API_SECRET") is { Length: > 0 })
{
    await Console.Error.WriteLineAsync("Refusing governed evidence run while Binance credentials are present.").ConfigureAwait(false);
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync("POSTGRES_CONNECTION_STRING is required and must remain outside command arguments.").ConfigureAwait(false);
    return 2;
}

if (!Guid.TryParse(arguments.ExperimentId, out var experimentGuid))
{
    throw new ArgumentException("--experiment-id must be a GUID.");
}

using var recorder = new MarketDataRecorder(arguments.InputDirectory);
var replay = await recorder.ReadReplayAsync().ConfigureAwait(false);
await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
await store.CheckConnectivityAsync().ConfigureAwait(false);
var snapshot = await store.ReadExperimentGovernanceSnapshotAsync(new ExperimentId(experimentGuid)).ConfigureAwait(false)
    ?? throw new InvalidDataException("Experiment governance snapshot was not found.");
var preregistration = snapshot.Preregistration;
var loaded = PublicKlineDatasetLoader.Load(
    replay,
    arguments.DatasetId,
    arguments.DatasetVersion,
    arguments.DatasetSource,
    arguments.Symbol,
    DateTimeOffset.UtcNow);
var bars = loaded.Records.Select(record => record.Bar).ToArray();
if (bars.Length < 80)
{
    throw new InvalidDataException("At least 80 quality-gated bars are required for the governed OOS run.");
}

var evaluation = WalkForwardEvaluator.Evaluate(
    loaded.Metadata,
    bars,
    TrendFollowingParameters.BtcUsdtBaseline,
    BacktestEvaluationOptions.DailyCrypto(),
    WalkForwardPlan.Create(bars.Length, trainingBars: 60, outOfSampleBars: 20, stepBars: 20));
var input = ExperimentEvidenceWriter.FromGovernanceState(
    preregistration,
    snapshot.Trials.Count,
    snapshot.HoldoutAccessCount,
    snapshot.HoldoutContaminated,
    evaluation,
    arguments.Decision,
    new Dictionary<string, string>
    {
        ["dataset"] = loaded.Metadata.ContentHash,
        ["dataset_version"] = loaded.Metadata.Version,
        ["code_commit"] = preregistration.CodeCommit,
        ["dependency_lock"] = preregistration.DependencyLockHash
    },
    arguments.TraceabilityReferences);
var bundle = ExperimentEvidenceWriter.Write(arguments.OutputDirectory, input, DateTimeOffset.UtcNow);
await Console.Out.WriteLineAsync($"Governed evidence bundle written: {bundle.ArtifactPath}").ConfigureAwait(false);
await Console.Out.WriteLineAsync($"Manifest: {bundle.ManifestPath}").ConfigureAwait(false);
return 0;

internal sealed record Arguments(
    string ExperimentId,
    string InputDirectory,
    string OutputDirectory,
    string DatasetId,
    string DatasetVersion,
    string DatasetSource,
    string Symbol,
    string Decision,
    IReadOnlyCollection<string> TraceabilityReferences)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException("Arguments require --name value pairs.");
            }

            var name = args[index][2..];
            var value = args[++index];
            if (string.Equals(name, "trace", StringComparison.OrdinalIgnoreCase))
            {
                references.Add(value);
            }
            else
            {
                values[name] = value;
            }
        }

        if (references.Count == 0)
        {
            throw new ArgumentException("At least one --trace reference is required.", nameof(args));
        }

        return new(
            Required(values, "experiment-id"),
            Path.GetFullPath(Required(values, "input")),
            Path.GetFullPath(Required(values, "output")),
            Required(values, "dataset-id"),
            Required(values, "dataset-version"),
            Required(values, "dataset-source"),
            Required(values, "symbol").ToUpperInvariant(),
            Required(values, "decision"),
            references);
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"Missing --{name}.", nameof(values));
}
