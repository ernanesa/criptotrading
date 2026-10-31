using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Backtesting.Tests;

// Synthetic unit-test outcomes isolate operational guards from classifier
// quality. These are real trained artifacts, never evidence of trading alpha.
internal static class MlOperationalTestModels
{
    private static readonly Lazy<MlArtifactCatalog> High = new(() => Build(low: false));
    private static readonly Lazy<MlArtifactCatalog> Low = new(() => Build(low: true));
    private static readonly Lazy<MlArtifactCatalog> Defensive = new(() => Build(low: true, sellLow: false));
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);

    public static MlArtifactCatalog Catalog(bool low) => low ? Low.Value : High.Value;
    public static MlArtifactCatalog DefensiveCatalog => Defensive.Value;

    private static MlArtifactCatalog Build(bool low, bool? sellLow = null)
    {
        List<MlArtifactExport> exports = [];
        foreach (var symbol in new[] { "BTCUSDT", "ETHUSDT" })
        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            var outcomeLow = side == OrderSide.Sell ? sellLow ?? low : low;
            exports.Add(MlDecisionTrainer.Train(Rows(0, 512, outcomeLow), Rows(600, 128, outcomeLow), Rows(800, 128, outcomeLow),
                new MlArtifactPolicy(symbol, "1m", side, 4), At.AddMinutes(1200), At.AddDays(3)));
        }
        var bundle = MlArtifactCatalog.CreateBundle(exports);
        return MlArtifactCatalog.LoadVerified(bundle.Payload.Span, bundle.Sha256);
    }

    private static MlTrainingObservation[] Rows(int offset, int count, bool low) =>
        Enumerable.Range(offset, count).Select(i =>
        {
            var features = new MlFeatureVector((i * 7 % 17 - 8) / 2m, i * 11 % 31 - 15,
                i * 13 % 61 - 30, 0.5m + (i * 17 % 60) / 2m);
            var at = At.AddMinutes(i);
            return new MlTrainingObservation(at, at.AddMinutes(4), features, low ? i % 10 == 0 : i % 10 != 0);
        }).ToArray();
}
