using CryptoTrading.Domain.Regimes;

namespace CryptoTrading.Domain.Research;

/// <summary>Historical performance reward statistics for a strategy under a specific regime.</summary>
public sealed record StrategyRegimePerformance(
    string StrategyId,
    MarketRegime Regime,
    int ObservationCount,
    decimal CumulativeReward,
    decimal AverageReward)
{
    public StrategyRegimePerformance Update(decimal reward)
    {
        var newCount = ObservationCount + 1;
        var newCumulative = CumulativeReward + reward;
        return new StrategyRegimePerformance(
            StrategyId,
            Regime,
            newCount,
            newCumulative,
            newCumulative / newCount);
    }
}

/// <summary>Contextual allocation share for a strategy.</summary>
public sealed record StrategyMetaWeight(
    string StrategyId,
    decimal Weight,
    decimal AllocatedCapital);

/// <summary>
/// Contextual meta-allocation engine (US-173).
/// Implements Bayesian / contextual bandit dynamic weighting across strategies conditioned on regime.
/// </summary>
public sealed class ContextualMetaAllocator
{
    private readonly Dictionary<(string StrategyId, MarketRegime Regime), StrategyRegimePerformance> _performances = new();
    private readonly object _lock = new();

    public void RecordReward(string strategyId, MarketRegime regime, decimal reward)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        lock (_lock)
        {
            var key = (strategyId, regime);
            if (_performances.TryGetValue(key, out var current))
            {
                _performances[key] = current.Update(reward);
            }
            else
            {
                _performances[key] = new StrategyRegimePerformance(strategyId, regime, 1, reward, reward);
            }
        }
    }

    public IReadOnlyList<StrategyMetaWeight> ComputeWeights(
        MarketRegime regime,
        IReadOnlyList<string> candidateStrategies,
        decimal totalCapital,
        decimal explorationFactor = 0.10m)
    {
        ArgumentNullException.ThrowIfNull(candidateStrategies);
        if (candidateStrategies.Count == 0 || totalCapital <= 0m)
        {
            return [];
        }

        // Chop or Illiquid: fail-closed safety (0 capital allocated)
        if (regime is MarketRegime.HighVolatilityChop or MarketRegime.IlliquidOrStale)
        {
            return candidateStrategies.Select(s => new StrategyMetaWeight(s, 0m, 0m)).ToList();
        }

        lock (_lock)
        {
            // Collect scores
            var rawScores = new List<(string Strategy, decimal Score)>(candidateStrategies.Count);
            foreach (var strategy in candidateStrategies)
            {
                var key = (strategy, regime);
                var avgReward = _performances.TryGetValue(key, out var perf) ? perf.AverageReward : 0.01m;
                // Floor at 0.001 to keep positive probability
                var positiveScore = Math.Max(0.001m, 1.0m + avgReward);
                rawScores.Add((strategy, positiveScore));
            }

            var sumScores = rawScores.Sum(x => x.Score);
            if (sumScores <= 0m)
            {
                sumScores = 1.0m;
            }

            // Softmax-like normalized weights with exploration blend
            var uniformWeight = 1.0m / candidateStrategies.Count;
            var weights = new List<StrategyMetaWeight>(candidateStrategies.Count);

            foreach (var (strategy, score) in rawScores)
            {
                var exploitedWeight = score / sumScores;
                var blendedWeight = ((1.0m - explorationFactor) * exploitedWeight) + (explorationFactor * uniformWeight);
                
                // Enforce single-strategy cap of 75% for diversification safety
                var cappedWeight = Math.Min(0.75m, blendedWeight);
                weights.Add(new StrategyMetaWeight(strategy, Math.Round(cappedWeight, 4), Math.Round(cappedWeight * totalCapital, 2)));
            }

            return weights;
        }
    }
}
