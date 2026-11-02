import { AlertCircle, Calendar, FileText, Landmark } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';

interface BenchmarkMetric {
  id: string;
  name: string;
  category: 'Renda Fixa' | 'Inflação' | 'Renda Variável' | 'Câmbio';
  currentRateAnnual: number;
  returnInPeriod: number;
  description: string;
  badgeColor: string;
}

export const BenchmarkDashboardView: React.FC = () => {
  const [selectedPeriodDays, setSelectedPeriodDays] = useState<number>(252); // 1 ano (252 dias úteis)
  const [capitalInput, setCapitalInput] = useState('100000');
  const [usdInput, setUsdInput] = useState('5.65');
  const initialInvestmentBrl = Number(capitalInput);
  const usdRate = Number(usdInput);
  const validCapital = capitalInput.trim() !== '' && Number.isFinite(initialInvestmentBrl) && initialInvestmentBrl > 0;
  const validRate = usdInput.trim() !== '' && Number.isFinite(usdRate) && usdRate > 0;
  const simulatedCapitalUsd = validCapital && validRate ? initialInvestmentBrl / usdRate : Number.NaN;

  // Fixed hypothetical annual rates, not current market observations.
  const cdiAnnual = 0.1075; // 10.75% a.a.
  const selicAnnual = 0.1075; // 10.75% a.a.
  const ipcaAnnual = 0.045; // 4.50% a.a.
  const tesouroIpcaAnnual = 0.1103; // IPCA + 6.25%
  const ibovAnnual = 0.124; // 12.40% a.a.
  const usdVariationAnnual = 0.082; // 8.20% a.a.

  // Fator proporcional ao período selecionado
  const yearFraction = selectedPeriodDays / 252;
  const cdiPeriodReturn = (1 + cdiAnnual) ** yearFraction - 1;
  const ipcaPeriodReturn = (1 + ipcaAnnual) ** yearFraction - 1;
  const tesouroPeriodReturn = (1 + tesouroIpcaAnnual) ** yearFraction - 1;
  const ibovPeriodReturn = (1 + ibovAnnual) ** yearFraction - 1;
  const usdPeriodVariation = (1 + usdVariationAnnual) ** yearFraction - 1;

  const benchmarks: BenchmarkMetric[] = [
    {
      id: 'cdi',
      name: 'CDI (Certificado de Depósito Interbancário)',
      category: 'Renda Fixa',
      currentRateAnnual: cdiAnnual,
      returnInPeriod: cdiPeriodReturn,
      description: 'Taxa livre de risco de referência para o mercado financeiro brasileiro (252 dias úteis).',
      badgeColor: 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
    },
    {
      id: 'selic',
      name: 'Taxa SELIC Meta (Copom / Banco Central)',
      category: 'Renda Fixa',
      currentRateAnnual: selicAnnual,
      returnInPeriod: cdiPeriodReturn,
      description: 'Taxa básica de juros da economia brasileira definida pelo Banco Central.',
      badgeColor: 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
    },
    {
      id: 'ipca',
      name: 'IPCA (Índice de Preços ao Consumidor Amplo)',
      category: 'Inflação',
      currentRateAnnual: ipcaAnnual,
      returnInPeriod: ipcaPeriodReturn,
      description: 'Inflação oficial calculada pelo IBGE. Régua essencial para avaliar ganhos reais.',
      badgeColor: 'bg-amber-500/10 text-amber-400 border-amber-500/20',
    },
    {
      id: 'tesouro_ipca',
      name: 'Tesouro IPCA+ (hipótese simplificada)',
      category: 'Renda Fixa',
      currentRateAnnual: tesouroIpcaAnnual,
      returnInPeriod: tesouroPeriodReturn,
      description: 'Hipótese simplificada; não representa cotação, marcação a mercado ou resultado de um título.',
      badgeColor: 'bg-indigo-500/10 text-indigo-400 border-indigo-500/20',
    },
    {
      id: 'ibovespa',
      name: 'IBOVESPA (B3 - Bolsa de Valores do Brasil)',
      category: 'Renda Variável',
      currentRateAnnual: ibovAnnual,
      returnInPeriod: ibovPeriodReturn,
      description: 'Principal índice de ações brasileiras negociadas na B3.',
      badgeColor: 'bg-cyan-500/10 text-cyan-400 border-cyan-500/20',
    },
    {
      id: 'dolar_ptax',
      name: 'USD / BRL (cenário hipotético)',
      category: 'Câmbio',
      currentRateAnnual: usdVariationAnnual,
      returnInPeriod: usdPeriodVariation,
      description: 'Hipótese de variação cambial, independente da cotação manual de conversão do capital.',
      badgeColor: 'bg-purple-500/10 text-purple-400 border-purple-500/20',
    },
  ];

  return (
    <div className="space-y-6" data-testid="benchmark-dashboard-view">
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 border-b border-slate-800 pb-5">
        <div>
          <div className="flex items-center gap-2">
            <Landmark className="w-5 h-5 text-emerald-400" />
            <h1 className="text-xl font-bold text-slate-100">Benchmarks Financeiros & Macroeconômicos Brasileiros</h1>
          </div>
          <p className="text-xs text-slate-400 mt-1">
            Projeções ilustrativas independentes da performance realizada do robô. Não representam resultados
            históricos.
          </p>
        </div>

        {/* Controles de Simulação */}
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 bg-slate-900/80 px-2.5 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">Capital (BRL):</span>
            <input
              type="number"
              aria-label="Capital Inicial Simulado em Reais"
              min="0.01"
              aria-invalid={!validCapital}
              value={capitalInput}
              onChange={(e) => setCapitalInput(e.target.value)}
              className="w-24 bg-slate-950 border border-slate-700 rounded px-2 py-0.5 text-right text-slate-100 font-mono focus:outline-none focus:border-emerald-500"
            />
          </div>

          <div className="flex items-center gap-2 bg-slate-900/80 px-2.5 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">USD/BRL:</span>
            <input
              type="number"
              step="0.05"
              aria-label="Cotação do Dólar em Reais"
              min="0.01"
              aria-invalid={!validRate}
              value={usdInput}
              onChange={(e) => setUsdInput(e.target.value)}
              className="w-16 bg-slate-950 border border-slate-700 rounded px-2 py-0.5 text-right text-slate-100 font-mono focus:outline-none focus:border-emerald-500"
            />
          </div>

          {/* Período Selector */}
          <div className="flex items-center gap-2 bg-slate-900/80 p-1.5 rounded-lg border border-slate-800">
            <Calendar className="w-4 h-4 text-slate-400 ml-1.5" />
            <span className="text-xs text-slate-400 pr-1">Horizonte:</span>
            {[
              { days: 21, label: '1 Mês' },
              { days: 63, label: '3 Meses' },
              { days: 126, label: '6 Meses' },
              { days: 252, label: '1 Ano (252 d.u.)' },
              { days: 504, label: '2 Anos' },
            ].map((item) => (
              <button
                type="button"
                key={item.days}
                onClick={() => setSelectedPeriodDays(item.days)}
                className={`px-2.5 py-1 text-xs rounded font-medium transition-colors cursor-pointer ${
                  selectedPeriodDays === item.days
                    ? 'bg-emerald-600 text-white'
                    : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800'
                }`}
              >
                {item.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      <section className="bg-amber-950/30 border border-amber-800 rounded-xl p-5 text-sm text-amber-200">
        <p>Comparação indisponível: a API não fornece séries históricas alinhadas.</p>
        <p className="mt-2 text-xs">
          Taxas fixas hipotéticas: projeções ilustrativas, sem cotações atuais, custos, impostos ou garantia de retorno.
        </p>
        <p className="mt-2 text-xs">
          USD/BRL é uma hipótese manual para converter o capital, não a variação cambial do período nem uma cotação
          PTAX.
        </p>
        <p className="mt-2" data-testid="simulated-capital-usd">
          Capital equivalente simulado (USD):{' '}
          {Number.isFinite(simulatedCapitalUsd)
            ? `$ ${simulatedCapitalUsd.toLocaleString('pt-BR', { maximumFractionDigits: 2 })}`
            : 'Indisponível'}
        </p>
        {!validRate && <p role="alert">Cotação inválida: informe um número finito maior que zero.</p>}
        {!validCapital && <p role="alert">Capital inválido: informe um número finito maior que zero.</p>}
      </section>

      {/* Tabela Comparativa Detalhada dos Índices */}
      <div className="bg-slate-900/40 border border-slate-800 rounded-xl overflow-hidden">
        <div className="p-4 border-b border-slate-800 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <FileText className="w-4 h-4 text-emerald-400" />
            <h2 className="text-sm font-semibold text-slate-200">Projeções ilustrativas de cenários macroeconômicos</h2>
          </div>
          <span className="text-xs text-slate-400">Horizonte: {selectedPeriodDays} dias úteis</span>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="bg-slate-900/80 text-slate-400 border-b border-slate-800">
              <tr>
                <th className="py-3 px-4 font-semibold">Índice / Benchmark</th>
                <th className="py-3 px-3 font-semibold">Categoria</th>
                <th className="py-3 px-3 font-semibold text-right">Hipótese Anual</th>
                <th className="py-3 px-3 font-semibold text-right">Projeção no Horizonte</th>
                <th className="py-3 px-4 font-semibold text-right">
                  Simulação R$ {validCapital ? initialInvestmentBrl.toLocaleString('pt-BR') : 'Indisponível'}
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800/60 font-mono">
              {/* Linhas dos Benchmarks */}
              {benchmarks.map((b) => {
                const finalBrl = initialInvestmentBrl * (1 + b.returnInPeriod);

                return (
                  <tr key={b.id} className="hover:bg-slate-800/30 text-slate-300 transition-colors">
                    <td className="py-3 px-4 font-sans font-medium text-slate-200">
                      <div>{b.name}</div>
                      <div className="text-[10px] text-slate-400 font-normal font-sans">{b.description}</div>
                    </td>
                    <td className="py-3 px-3 font-sans">
                      <span className={`px-2 py-0.5 rounded text-[10px] border ${b.badgeColor}`}>{b.category}</span>
                    </td>
                    <td className="py-3 px-3 text-right">{(b.currentRateAnnual * 100).toFixed(2)}%</td>
                    <td className="py-3 px-3 text-right">{(b.returnInPeriod * 100).toFixed(2)}%</td>
                    <td className="py-3 px-4 text-right text-slate-200">
                      {validCapital && Number.isFinite(finalBrl)
                        ? `R$ ${finalBrl.toLocaleString('pt-BR', { maximumFractionDigits: 0 })}`
                        : 'Indisponível'}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>

      {/* Seção Regulatória & Fiscal Brasileira */}
      <div className="bg-slate-900/50 border border-slate-800 rounded-xl p-5">
        <h3 className="text-sm font-semibold text-slate-200 flex items-center gap-2 mb-3">
          <AlertCircle className="w-4 h-4 text-amber-400" />
          <span>Considerações Tributárias & Regulatórias (Brasil / Ativos Virtuais)</span>
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs text-slate-300">
          <div className="bg-slate-950/60 p-3.5 rounded-lg border border-slate-800/80">
            <div className="font-semibold text-slate-100 mb-1">IN RFB nº 1.888/2019</div>
            <p className="text-slate-400 text-[11px] leading-relaxed">
              Obrigação acessória de reporte mensal para exchanges domiciliadas no Brasil ou para operações que
              ultrapassem R$ 30.000 mensais em exchanges no exterior ou carteiras descentralizadas.
            </p>
          </div>
          <div className="bg-slate-950/60 p-3.5 rounded-lg border border-slate-800/80">
            <div className="font-semibold text-slate-100 mb-1">Lei nº 14.754/2023 (Offshore / Exterior)</div>
            <p className="text-slate-400 text-[11px] leading-relaxed">
              Ativos virtuais e carteiras digitais detidos no exterior sofrem tributação anual uniforme de 15% sobre o
              ganho de capital realizado, sem incidência da isenção mensal de R$ 35.000 aplicável ao mercado nacional.
            </p>
          </div>
          <div className="bg-slate-950/60 p-3.5 rounded-lg border border-slate-800/80">
            <div className="font-semibold text-slate-100 mb-1">Custo de Oportunidade Selic</div>
            <p className="text-slate-400 text-[11px] leading-relaxed">
              Com taxa Selic em dois dígitos, o robô opera com hurdle rate mínimo: trades e alocações só são
              justificados quando a expectativa matemática líquida de Sharpe e retorno supera o carrego livre de risco
              do CDI.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
};
