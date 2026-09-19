using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

public sealed record OutOfSampleSplit
{
    public OutOfSampleSplit(int inSampleStartIndex, int inSampleLength, int outOfSampleStartIndex, int outOfSampleLength)
    {
        if (inSampleStartIndex < 0 || inSampleLength <= 0 || outOfSampleStartIndex < 0 || outOfSampleLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inSampleStartIndex), "Split indexes and lengths must be non-negative and non-zero.");
        }

        if (outOfSampleStartIndex < inSampleStartIndex + inSampleLength)
        {
            throw new ArgumentException("Out-of-sample data must start after the in-sample range.", nameof(outOfSampleStartIndex));
        }

        InSampleStartIndex = inSampleStartIndex;
        InSampleLength = inSampleLength;
        OutOfSampleStartIndex = outOfSampleStartIndex;
        OutOfSampleLength = outOfSampleLength;
    }

    public int InSampleStartIndex { get; }
    public int InSampleLength { get; }
    public int OutOfSampleStartIndex { get; }
    public int OutOfSampleLength { get; }
}

public sealed record WalkForwardWindow(
    int WindowNumber,
    int InSampleStartIndex,
    int InSampleLength,
    int OutOfSampleStartIndex,
    int OutOfSampleLength)
{
    public OutOfSampleSplit Split => new(InSampleStartIndex, InSampleLength, OutOfSampleStartIndex, OutOfSampleLength);
}

/// <summary>Creates rolling train/test windows; it does not fit parameters or evaluate results.</summary>
public static class WalkForwardPlan
{
    public static IReadOnlyList<WalkForwardWindow> Create(int totalBars, int trainingBars, int outOfSampleBars, int stepBars)
    {
        if (totalBars <= 0 || trainingBars <= 0 || outOfSampleBars <= 0 || stepBars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBars), "Window sizes and total bars must be greater than zero.");
        }

        var windows = new List<WalkForwardWindow>();
        var number = 1;
        for (var trainStart = 0; trainStart + trainingBars + outOfSampleBars <= totalBars; trainStart += stepBars)
        {
            windows.Add(new WalkForwardWindow(
                number++,
                trainStart,
                trainingBars,
                trainStart + trainingBars,
                outOfSampleBars));
        }

        return windows;
    }
}

public sealed record SensitivityCase
{
    public SensitivityCase(string caseId, TrendFollowingParameters parameters)
    {
        CaseId = string.IsNullOrWhiteSpace(caseId)
            ? throw new ArgumentException("Case identifier is required.", nameof(caseId))
            : caseId.Trim();
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public string CaseId { get; }

    public TrendFollowingParameters Parameters { get; }
}

/// <summary>Validates an explicit parameter matrix without selecting a winner or hiding weak results.</summary>
public static class SensitivityPlan
{
    public static IReadOnlyList<SensitivityCase> Create(IEnumerable<SensitivityCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var materialized = cases.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one sensitivity case is required.", nameof(cases));
        }

        if (materialized.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException("Sensitivity case identifiers must be unique.", nameof(cases));
        }

        return materialized;
    }
}
