using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

/// <summary>
/// Tests that assert on the process-wide <c>TradingTelemetry</c> activity stream
/// must not run concurrently with other tests that also emit order activities,
/// otherwise the observed sequence is non-deterministic.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515",
    Justification = "xUnit requires collection definition classes to be public.")]
public sealed class TelemetryActivityFixture
{
    public const string Name = "TelemetryActivities";
}
