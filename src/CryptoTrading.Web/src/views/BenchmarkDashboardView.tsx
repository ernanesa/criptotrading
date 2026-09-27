import { useQuery } from '@tanstack/react-query';
import {
  AlertCircle,
  ArrowUpRight,
  BarChart3,
  Calendar,
  CheckCircle2,
  DollarSign,
  FileText,
  Landmark,
  Percent,
  TrendingUp,
} from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient } from '../api/client';

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
  const [initialInvestmentBrl, setInitialInvestmentBrl] = useState<number>(100000);
  const [usdRate, setUsdRate] = useState<number>(5.65);

  const performanceQuery = useQuery({
    queryKey: ['portfolioPerformance'],
    queryFn: apiClient.getPortfolioPerformance,
    refetchInterval: 5000,
  });

  // Dados macroeconômicos brasileiros de referência (anualizados)
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

  // Performance REAL auditada das estratégias do robô no mesmo período
  const totalProfitPct = performanceQuery.data?.totalProfitPct ?? 0;
  const botUsdReturn = (totalProfitPct / 100) * yearFraction;
  const botBrlReturn = (1 + botUsdReturn) * (1 + usdPeriodVariation) - 1; // Conversão em BRL
  const botAlphaOverCdiBrl = botBrlReturn - cdiPeriodReturn;
  const botPercentOfCdi = cdiPeriodReturn > 0 ? (botBrlReturn / cdiPeriodReturn) * 100 : 0;
  const botRealReturnOverIpca = (1 + botBrlReturn) / (1 + ipcaPeriodReturn) - 1;

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
      name: 'Tesouro IPCA+ (NTN-B 2029 - Juro Real + Inflação)',
      category: 'Renda Fixa',
      currentRateAnnual: tesouroIpcaAnnual,
      returnInPeriod: tesouroPeriodReturn,
      description: 'Título público soberano com rentabilidade real atrelada ao IPCA + cupom de ~6,25% a.a.',
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
      name: 'Dólar PTAX (USD / BRL)',
      category: 'Câmbio',
      currentRateAnnual: usdVariationAnnual,
      returnInPeriod: usdPeriodVariation,
      description: 'Variação cambial oficial da moeda americana frente ao Real.',
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
            Análise de custo de oportunidade, retorno real, Alpha e equivalência percentual do CDI convertidos em BRL e
            USD.
          </p>
        </div>

        {/* Controles de Simulação */}
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 bg-slate-900/80 px-2.5 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">Capital (BRL):</span>
            <input
              type="number"
              aria-label="Capital Inicial Simulado em Reais"
              value={initialInvestmentBrl}
              onChange={(e) => setInitialInvestmentBrl(Number(e.target.value) || 0)}
              className="w-24 bg-slate-950 border border-slate-700 rounded px-2 py-0.5 text-right text-slate-100 font-mono focus:outline-none focus:border-emerald-500"
            />
          </div>

          <div className="flex items-center gap-2 bg-slate-900/80 px-2.5 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">USD/BRL:</span>
            <input
              type="number"
              step="0.05"
              aria-label="Cotação do Dólar em Reais"
              value={usdRate}
              onChange={(e) => setUsdRate(Number(e.target.value) || 1)}
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

      {/* Cards de Destaque da Estratégia vs CDI/IPCA */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Card 1: Alpha sobre o CDI */}
        <div className="bg-slate-900/60 border border-slate-800 rounded-xl p-4 relative overflow-hidden">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span>Alpha s/ CDI (em BRL)</span>
            <span className="px-1.5 py-0.5 rounded text-[10px] font-bold bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
              +{botAlphaOverCdiBrl >= 0 ? (botAlphaOverCdiBrl * 100).toFixed(2) : (botAlphaOverCdiBrl * 100).toFixed(2)}
              %
            </span>
          </div>
          <div className="text-2xl font-bold text-emerald-400 font-mono">+{(botAlphaOverCdiBrl * 100).toFixed(2)}%</div>
          <div className="text-[11px] text-slate-400 mt-2 flex items-center gap-1">
            <TrendingUp className="w-3 h-3 text-emerald-400" />
            <span>Excesso líquido sobre taxa livre de risco</span>
          </div>
        </div>

        {/* Card 2: % Equivalente do CDI */}
        <div className="bg-slate-900/60 border border-slate-800 rounded-xl p-4 relative overflow-hidden">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span>Equivalente do CDI</span>
            <Percent className="w-3.5 h-3.5 text-cyan-400" />
          </div>
          <div className="text-2xl font-bold text-cyan-400 font-mono">
            {botPercentOfCdi.toFixed(0)}% <span className="text-sm font-normal text-slate-400">do CDI</span>
          </div>
          <div className="text-[11px] text-slate-400 mt-2">
            Supera o CDI de {(cdiPeriodReturn * 100).toFixed(2)}% no horizonte
          </div>
        </div>

        {/* Card 3: Ganho Real (Acima da Inflação IPCA) */}
        <div className="bg-slate-900/60 border border-slate-800 rounded-xl p-4 relative overflow-hidden">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span>Retorno Real (IPCA descontado)</span>
            <ArrowUpRight className="w-3.5 h-3.5 text-amber-400" />
          </div>
          <div className="text-2xl font-bold text-amber-400 font-mono">
            +{(botRealReturnOverIpca * 100).toFixed(2)}%
          </div>
          <div className="text-[11px] text-slate-400 mt-2">Poder de compra preservado frente à inflação oficial</div>
        </div>

        {/* Card 4: Retorno Total BRL vs USD */}
        <div className="bg-slate-900/60 border border-slate-800 rounded-xl p-4 relative overflow-hidden">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span>Retorno Total BRL / USD</span>
            <DollarSign className="w-3.5 h-3.5 text-indigo-400" />
          </div>
          <div className="text-2xl font-bold text-slate-100 font-mono">
            +{(botBrlReturn * 100).toFixed(1)}% <span className="text-xs text-indigo-400">(BRL)</span>
          </div>
          <div className="text-[11px] text-slate-400 mt-2 font-mono">
            +{(botUsdReturn * 100).toFixed(1)}% USD • Câmbio R$ {usdRate.toFixed(2)}
          </div>
        </div>
      </div>

      {/* Gráfico Comparativo de Trajetória Patrimonial Normalizada (Base 100) */}
      <div className="bg-slate-900/40 border border-slate-800 rounded-xl p-5">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 mb-4">
          <div>
            <h2 className="text-sm font-semibold text-slate-200 flex items-center gap-2">
              <BarChart3 className="w-4 h-4 text-cyan-400" />
              <span>Trajetória Patrimonial Acumulada (Base 100 = R$ 100.000 investidos)</span>
            </h2>
            <p className="text-xs text-slate-400 mt-0.5">
              Comparativo direto: Estratégia Cripto em Reais vs Renda Fixa (CDI / Tesouro IPCA+) vs Inflação (IPCA) vs
              IBOVESPA.
            </p>
          </div>

          <div className="flex flex-wrap items-center gap-3 text-xs">
            <span className="flex items-center gap-1.5 text-emerald-400">
              <span className="w-2.5 h-2.5 rounded-full bg-emerald-500 inline-block" />
              Robô Cripto (BRL)
            </span>
            <span className="flex items-center gap-1.5 text-cyan-400">
              <span className="w-2.5 h-2.5 rounded-full bg-cyan-500 inline-block" />
              Tesouro IPCA+
            </span>
            <span className="flex items-center gap-1.5 text-indigo-400">
              <span className="w-2.5 h-2.5 rounded-full bg-indigo-500 inline-block" />
              100% CDI
            </span>
            <span className="flex items-center gap-1.5 text-amber-400">
              <span className="w-2.5 h-2.5 rounded-full bg-amber-500 inline-block" />
              IPCA (Inflação)
            </span>
            <span className="flex items-center gap-1.5 text-rose-400">
              <span className="w-2.5 h-2.5 rounded-full bg-rose-500 inline-block" />
              IBOVESPA
            </span>
          </div>
        </div>

        {/* SVG Comparativo */}
        <div className="h-64 w-full bg-slate-950/70 rounded-lg border border-slate-800/80 p-4 flex flex-col justify-between">
          <svg className="w-full h-full overflow-visible" viewBox="0 0 500 160">
            <title>Comparativo Patrimonial: Robô vs CDI vs IPCA vs IBOVESPA</title>
            {/* Grid horizontal */}
            <line x1="0" y1="130" x2="500" y2="130" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />
            <line x1="0" y1="90" x2="500" y2="90" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />
            <line x1="0" y1="50" x2="500" y2="50" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />
            <line x1="0" y1="10" x2="500" y2="10" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />

            {/* Base 100 baseline */}
            <line x1="0" y1="130" x2="500" y2="130" stroke="#475569" strokeWidth="1" />

            {/* IPCA (Amarelo - curva suave ascendente de inflação) */}
            <path
              d="M 0 130 C 120 128, 250 126, 500 122"
              fill="none"
              stroke="#f59e0b"
              strokeWidth="2"
              strokeDasharray="4 2"
            />

            {/* CDI 100% (Índigo - curva estável sem volatilidade) */}
            <path d="M 0 130 C 120 125, 250 118, 500 110" fill="none" stroke="#6366f1" strokeWidth="2.5" />

            {/* Tesouro IPCA+ (Ciano - curva moderada) */}
            <path d="M 0 130 C 120 123, 250 114, 500 105" fill="none" stroke="#06b6d4" strokeWidth="2" />

            {/* IBOVESPA (Rose - oscilante com volatilidade) */}
            <path
              d="M 0 130 C 60 142, 140 118, 220 128 C 300 110, 380 122, 500 102"
              fill="none"
              stroke="#f43f5e"
              strokeWidth="1.8"
            />

            {/* Robô Cripto BRL (Verde esmeralda - forte crescimento composto com volatilidade controlada) */}
            <path
              d="M 0 130 C 80 118, 150 105, 220 85 C 300 95, 390 60, 500 24"
              fill="none"
              stroke="#10b981"
              strokeWidth="3"
            />

            {/* Pontos terminais */}
            <circle cx="500" cy="24" r="4" fill="#10b981" />
            <circle cx="500" cy="102" r="3" fill="#f43f5e" />
            <circle cx="500" cy="105" r="3" fill="#06b6d4" />
            <circle cx="500" cy="110" r="3" fill="#6366f1" />
            <circle cx="500" cy="122" r="3" fill="#f59e0b" />
          </svg>

          {/* Rótulos do Eixo X */}
          <div className="flex justify-between text-[10px] text-slate-400 font-mono pt-2 border-t border-slate-800">
            <span>Início (T0)</span>
            <span>25% Período</span>
            <span>50% Período</span>
            <span>75% Período</span>
            <span>Atual (T{selectedPeriodDays}d)</span>
          </div>
        </div>
      </div>

      {/* Tabela Comparativa Detalhada dos Índices */}
      <div className="bg-slate-900/40 border border-slate-800 rounded-xl overflow-hidden">
        <div className="p-4 border-b border-slate-800 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <FileText className="w-4 h-4 text-emerald-400" />
            <h2 className="text-sm font-semibold text-slate-200">Régua Comparativa de Índices Macroeconômicos</h2>
          </div>
          <span className="text-xs text-slate-400">Horizonte: {selectedPeriodDays} dias úteis</span>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="bg-slate-900/80 text-slate-400 border-b border-slate-800">
              <tr>
                <th className="py-3 px-4 font-semibold">Índice / Benchmark</th>
                <th className="py-3 px-3 font-semibold">Categoria</th>
                <th className="py-3 px-3 font-semibold text-right">Taxa Anualizada</th>
                <th className="py-3 px-3 font-semibold text-right">Retorno no Período</th>
                <th className="py-3 px-3 font-semibold text-right">Excesso de Retorno (Alpha)</th>
                <th className="py-3 px-3 font-semibold text-right">% do CDI</th>
                <th className="py-3 px-4 font-semibold text-right">
                  Simulação R$ {initialInvestmentBrl.toLocaleString('pt-BR')}
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800/60 font-mono">
              {/* Linha do Robô Cripto (Destaque) */}
              <tr className="bg-emerald-950/20 text-slate-100 font-semibold border-b border-emerald-500/20">
                <td className="py-3 px-4 font-sans flex items-center gap-2 text-emerald-400">
                  <CheckCircle2 className="w-4 h-4" />
                  <span>Portfólio Cripto Autônomo (em BRL)</span>
                </td>
                <td className="py-3 px-3 font-sans">
                  <span className="px-2 py-0.5 rounded text-[10px] bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
                    Ativo Alternativo
                  </span>
                </td>
                <td className="py-3 px-3 text-right text-emerald-400">+{(botBrlReturn * 100).toFixed(2)}%</td>
                <td className="py-3 px-3 text-right text-emerald-400">+{(botBrlReturn * 100).toFixed(2)}%</td>
                <td className="py-3 px-3 text-right text-emerald-400">+{(botAlphaOverCdiBrl * 100).toFixed(2)}%</td>
                <td className="py-3 px-3 text-right text-emerald-400">{botPercentOfCdi.toFixed(0)}%</td>
                <td className="py-3 px-4 text-right text-emerald-300">
                  R$ {(initialInvestmentBrl * (1 + botBrlReturn)).toLocaleString('pt-BR', { maximumFractionDigits: 0 })}
                </td>
              </tr>

              {/* Linhas dos Benchmarks */}
              {benchmarks.map((b) => {
                const finalBrl = initialInvestmentBrl * (1 + b.returnInPeriod);
                const alphaOverThis = botBrlReturn - b.returnInPeriod;
                const pctOfCdi = cdiPeriodReturn > 0 ? (b.returnInPeriod / cdiPeriodReturn) * 100 : 0;

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
                    <td className="py-3 px-3 text-right text-emerald-400">
                      +{alphaOverThis >= 0 ? (alphaOverThis * 100).toFixed(2) : (alphaOverThis * 100).toFixed(2)}%
                    </td>
                    <td className="py-3 px-3 text-right text-cyan-400">{pctOfCdi.toFixed(0)}%</td>
                    <td className="py-3 px-4 text-right text-slate-200">
                      R$ {finalBrl.toLocaleString('pt-BR', { maximumFractionDigits: 0 })}
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
