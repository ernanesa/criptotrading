import { BarChart3, Compass } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';

const STRATEGIES = [
  { id: 'trend', name: 'Trend Following EMA Cross', family: 'Trend Following' },
  { id: 'breakout', name: 'Donchian Channel Breakout', family: 'Breakout / Momentum' },
  { id: 'reversion', name: 'Z-Score Mean Reversion & Regime Guard', family: 'Mean Reversion' },
  { id: 'grid', name: 'Adaptive Geometric Grid with Volatility Sizing', family: 'Adaptive Grid' },
] as const;

const BENCHMARKS = [
  { id: 'btc', name: 'Bitcoin Buy & Hold (USD)' },
  { id: 'selic', name: 'CDI / Taxa SELIC' },
  { id: 'tesouro', name: 'Tesouro IPCA+' },
  { id: 'ipca', name: 'Inflação Oficial (IPCA)' },
  { id: 'ibov', name: 'IBOVESPA B3' },
] as const;

export const ResearchDashboardView: React.FC = () => {
  const [selectedStrategyId, setSelectedStrategyId] = useState<string>('trend');
  const [selectedBenchmarkId, setSelectedBenchmarkId] = useState<string>('selic');

  const selectedStrategy = STRATEGIES.find(({ id }) => id === selectedStrategyId) ?? STRATEGIES[0];
  const selectedBenchmark = BENCHMARKS.find(({ id }) => id === selectedBenchmarkId) ?? BENCHMARKS[0];

  return (
    <div className="space-y-6" data-testid="research-dashboard-view">
      <div className="flex flex-col gap-4 rounded-lg border border-slate-800 bg-slate-900 p-5 md:flex-row md:items-end md:justify-between">
        <div className="flex items-center gap-4">
          <div className="flex h-12 w-12 items-center justify-center rounded-lg border border-indigo-500/30 bg-indigo-500/10 text-indigo-400">
            <Compass className="h-6 w-6" />
          </div>
          <div>
            <h2 className="text-xl font-bold tracking-tight text-slate-100">Pesquisa Quantitativa</h2>
            <p className="mt-0.5 text-xs text-slate-400">
              Selecione um candidato e um benchmark para consultar evidências disponíveis.
            </p>
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-2">
          <label className="text-xs text-slate-400">
            Estratégia
            <select
              aria-label="Estratégia"
              value={selectedStrategyId}
              onChange={(event) => setSelectedStrategyId(event.target.value)}
              className="mt-1 block w-full rounded border border-slate-700 bg-slate-950 px-3 py-2 text-slate-200"
            >
              {STRATEGIES.map((strategy) => (
                <option key={strategy.id} value={strategy.id}>
                  {strategy.family}
                </option>
              ))}
            </select>
          </label>

          <label className="text-xs text-slate-400">
            Benchmark
            <select
              aria-label="Benchmark"
              value={selectedBenchmarkId}
              onChange={(event) => setSelectedBenchmarkId(event.target.value)}
              className="mt-1 block w-full rounded border border-slate-700 bg-slate-950 px-3 py-2 text-slate-200"
            >
              {BENCHMARKS.map((benchmark) => (
                <option key={benchmark.id} value={benchmark.id}>
                  {benchmark.name}
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <section
        aria-live="polite"
        className="rounded-xl border border-amber-800/60 bg-amber-950/30 p-6"
        data-testid="research-evidence-unavailable"
      >
        <div className="flex items-start gap-3">
          <BarChart3 className="mt-0.5 h-5 w-5 shrink-0 text-amber-400" />
          <div className="space-y-2">
            <h3 className="text-sm font-semibold text-amber-200">Evidência de pesquisa indisponível</h3>
            <p className="text-sm text-slate-300">
              Não há artefato OOS/backtest verificado carregado para a estratégia selecionada. Os resultados e as séries
              do benchmark não foram acessados nesta tela.
            </p>
            <p className="text-xs text-slate-400">
              Métricas, curvas de patrimônio, lucro e comparação: <strong>Indisponível</strong>.
            </p>
            <div className="flex flex-wrap gap-x-5 gap-y-1 pt-1 text-xs text-slate-400">
              <span>Estratégia selecionada: {selectedStrategy.name}</span>
              <span>Benchmark selecionado: {selectedBenchmark.name}</span>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
};
