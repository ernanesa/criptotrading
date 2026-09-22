using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class BrazilianMacroBenchmarksTests
{
    [Fact]
    public void BrazilianMacroBenchmarksCalculatesAlphaOverCdiAndIpca()
    {
        // Strategy returns 35% USD in 1 year (365 days) with 5% USD appreciation vs BRL
        var compCdi = BrazilianMacroBenchmarks.Compare(
            strategyReturnUsdRate: 0.35m,
            periodDays: 365,
            benchmarkType: BrazilianBenchmarkType.SelicCdi,
            usdBrlDepreciationRate: 0.05m);

        Assert.True(compCdi.OutperformedBenchmark);
        Assert.True(compCdi.AlphaExcessReturn > 0.20m); // Alpha > 20%
        Assert.True(compCdi.PercentOfCdiEquivalent > 300.0m); // ~388% of CDI
        Assert.Equal("CDI / Taxa SELIC", compCdi.BenchmarkName);
        Assert.Contains("ALPHA_POSITIVO", compCdi.Summary, StringComparison.Ordinal);

        // Inflation comparison (IPCA)
        var compIpca = BrazilianMacroBenchmarks.Compare(
            strategyReturnUsdRate: 0.35m,
            periodDays: 365,
            benchmarkType: BrazilianBenchmarkType.IpcaInflation,
            usdBrlDepreciationRate: 0.05m);

        Assert.True(compIpca.OutperformedBenchmark);
        Assert.True(compIpca.AlphaExcessReturn > 0.30m); // Real return over inflation > 30%
        Assert.Equal("Inflação Oficial (IPCA)", compIpca.BenchmarkName);

        // Tesouro IPCA+ comparison
        var compTesouro = BrazilianMacroBenchmarks.Compare(
            strategyReturnUsdRate: 0.35m,
            periodDays: 365,
            benchmarkType: BrazilianBenchmarkType.TesouroIpcaPlus,
            usdBrlDepreciationRate: 0.0m);

        Assert.True(compTesouro.OutperformedBenchmark);
        Assert.Equal("Tesouro IPCA+ (Juro Real)", compTesouro.BenchmarkName);
    }

    [Fact]
    public void BrazilianMacroBenchmarksHandlesUnderperformanceGracefully()
    {
        // Strategy returns 3% in 1 year, while CDI is ~10.75%
        var underperf = BrazilianMacroBenchmarks.Compare(
            strategyReturnUsdRate: 0.03m,
            periodDays: 365,
            benchmarkType: BrazilianBenchmarkType.SelicCdi);

        Assert.False(underperf.OutperformedBenchmark);
        Assert.True(underperf.AlphaExcessReturn < 0m);
        Assert.True(underperf.PercentOfCdiEquivalent < 100.0m); // ~28% do CDI
        Assert.Contains("UNDERPERFORM", underperf.Summary, StringComparison.Ordinal);
    }
}
