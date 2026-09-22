namespace CryptoTrading.Domain.Research;

public enum BrazilianBenchmarkType
{
    SelicCdi,            // Taxa básica de juros / CDI (~10.75% a.a.)
    IpcaInflation,       // Inflação oficial IPCA (~4.50% a.a.)
    TesouroIpcaPlus,     // Tesouro IPCA+ Juro Real (~IPCA + 6.5% a.a. = ~11.3% a.a.)
    Ibovespa,            // Índice Bovespa (~12.5% a.a. médio)
    UsdBrlDollar,        // Variação Cambial Dólar / Real
}

/// <summary>Comparative result measuring crypto strategy performance against Brazilian macro benchmarks.</summary>
public sealed record BrazilianBenchmarkComparison(
    BrazilianBenchmarkType BenchmarkType,
    string BenchmarkName,
    decimal StrategyReturnUsd,
    decimal StrategyReturnBrl,
    decimal BenchmarkReturn,
    decimal AlphaExcessReturn,
    decimal PercentOfCdiEquivalent,
    bool OutperformedBenchmark,
    string Summary);

/// <summary>
/// Brazilian macro benchmark engine (SELIC, CDI, IPCA, Tesouro Direto, IBOVESPA).
/// Evaluates opportunity cost for Brazilian investors, computing alpha over local risk-free rates and inflation.
/// </summary>
public static class BrazilianMacroBenchmarks
{
    // Annualized benchmark reference baseline rates (in decimal, e.g. 0.1075 = 10.75%)
    public const decimal DefaultAnnualSelicCdiRate = 0.1075m; // 10.75% a.a.
    public const decimal DefaultAnnualIpcaRate = 0.045m;       // 4.50% a.a.
    public const decimal DefaultAnnualTesouroIpcaPlusRate = 0.1130m; // 11.30% a.a.
    public const decimal DefaultAnnualIbovespaRate = 0.1250m;  // 12.50% a.a.

    public static BrazilianBenchmarkComparison Compare(
        decimal strategyReturnUsdRate,
        int periodDays,
        BrazilianBenchmarkType benchmarkType,
        decimal usdBrlDepreciationRate = 0.0m)
    {
        if (periodDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(periodDays), "Period days must be strictly positive.");
        }

        var yearFraction = (decimal)periodDays / 365.0m;

        var annualRate = benchmarkType switch
        {
            BrazilianBenchmarkType.SelicCdi => DefaultAnnualSelicCdiRate,
            BrazilianBenchmarkType.IpcaInflation => DefaultAnnualIpcaRate,
            BrazilianBenchmarkType.TesouroIpcaPlus => DefaultAnnualTesouroIpcaPlusRate,
            BrazilianBenchmarkType.Ibovespa => DefaultAnnualIbovespaRate,
            BrazilianBenchmarkType.UsdBrlDollar => usdBrlDepreciationRate,
            _ => DefaultAnnualSelicCdiRate,
        };

        // Compound interest over period: (1 + annualRate)^yearFraction - 1
        var benchmarkReturn = (decimal)(Math.Pow((double)(1.0m + annualRate), (double)yearFraction) - 1.0);

        // Strategy return in BRL: (1 + R_usd) * (1 + R_fx) - 1
        var strategyReturnBrl = ((1.0m + strategyReturnUsdRate) * (1.0m + usdBrlDepreciationRate)) - 1.0m;

        // CDI return over period for percentage comparison
        var cdiReturn = (decimal)(Math.Pow((double)(1.0m + DefaultAnnualSelicCdiRate), (double)yearFraction) - 1.0);
        var percentOfCdi = cdiReturn > 0m
            ? Math.Round((strategyReturnBrl / cdiReturn) * 100.0m, 1)
            : 0m;

        var alpha = strategyReturnBrl - benchmarkReturn;
        var outpaced = alpha > 0m;

        var name = benchmarkType switch
        {
            BrazilianBenchmarkType.SelicCdi => "CDI / Taxa SELIC",
            BrazilianBenchmarkType.IpcaInflation => "Inflação Oficial (IPCA)",
            BrazilianBenchmarkType.TesouroIpcaPlus => "Tesouro IPCA+ (Juro Real)",
            BrazilianBenchmarkType.Ibovespa => "Índice IBOVESPA (B3)",
            BrazilianBenchmarkType.UsdBrlDollar => "Câmbio USD/BRL (Dólar Comercial)",
            _ => "Benchmark",
        };

        var summary = outpaced
            ? $"ALPHA_POSITIVO: Estratégia superou {name} em +{alpha * 100.0m:F2}% no período ({percentOfCdi:F0}% do CDI)."
            : $"UNDERPERFORM: Retorno ficou abaixo de {name} em {Math.Abs(alpha) * 100.0m:F2}%.";

        return new BrazilianBenchmarkComparison(
            benchmarkType,
            name,
            Math.Round(strategyReturnUsdRate, 4),
            Math.Round(strategyReturnBrl, 4),
            Math.Round(benchmarkReturn, 4),
            Math.Round(alpha, 4),
            percentOfCdi,
            outpaced,
            summary);
    }
}
