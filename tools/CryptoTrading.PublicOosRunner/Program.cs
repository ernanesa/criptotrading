using System.Globalization;
using System.Text.Json;
using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Autonomy;
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
const decimal researchQuoteCapital = 10_000m;
var baselineOptions = new StrategyBacktestOptions(researchQuoteCapital, 365, feeRate: 0.001m, halfSpreadRate: 0m, slippageRate: 0.0005m);
var stressOptions = new StrategyBacktestOptions(researchQuoteCapital, 365, feeRate: 0.001m, halfSpreadRate: 0.0005m, slippageRate: 0.0005m);
var baselineComparison = StrategyComparisonEvaluator.Evaluate(
    loaded.Metadata,
    bars,
    baselineOptions,
    windows);
var spreadStressComparison = StrategyComparisonEvaluator.Evaluate(
    loaded.Metadata,
    bars,
    stressOptions,
    windows);
var sensitivityReport = arguments.IncludeSensitivity
    ? new
    {
        baseline_no_observed_spread = ToCompactSensitivityReport(StrategyComparisonEvaluator.EvaluateSensitivity(
            loaded.Metadata, bars, baselineOptions, windows)),
        stress_half_spread_5bps = ToCompactSensitivityReport(StrategyComparisonEvaluator.EvaluateSensitivity(
            loaded.Metadata, bars, stressOptions, windows)),
    }
    : null;
var trendComparison = baselineComparison.Strategies.Single(static result =>
    result.StrategyId.StartsWith("trend-following-", StringComparison.Ordinal));
var microCapScenario = BuildMicroCapScenario(arguments.MicroCapital, loaded.Metadata, bars, windows, arguments.IncludeSensitivity);
var mlCalibration = MlNetSystem1DecisionEngine.EvaluateWalkForwardCalibration(bars, windows);
var report = new
{
    schema_version = "public-oos-report/v3",
    promotion_authorized = false,
    dataset = loaded.Metadata,
    quality = loaded.Quality,
    strategy = new
    {
        id = trendComparison.StrategyId,
        version = trendComparison.StrategyVersion,
        configuration_fingerprint = trendComparison.StrategyConfigurationFingerprint
    },
    windows = trendComparison.RollingWindows.Select(window => new
    {
        window.Window,
        window.NetReturn,
        window.MaximumDrawdown,
        benchmark = window.Benchmark
    }),
    aggregate_benchmark_summary = trendComparison.AggregateBenchmarks,
    strategy_comparison = new
    {
        schema_version = "strategy-comparison/v2",
        promotion_authorized = false,
        initial_quote_units = researchQuoteCapital,
        quote_asset = "USDT",
        capital_note = "Normalization only; this is not BRL and not the R$100 micro-live budget.",
        methodology = baselineComparison.Methodology,
        execution_assumption = baselineComparison.ExecutionAssumption,
        position_sizing_assumption = baselineComparison.PositionSizingAssumption,
        capital_allocation_assumption = baselineComparison.CapitalAllocationAssumption,
        baseline_no_observed_spread = baselineComparison.Strategies.Select(ToCompactStrategyResult),
        stress_half_spread_5bps = spreadStressComparison.Strategies.Select(ToCompactStrategyResult),
        sensitivity_study = sensitivityReport,
        spread_note = "Historical dataset has closes only; baseline sets additional spread to zero and stress adds 5 bps half-spread. Neither is observed spread.",
    },
    ml_system1_calibration = new
    {
        schema_version = "ml-system1-calibration/v1",
        brier_score = mlCalibration.BrierScore,
        expected_calibration_error = mlCalibration.ExpectedCalibrationError,
        brier_threshold_met = mlCalibration.BrierScoreThresholdMet,
        target_brier_threshold = mlCalibration.TargetBrierThreshold,
        total_evaluations = mlCalibration.TotalEvaluations,
        positive_outcomes = mlCalibration.PositiveOutcomes,
        empirical_win_rate = mlCalibration.TotalEvaluations > 0 ? Math.Round((decimal)mlCalibration.PositiveOutcomes / mlCalibration.TotalEvaluations, 4) : 0m,
        reliability_bins = mlCalibration.ReliabilityBins.Select(bin => new
        {
            bin_index = bin.BinIndex,
            range = $"{bin.LowerBound:P0} - {bin.UpperBound:P0}",
            sample_count = bin.SampleCount,
            mean_confidence = bin.MeanConfidence,
            empirical_win_rate = bin.EmpiricalWinRate,
            calibration_error = bin.AbsoluteError,
        }),
        evaluated_at_utc = mlCalibration.EvaluatedAtUtc,
    },
    micro_cap_scenario = microCapScenario,
    generated_at_utc = DateTimeOffset.UtcNow
};
if (File.Exists(arguments.OutputPath))
{
    throw new IOException("Refusing to overwrite an existing OOS report.");
}

if (arguments.MicroCapital is { } finalMicroCapital)
{
    Arguments.ValidateMicroSnapshotFreshness(finalMicroCapital, DateTimeOffset.UtcNow);
}

Directory.CreateDirectory(Path.GetDirectoryName(arguments.OutputPath) ?? ".");
await File.WriteAllTextAsync(arguments.OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })).ConfigureAwait(false);
await Console.Out.WriteLineAsync($"OOS report written: {arguments.OutputPath}").ConfigureAwait(false);
return 0;

static object ToCompactStrategyResult(StrategyComparisonStrategyResult result) => new
{
    strategy_id = result.StrategyId,
    strategy_version = result.StrategyVersion,
    strategy_configuration_fingerprint = result.StrategyConfigurationFingerprint,
    dataset_content_hash = result.DatasetContentHash,
    full_period_evaluation_fingerprint = result.FullPeriod.EvaluationFingerprint,
    evaluation_options = result.FullPeriod.Options,
    full_period = result.FullPeriod.Metrics,
    state_sync_warning = result.FullPeriod.Metrics.HasSignalStateDivergence
        ? "At least one order signal was unfilled/rejected or partially filled; strategy internal position was not fed actual fills, so this result is not suitable for promotion."
        : null,
    capacity_eligibility = result.CapacityEligibility is null ? null : new
    {
        is_eligible = result.CapacityEligibility.IsEligible,
        exclusion_reason = result.CapacityEligibility.ExclusionReason,
        total_budget_quote = result.CapacityEligibility.TotalBudgetQuote,
        per_order_quote_notional = result.CapacityEligibility.PerOrderQuoteNotional,
        minimum_order_notional_quote = result.CapacityEligibility.MinimumOrderNotionalQuote,
    },
    rolling_windows = result.RollingWindows.Select(static window => new
    {
        window = window.Window.WindowNumber,
        window.StartTimeUtc,
        window.EndTimeUtc,
        window.StartingEquity,
        window.FinalEquity,
        window.NetReturn,
        window.MaximumDrawdown,
        window.TotalFees,
        window.TotalSpreadCost,
        window.TotalSlippageCost,
        window.TradeCount,
        window.PartialFillCount,
        window.PeriodCount,
        benchmark = new
        {
            strategy = window.Benchmark.Strategy.NetReturn,
            no_trade_cash = window.Benchmark.NoTradeCash.NetReturn,
            buy_and_hold = window.Benchmark.BuyAndHold.NetReturn,
            buy_and_hold_maximum_drawdown = window.Benchmark.BuyAndHold.MaximumDrawdown,
        },
        has_partial_fixed_quantity_fills = window.PartialFillCount > 0,
    }),
    market_regime_attribution = result.MarketRegimeAttribution,
    aggregate_benchmarks = result.AggregateBenchmarks,
};

static object ToCompactSensitivityReport(StrategySensitivityReport result) => new
{
    dataset_content_hash = result.DatasetContentHash,
    symbol = result.Symbol,
    methodology = result.Methodology,
    capital_allocation_assumption = result.CapitalAllocationAssumption,
    winner_selected = result.WinnerSelected,
    promotion_authorized = result.PromotionAuthorized,
    variants = result.Variants.Select(ToCompactStrategyResult),
};

static object? BuildMicroCapScenario(
    MicroCapitalInputs? inputs,
    DatasetMetadata dataset,
    IReadOnlyList<CloseBar> bars,
    IReadOnlyList<WalkForwardWindow> windows,
    bool includeSensitivity)
{
    if (inputs is null)
    {
        return null;
    }

    var quoteCapital = inputs.CapitalBrl / inputs.UsdtBrlPrice;
    var baselineOptions = new StrategyBacktestOptions(
        quoteCapital,
        365,
        feeRate: 0.001m,
        halfSpreadRate: 0m,
        slippageRate: 0.0005m,
        minimumOrderNotionalQuote: inputs.MinimumOrderNotionalQuote,
        maximumPositionNotionalAtBuyQuote: quoteCapital);
    var stressOptions = new StrategyBacktestOptions(
        quoteCapital,
        365,
        feeRate: 0.001m,
        halfSpreadRate: 0.0005m,
        slippageRate: 0.0005m,
        minimumOrderNotionalQuote: inputs.MinimumOrderNotionalQuote,
        maximumPositionNotionalAtBuyQuote: quoteCapital);
    var baseline = StrategyComparisonEvaluator.Evaluate(
        dataset,
        bars,
        baselineOptions,
        windows);
    var spreadStress = StrategyComparisonEvaluator.Evaluate(
        dataset,
        bars,
        stressOptions,
        windows);
    var sensitivity = includeSensitivity
        ? new
        {
            baseline_no_observed_spread = ToCompactSensitivityReport(StrategyComparisonEvaluator.EvaluateSensitivity(dataset, bars, baselineOptions, windows)),
            stress_half_spread_5bps = ToCompactSensitivityReport(StrategyComparisonEvaluator.EvaluateSensitivity(dataset, bars, stressOptions, windows)),
        }
        : null;

    return new
    {
        simulation_only = true,
        live_orders_authorized = false,
        user_capital_brl = inputs.CapitalBrl,
        usdtbrl_reference_price = inputs.UsdtBrlPrice,
        quote_capital_usdt = quoteCapital,
        maximum_position_notional_at_buy_quote = quoteCapital,
        minimum_order_notional_quote = inputs.MinimumOrderNotionalQuote,
        capital_allocation_assumption = baseline.CapitalAllocationAssumption,
        fx_snapshot_utc = inputs.FxSnapshotUtc,
        exchange_rules_snapshot_utc = inputs.ExchangeRulesSnapshotUtc,
        snapshot_sources_verified_by_runner = false,
        fx_source = "Operator-supplied public snapshot metadata; this runner does not fetch or verify its source.",
        filters_source = "Operator-supplied public symbol-filter metadata; this runner does not fetch or verify its source.",
        baseline_no_observed_spread = baseline.Strategies.Select(ToCompactStrategyResult),
        stress_half_spread_5bps = spreadStress.Strategies.Select(ToCompactStrategyResult),
        sensitivity_study = sensitivity,
        caveat = "Public indicative FX and exchange filters only; excludes account fee tier, deposit/conversion fees, observed live spread and actual fills. Position-notional-at-buy is simulation-only; market appreciation may raise marked exposure and this does not configure the live risk engine.",
    };
}

internal sealed record Arguments(
    string InputDirectory,
    string OutputPath,
    string DatasetId,
    string Version,
    string Source,
    string Symbol,
    MicroCapitalInputs? MicroCapital,
    bool IncludeSensitivity)
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

        var microCapital = ParseMicroCapital(values);
        var includeSensitivity = false;
        if (values.TryGetValue("include-sensitivity", out var sensitivityValue) &&
            !bool.TryParse(sensitivityValue, out includeSensitivity))
        {
            throw new ArgumentException("--include-sensitivity must be true or false.");
        }

        return new(
            Path.GetFullPath(Required(values, "input")),
            Path.GetFullPath(Required(values, "output")),
            Required(values, "dataset-id"),
            Required(values, "version"),
            Required(values, "source"),
            Required(values, "symbol").ToUpperInvariant(),
            microCapital,
            includeSensitivity);
    }

    private static MicroCapitalInputs? ParseMicroCapital(Dictionary<string, string> values)
    {
        var names = new[]
        {
            "micro-cap-brl", "usdtbrl-price", "minimum-order-notional-quote",
            "fx-snapshot-utc", "exchange-rules-snapshot-utc",
        };
        var suppliedCount = names.Count(values.ContainsKey);
        if (suppliedCount == 0)
        {
            return null;
        }

        if (suppliedCount != names.Length)
        {
            throw new ArgumentException("Micro-cap evaluation requires capital, FX, minimum-notional and both snapshot timestamps.");
        }

        var capitalBrl = ParsePositiveDecimal(values["micro-cap-brl"], "micro-cap-brl");
        if (capitalBrl > 100m)
        {
            throw new ArgumentException("--micro-cap-brl must not exceed the R$100 campaign cap.", nameof(values));
        }

        var usdtBrlPrice = ParsePositiveDecimal(values["usdtbrl-price"], "usdtbrl-price");
        var minimumOrderNotional = ParsePositiveDecimal(values["minimum-order-notional-quote"], "minimum-order-notional-quote");
        var fxSnapshotUtc = ParseUtc(values["fx-snapshot-utc"], "fx-snapshot-utc");
        var rulesSnapshotUtc = ParseUtc(values["exchange-rules-snapshot-utc"], "exchange-rules-snapshot-utc");
        var result = new MicroCapitalInputs(capitalBrl, usdtBrlPrice, minimumOrderNotional, fxSnapshotUtc, rulesSnapshotUtc);
        ValidateMicroSnapshotFreshness(result, DateTimeOffset.UtcNow);
        return result;
    }

    internal static void ValidateMicroSnapshotFreshness(MicroCapitalInputs inputs, DateTimeOffset nowUtc)
    {
        ValidateSnapshotFreshness(inputs.FxSnapshotUtc, nowUtc, "fx-snapshot-utc");
        ValidateSnapshotFreshness(inputs.ExchangeRulesSnapshotUtc, nowUtc, "exchange-rules-snapshot-utc");
        if ((inputs.FxSnapshotUtc - inputs.ExchangeRulesSnapshotUtc).Duration() > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentException("FX and exchange-rule snapshots must be within five minutes of one another.");
        }
    }

    private static void ValidateSnapshotFreshness(DateTimeOffset snapshotUtc, DateTimeOffset nowUtc, string name)
    {
        if (snapshotUtc > nowUtc.AddMinutes(2))
        {
            throw new ArgumentException($"--{name} cannot be more than two minutes in the future.");
        }

        if (nowUtc - snapshotUtc > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentException($"--{name} snapshot is older than the 15-minute research freshness window.");
        }
    }

    private static DateTimeOffset ParseUtc(string value, string name)
    {
        var hasExplicitUtcSuffix = value.EndsWith('Z', StringComparison.Ordinal) ||
            value.EndsWith('z', StringComparison.Ordinal) ||
            value.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcSuffix)
        {
            throw new ArgumentException($"--{name} must include an explicit Z or +00:00 suffix.");
        }

        if (!DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var snapshotUtc) || snapshotUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"--{name} must be an ISO-8601 UTC timestamp.");
        }

        return snapshotUtc;
    }

    private static decimal ParsePositiveDecimal(string value, string name)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0m)
        {
            throw new ArgumentException($"--{name} must be a positive invariant-culture decimal.");
        }

        return parsed;
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"Missing --{name}.", nameof(values));
}

internal sealed record MicroCapitalInputs(
    decimal CapitalBrl,
    decimal UsdtBrlPrice,
    decimal MinimumOrderNotionalQuote,
    DateTimeOffset FxSnapshotUtc,
    DateTimeOffset ExchangeRulesSnapshotUtc);
