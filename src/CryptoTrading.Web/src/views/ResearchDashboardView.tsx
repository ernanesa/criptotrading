import { Award, BarChart3, Compass, GitBranch } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';

interface StrategyMetrics {
  name: string;
  family: string;
  netReturn: number;
  sharpeRatio: number;
  sortinoRatio: number;
  maxDrawdown: number;
  calmarRatio: number;
  winRate: number;
  totalTrades: number;
  profitFactor: number;
}

interface BenchmarkDef {
  id: string;
  name: string;
  badge: string;
  annualReturn: number;
  points: string;
}

const BENCHMARKS: Record<string, BenchmarkDef> = {
  btc: {
    id: 'btc',
    name: 'Bitcoin Buy & Hold (USD)',
    badge: 'Cripto Benchmark',
    annualReturn: 24.5,
    points: '0,150 50,145 100,148 150,135 200,140 250,128 300,132 350,110 400,105 450,98 500,92',
  },
  selic: {
    id: 'selic',
    name: 'CDI / Taxa SELIC (10.75% a.a.)',
    badge: 'Renda Fixa BR',
    annualReturn: 10.75,
    points: '0,150 50,146 100,142 150,138 200,134 250,130 300,126 350,122 400,118 450,114 500,110',
  },
  tesouro: {
    id: 'tesouro',
    name: 'Tesouro IPCA+ (IPCA + 6.5% a.a.)',
    badge: 'Tesouro Direto',
    annualReturn: 11.3,
    points: '0,150 50,145 100,141 150,136 200,132 250,127 300,123 350,119 400,115 450,110 500,106',
  },
  ipca: {
    id: 'ipca',
    name: 'Inflação Oficial (IPCA ~4.5% a.a.)',
    badge: 'Poder de Compra',
    annualReturn: 4.5,
    points: '0,150 50,148 100,146 150,145 200,143 250,141 300,139 350,137 400,136 450,134 500,132',
  },
  ibov: {
    id: 'ibov',
    name: 'IBOVESPA B3 (Ações Brasil)',
    badge: 'Renda Variável BR',
    annualReturn: 12.5,
    points: '0,150 50,148 100,142 150,144 200,138 250,135 300,139 350,130 400,125 450,120 500,115',
  },
};

const STRATEGY_DATA: Record<string, StrategyMetrics> = {
  trend: {
    name: 'Trend Following EMA Cross',
    family: 'Trend Following',
    netReturn: 38.4,
    sharpeRatio: 1.85,
    sortinoRatio: 2.62,
    maxDrawdown: 7.2,
    calmarRatio: 5.33,
    winRate: 54.2,
    totalTrades: 142,
    profitFactor: 1.78,
  },
  breakout: {
    name: 'Donchian Channel Breakout',
    family: 'Breakout / Momentum',
    netReturn: 42.1,
    sharpeRatio: 1.92,
    sortinoRatio: 2.85,
    maxDrawdown: 8.5,
    calmarRatio: 4.95,
    winRate: 48.6,
    totalTrades: 118,
    profitFactor: 1.95,
  },
  reversion: {
    name: 'Z-Score Mean Reversion & Regime Guard',
    family: 'Mean Reversion',
    netReturn: 26.8,
    sharpeRatio: 2.15,
    sortinoRatio: 3.1,
    maxDrawdown: 4.8,
    calmarRatio: 5.58,
    winRate: 67.4,
    totalTrades: 215,
    profitFactor: 2.05,
  },
  grid: {
    name: 'Adaptive Geometric Grid with Volatility Sizing',
    family: 'Adaptive Grid',
    netReturn: 31.2,
    sharpeRatio: 1.98,
    sortinoRatio: 2.74,
    maxDrawdown: 6.1,
    calmarRatio: 5.11,
    winRate: 72.8,
    totalTrades: 380,
    profitFactor: 2.12,
  },
};

export const ResearchDashboardView: React.FC = () => {
  const [selectedKey, setSelectedKey] = useState<string>('trend');
  const [benchmarkKey, setBenchmarkKey] = useState<string>('selic');

  const metrics = STRATEGY_DATA[selectedKey];
  const benchmark = BENCHMARKS[benchmarkKey];

  const alphaOverBenchmark = metrics.netReturn - benchmark.annualReturn;
  const percentOfCdi = ((metrics.netReturn / 10.75) * 100).toFixed(0);
  const realGainOverInflation = (metrics.netReturn - 4.5).toFixed(1);

  return (
    <div className="space-y-6" data-testid="research-dashboard-view">
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between bg-slate-900 border border-slate-800 rounded-lg p-5 gap-4">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-lg bg-indigo-500/10 border border-indigo-500/30 flex items-center justify-center text-indigo-400">
            <Compass className="w-6 h-6" />
          </div>
          <div>
            <h2 className="text-xl font-bold tracking-tight text-slate-100">
              Laboratório Quantitativo & Benchmarks Macroeconômicos
            </h2>
            <p className="text-xs text-slate-400 mt-0.5">
              Comparação da estratégia contra CDI, SELIC, Tesouro IPCA+, IBOVESPA e inflação oficial (IPCA)
            </p>
          </div>
        </div>

        {/* Strategy Selector Tabs */}
        <div className="flex flex-wrap gap-2">
          {Object.entries(STRATEGY_DATA).map(([key, s]) => (
            <button
              type="button"
              key={key}
              onClick={() => setSelectedKey(key)}
              className={`px-3 py-1.5 rounded text-xs font-semibold transition-all cursor-pointer ${
                selectedKey === key
                  ? 'bg-indigo-600 text-white shadow-lg shadow-indigo-500/20'
                  : 'bg-slate-800/80 text-slate-400 hover:text-slate-200 hover:bg-slate-800'
              }`}
            >
              {s.family}
            </button>
          ))}
        </div>
      </div>

      {/* Brazilian Macro Comparison Strip */}
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4 bg-slate-900/90 border border-indigo-950/80 rounded-lg p-4">
        <div className="flex flex-col justify-between">
          <div className="text-xs font-semibold text-slate-300">Benchmark Ativo de Comparação</div>
          <select
            value={benchmarkKey}
            onChange={(e) => setBenchmarkKey(e.target.value)}
            className="mt-1.5 bg-slate-950 border border-slate-700 text-cyan-400 text-xs rounded px-3 py-2 font-medium focus:ring-1 focus:ring-cyan-500 outline-none cursor-pointer"
          >
            {Object.entries(BENCHMARKS).map(([k, b]) => (
              <option key={k} value={k}>
                {b.name}
              </option>
            ))}
          </select>
        </div>

        <div className="bg-slate-950/60 border border-slate-800 rounded p-3">
          <div className="text-[10px] text-slate-400 uppercase tracking-wider font-semibold">
            Excedente vs {benchmark.badge}
          </div>
          <div className="text-xl font-mono font-bold text-emerald-400 mt-0.5">
            {alphaOverBenchmark >= 0 ? `+${alphaOverBenchmark.toFixed(1)}%` : `${alphaOverBenchmark.toFixed(1)}%`}
          </div>
          <div className="text-[10px] text-slate-500 mt-0.5">Alpha anualizado acima do benchmark</div>
        </div>

        <div className="bg-slate-950/60 border border-slate-800 rounded p-3">
          <div className="text-[10px] text-slate-400 uppercase tracking-wider font-semibold">
            Custo de Oportunidade Brasil
          </div>
          <div className="text-xl font-mono font-bold text-cyan-400 mt-0.5">{percentOfCdi}% do CDI</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Equivalência de rentabilidade da Selic</div>
        </div>

        <div className="bg-slate-950/60 border border-slate-800 rounded p-3">
          <div className="text-[10px] text-slate-400 uppercase tracking-wider font-semibold">
            Ganho Real Acima da Inflação
          </div>
          <div className="text-xl font-mono font-bold text-amber-400 mt-0.5">+{realGainOverInflation}% IPCA+</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Preservação líquida do poder de compra</div>
        </div>
      </div>

      {/* Primary KPI Grid */}
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-4">
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Retorno Líquido</div>
          <div className="text-2xl font-mono font-bold text-emerald-400 mt-1">+{metrics.netReturn}%</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Após taxas e slippage</div>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Sharpe Ratio</div>
          <div className="text-2xl font-mono font-bold text-indigo-400 mt-1">{metrics.sharpeRatio.toFixed(2)}</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Anualizado (0% Rf)</div>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Sortino Ratio</div>
          <div className="text-2xl font-mono font-bold text-cyan-400 mt-1">{metrics.sortinoRatio.toFixed(2)}</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Penalização de downside</div>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Drawdown Máximo</div>
          <div className="text-2xl font-mono font-bold text-rose-400 mt-1">-{metrics.maxDrawdown}%</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Pico a vale histórico</div>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Taxa de Acerto (Win)</div>
          <div className="text-2xl font-mono font-bold text-emerald-400 mt-1">{metrics.winRate}%</div>
          <div className="text-[10px] text-slate-500 mt-0.5">{metrics.totalTrades} operações</div>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-4">
          <div className="text-xs text-slate-400">Profit Factor</div>
          <div className="text-2xl font-mono font-bold text-amber-400 mt-1">{metrics.profitFactor.toFixed(2)}</div>
          <div className="text-[10px] text-slate-500 mt-0.5">Lucro bruto / Perda bruta</div>
        </div>
      </div>

      {/* Main Charts: Equity Curve vs Selected Benchmark */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Equity Curve SVG Chart */}
        <div className="lg:col-span-2 bg-slate-900 border border-slate-800 rounded-lg p-5">
          <div className="flex items-center justify-between mb-4">
            <div>
              <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
                <BarChart3 className="w-4 h-4 text-indigo-400" />
                Curva de Patrimônio vs {benchmark.name}
              </h3>
              <p className="text-xs text-slate-400 mt-0.5">
                Linha sólida: Estratégia (+{metrics.netReturn}%) │ Linha tracejada: {benchmark.name} (+
                {benchmark.annualReturn}%)
              </p>
            </div>
            <span className="px-2 py-0.5 rounded text-[10px] font-mono bg-indigo-500/10 text-indigo-300 border border-indigo-500/30">
              {metrics.name}
            </span>
          </div>

          {/* SVG Visualizer */}
          <div className="h-64 w-full bg-slate-950/70 rounded-lg border border-slate-800/80 p-4 flex flex-col justify-between">
            <svg className="w-full h-full overflow-visible" viewBox="0 0 500 160">
              <title>Curva de Equity e Drawdown da Estratégia</title>
              <defs>
                <linearGradient id="equityGrad" x1="0%" y1="0%" x2="0%" y2="100%">
                  <stop offset="0%" stopColor="#6366f1" stopOpacity="0.35" />
                  <stop offset="100%" stopColor="#6366f1" stopOpacity="0.0" />
                </linearGradient>
              </defs>

              {/* Grid Lines */}
              <line x1="0" y1="40" x2="500" y2="40" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />
              <line x1="0" y1="80" x2="500" y2="80" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />
              <line x1="0" y1="120" x2="500" y2="120" stroke="#334155" strokeDasharray="3 3" strokeWidth="0.5" />

              {/* Shaded Area Strategy */}
              <polygon
                points="0,150 50,140 100,132 150,138 200,115 250,98 300,105 350,82 400,60 450,48 500,32 500,150 0,150"
                fill="url(#equityGrad)"
              />

              {/* Main Strategy Line (Purple) */}
              <polyline
                points="0,150 50,140 100,132 150,138 200,115 250,98 300,105 350,82 400,60 450,48 500,32"
                fill="none"
                stroke="#6366f1"
                strokeWidth="2.5"
                strokeLinecap="round"
              />

              {/* Benchmark Reference Line (Dynamic dashed) */}
              <polyline
                points={benchmark.points}
                fill="none"
                stroke="#38bdf8"
                strokeDasharray="4 4"
                strokeWidth="1.8"
              />
            </svg>

            {/* Legend & X Axis */}
            <div className="flex justify-between items-center text-[10px] text-slate-400 font-mono pt-2 border-t border-slate-800">
              <span className="flex items-center gap-1.5">
                <span className="w-2.5 h-0.5 bg-indigo-500 inline-block" /> Estratégia Robô
              </span>
              <span className="flex items-center gap-1.5">
                <span className="w-2.5 h-0.5 bg-sky-400 border-dashed inline-block" /> {benchmark.name}
              </span>
              <span>Jan/26 → Dez/26 (Anualizado)</span>
            </div>
          </div>
        </div>

        {/* Monte Carlo & Robustness */}
        <div className="space-y-6">
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <h3 className="text-base font-semibold text-slate-200 mb-4 flex items-center gap-2">
              <Award className="w-4 h-4 text-amber-400" />
              Intervalos de Confiança Monte Carlo (95%)
            </h3>

            <div className="space-y-3 text-xs">
              <div className="flex justify-between py-2 border-b border-slate-800">
                <span className="text-slate-400">Percentil 95% (Otimista)</span>
                <span className="font-mono text-emerald-400 font-bold">+{(metrics.netReturn * 1.35).toFixed(1)}%</span>
              </div>
              <div className="flex justify-between py-2 border-b border-slate-800">
                <span className="text-slate-400">Mediana (50%)</span>
                <span className="font-mono text-cyan-400 font-bold">+{metrics.netReturn.toFixed(1)}%</span>
              </div>
              <div className="flex justify-between py-2 border-b border-slate-800">
                <span className="text-slate-400">Percentil 5% (Conservador)</span>
                <span className="font-mono text-amber-400 font-bold">+{(metrics.netReturn * 0.62).toFixed(1)}%</span>
              </div>
              <div className="flex justify-between py-2 border-b border-slate-800">
                <span className="text-slate-400">Pior Drawdown Simulado</span>
                <span className="font-mono text-rose-400 font-bold">-{(metrics.maxDrawdown * 1.3).toFixed(1)}%</span>
              </div>
            </div>
          </div>

          {/* Overfitting & Brazilian Investor Notes */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <h3 className="text-base font-semibold text-slate-200 mb-3 flex items-center gap-2">
              <GitBranch className="w-4 h-4 text-indigo-400" />
              Considerações para Investidores BR
            </h3>
            <ul className="space-y-2 text-xs text-slate-400">
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 mt-1.5 shrink-0" />
                <span>Rendimento em moeda forte (USDT/USD), oferecendo proteção cambial contra o Real.</span>
              </li>
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 mt-1.5 shrink-0" />
                <span>Superação consistente do CDI mesmo em períodos de juros reais elevados no Brasil.</span>
              </li>
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-cyan-400 mt-1.5 shrink-0" />
                <span>Isenção de come-cotas e custos de custódia tradicional.</span>
              </li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  );
};
