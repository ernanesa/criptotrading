namespace CryptoTrading.Backtesting;

/// <summary>
/// Bounded, deterministic bootstrap stress configuration. This is a robustness tool,
/// not a return forecast or a guarantee of future performance.
/// </summary>
public sealed record MonteCarloStressOptions
{
    public MonteCarloStressOptions(
        int simulationCount,
        int horizonPeriods,
        int seed,
        decimal initialEquity,
        decimal negativeReturnMultiplier = 1m,
        decimal adverseReturnShock = 0m,
        string scenarioId = "bootstrap")
    {
        if (simulationCount is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(simulationCount), "Simulation count must be between 1 and 100,000.");
        }

        if (horizonPeriods is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonPeriods), "Horizon must be between 1 and 10,000 periods.");
        }

        if ((long)simulationCount * horizonPeriods > 5_000_000)
        {
            throw new ArgumentException("The simulation work budget cannot exceed 5,000,000 return samples.");
        }

        if (initialEquity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialEquity), "Initial equity must be greater than zero.");
        }

        if (negativeReturnMultiplier < 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(negativeReturnMultiplier), "Negative-return multiplier must be at least one.");
        }

        if (adverseReturnShock < 0m || adverseReturnShock >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(adverseReturnShock), "Adverse return shock must be in [0, 1).");
        }

        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            throw new ArgumentException("Scenario identifier is required.", nameof(scenarioId));
        }

        SimulationCount = simulationCount;
        HorizonPeriods = horizonPeriods;
        Seed = seed;
        InitialEquity = initialEquity;
        NegativeReturnMultiplier = negativeReturnMultiplier;
        AdverseReturnShock = adverseReturnShock;
        ScenarioId = scenarioId.Trim();
    }

    public int SimulationCount { get; }

    public int HorizonPeriods { get; }

    public int Seed { get; }

    public decimal InitialEquity { get; }

    /// <summary>Multiplier applied only to sampled negative returns.</summary>
    public decimal NegativeReturnMultiplier { get; }

    /// <summary>Absolute return subtracted from every sampled period.</summary>
    public decimal AdverseReturnShock { get; }

    public string ScenarioId { get; }
}

/// <summary>Distribution summary for one bounded Monte Carlo/stress run.</summary>
public sealed record MonteCarloStressResult(
    string ScenarioId,
    int Seed,
    int SimulationCount,
    int HorizonPeriods,
    int ObservedPeriodCount,
    decimal InitialEquity,
    decimal MeanFinalEquity,
    decimal MinimumFinalEquity,
    decimal MaximumFinalEquity,
    decimal FinalEquityP05,
    decimal FinalEquityP50,
    decimal FinalEquityP95,
    decimal MeanMaximumDrawdown,
    decimal MaximumDrawdownP50,
    decimal MaximumDrawdownP95,
    decimal ProbabilityOfLoss,
    decimal ProbabilityOfRuin,
    decimal WorstLossFraction);

/// <summary>
/// Resamples already observed period returns with replacement and applies explicit adverse
/// shocks. It performs no parameter fitting, exchange access, order submission, or prediction.
/// </summary>
public static class MonteCarloStressEvaluator
{
    public static MonteCarloStressResult Evaluate(
        IReadOnlyList<decimal> observedPeriodReturns,
        MonteCarloStressOptions options)
    {
        ArgumentNullException.ThrowIfNull(observedPeriodReturns);
        ArgumentNullException.ThrowIfNull(options);
        if (observedPeriodReturns.Count == 0)
        {
            throw new ArgumentException("At least one observed period return is required.", nameof(observedPeriodReturns));
        }

        var finalEquities = new decimal[options.SimulationCount];
        var maximumDrawdowns = new decimal[options.SimulationCount];
        var random = new DeterministicRandom(options.Seed);
        var lossCount = 0;
        var ruinCount = 0;
        var totalFinalEquity = 0m;
        var totalMaximumDrawdown = 0m;
        var worstLossFraction = 0m;

        for (var simulation = 0; simulation < options.SimulationCount; simulation++)
        {
            var equity = options.InitialEquity;
            var peak = equity;
            var maximumDrawdown = 0m;

            for (var period = 0; period < options.HorizonPeriods; period++)
            {
                var sampledReturn = observedPeriodReturns[random.Next(observedPeriodReturns.Count)];
                sampledReturn = ApplyStress(sampledReturn, options);
                equity = equity == 0m ? 0m : equity * Math.Max(0m, 1m + sampledReturn);
                peak = Math.Max(peak, equity);
                var drawdown = peak == 0m ? 0m : 1m - equity / peak;
                maximumDrawdown = Math.Max(maximumDrawdown, drawdown);
            }

            finalEquities[simulation] = equity;
            maximumDrawdowns[simulation] = maximumDrawdown;
            totalFinalEquity += equity;
            totalMaximumDrawdown += maximumDrawdown;
            if (equity < options.InitialEquity)
            {
                lossCount++;
            }

            if (equity == 0m)
            {
                ruinCount++;
            }

            worstLossFraction = Math.Max(worstLossFraction, 1m - equity / options.InitialEquity);
        }

        Array.Sort(finalEquities);
        Array.Sort(maximumDrawdowns);
        return new MonteCarloStressResult(
            options.ScenarioId,
            options.Seed,
            options.SimulationCount,
            options.HorizonPeriods,
            observedPeriodReturns.Count,
            options.InitialEquity,
            totalFinalEquity / options.SimulationCount,
            finalEquities[0],
            finalEquities[^1],
            Quantile(finalEquities, 0.05m),
            Quantile(finalEquities, 0.50m),
            Quantile(finalEquities, 0.95m),
            totalMaximumDrawdown / options.SimulationCount,
            Quantile(maximumDrawdowns, 0.50m),
            Quantile(maximumDrawdowns, 0.95m),
            (decimal)lossCount / options.SimulationCount,
            (decimal)ruinCount / options.SimulationCount,
            worstLossFraction);
    }

    public static MonteCarloStressResult Evaluate(
        BacktestEvaluationResult backtest,
        MonteCarloStressOptions options)
    {
        ArgumentNullException.ThrowIfNull(backtest);
        return Evaluate(backtest.EquityCurve.Select(point => point.PeriodReturn).ToArray(), options);
    }

    private static decimal ApplyStress(decimal observedReturn, MonteCarloStressOptions options)
    {
        var stressed = observedReturn < 0m
            ? observedReturn * options.NegativeReturnMultiplier
            : observedReturn;
        return stressed - options.AdverseReturnShock;
    }

    private static decimal Quantile(decimal[] sortedValues, decimal probability)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var fraction = position - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    // Small fixed-width generator keeps results independent from framework Random changes.
    private sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(int seed)
        {
            _state = (ulong)(uint)seed + 0x9E3779B97F4A7C15UL;
        }

        public int Next(int exclusiveUpperBound)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            var value = _state * 0x2545F4914F6CDD1DUL;
            return (int)(value % (uint)exclusiveUpperBound);
        }
    }
}
