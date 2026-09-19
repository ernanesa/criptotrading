using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

/// <summary>
/// Local-only sensitivity harness. It executes every preregistered case and
/// deliberately does not rank or select a winner.
/// </summary>
public sealed class OosSensitivityTests
{
    [Fact]
    [Trait("Category", "OosSensitivity")]
    public void ExecutesEveryCaseAcrossTheSameChronologicalOosWindows()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var windows = WalkForwardPlan.Create(dataset.Bars.Count, trainingBars: 60, outOfSampleBars: 20, stepBars: 20);
        var cases = SensitivityPlan.Create([
            new SensitivityCase("baseline", TrendFollowingParameters.BtcUsdtBaseline),
            new SensitivityCase(
                "faster-10-30",
                new TrendFollowingParameters("trend-following-btcusdt", "1.0.0", new Symbol("BTCUSDT"), 10, 30, 0.001m, 0.0005m)),
            new SensitivityCase(
                "cost-stress",
                new TrendFollowingParameters("trend-following-btcusdt", "1.0.0", new Symbol("BTCUSDT"), 20, 50, 0.002m, 0.001m)),
        ]);

        var firstRun = Evaluate(dataset, windows, cases);
        var secondRun = Evaluate(dataset, windows, cases);

        Assert.Equal(cases.Count, firstRun.Length);
        Assert.All(firstRun, result => Assert.Equal(windows.Count, result.Windows.Count));
        Assert.Equal(
            firstRun.Select(result => result.Fingerprint),
            secondRun.Select(result => result.Fingerprint));
        Assert.Equal(cases.Select(item => item.CaseId), firstRun.Select(result => result.CaseId));
        Assert.Equal(
            cases.Select(item => item.Parameters.ConfigurationFingerprint),
            firstRun.Select(result => result.ConfigurationFingerprint));
        Assert.All(
            firstRun.SelectMany(result => result.Windows),
            window => Assert.Equal(20, window.PeriodCount));
    }

    private static SensitivityResult[] Evaluate(
        (DatasetMetadata Metadata, IReadOnlyList<CloseBar> Bars) dataset,
        IReadOnlyList<WalkForwardWindow> windows,
        IReadOnlyList<SensitivityCase> cases) => cases
        .Select(item =>
        {
            var evaluation = WalkForwardEvaluator.Evaluate(
                dataset.Metadata,
                dataset.Bars,
                item.Parameters,
                BacktestEvaluationOptions.DailyCrypto(),
                windows);
            var fingerprint = string.Join(
                '|',
                new[]
                {
                    dataset.Metadata.ContentHash,
                    item.CaseId,
                    item.Parameters.ConfigurationFingerprint,
                }.Concat(evaluation.Windows.Select(window =>
                    $"{window.FinalEquity:G29}:{window.NetReturn:G29}:{window.MaximumDrawdown:G29}")));
            return new SensitivityResult(
                item.CaseId,
                item.Parameters.ConfigurationFingerprint,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(fingerprint))),
                evaluation);
        })
        .ToArray();

    private sealed record SensitivityResult(
        string CaseId,
        string ConfigurationFingerprint,
        string Fingerprint,
        WalkForwardEvaluationResult Evaluation)
    {
        public IReadOnlyList<OutOfSampleWindowResult> Windows => Evaluation.Windows;
    }
}
