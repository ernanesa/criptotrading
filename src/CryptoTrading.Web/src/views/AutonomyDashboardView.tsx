import { Activity, Cpu, Flame, GitPullRequest, Scale } from 'lucide-react';
import type React from 'react';

interface StrategyAllocation {
  id: string;
  name: string;
  weight: number;
  allocatedNotional: number;
  expectedUtility: number;
}

interface DriftMetric {
  type: string;
  observed: string;
  threshold: string;
  status: 'Normal' | 'Warning' | 'Breached';
}

interface ModelRegistryEntry {
  modelId: string;
  strategyFamily: string;
  version: string;
  status: 'Active' | 'Candidate' | 'Quarantined' | 'Retired';
  datasetHash: string;
  registeredAt: string;
}

export const AutonomyDashboardView: React.FC = () => {
  const currentRegime = {
    name: 'BullTrend',
    confidence: 0.88,
    trendSlopeRate: '+0.42% / bar',
    volatilityRate: '1.85%',
    reason: 'Regressão linear com inclinação positiva sustentada e fluxo de volume comprador.',
  };

  const allocations: StrategyAllocation[] = [
    {
      id: 'trend-following-btcusdt',
      name: 'Trend Following EMA Cross',
      weight: 0.55,
      allocatedNotional: 5500.0,
      expectedUtility: 1.82,
    },
    {
      id: 'breakout-momentum-btcusdt',
      name: 'Donchian Breakout Momentum',
      weight: 0.3,
      allocatedNotional: 3000.0,
      expectedUtility: 1.64,
    },
    {
      id: 'mean-reversion-btcusdt',
      name: 'Mean Reversion Z-Score',
      weight: 0.15,
      allocatedNotional: 1500.0,
      expectedUtility: 0.95,
    },
    {
      id: 'adaptive-grid-btcusdt',
      name: 'Adaptive Geometric Grid',
      weight: 0.0,
      allocatedNotional: 0.0,
      expectedUtility: 0.2,
    },
  ];

  const driftMetrics: DriftMetric[] = [
    {
      type: 'Execution Slippage Drift (US-172)',
      observed: '2.4 bps',
      threshold: '5.0 bps',
      status: 'Normal',
    },
    {
      type: 'Realized vs Expected Return (US-174)',
      observed: '-0.8%',
      threshold: '2.5%',
      status: 'Normal',
    },
    {
      type: 'Volatility Regime Drift (US-174)',
      observed: '2.1%',
      threshold: '5.0%',
      status: 'Normal',
    },
  ];

  const registeredModels: ModelRegistryEntry[] = [
    {
      modelId: 'mdl-trend-prod-v1.2',
      strategyFamily: 'Trend Following',
      version: '1.2.0',
      status: 'Active',
      datasetHash: 'e3b0c44298fc1c14...ca495991b7852b855',
      registeredAt: '2026-09-20 14:00 UTC',
    },
    {
      modelId: 'mdl-trend-challenger-v1.3',
      strategyFamily: 'Trend Following',
      version: '1.3.0',
      status: 'Candidate',
      datasetHash: '7f83b1657ff1fc53...bf09e46a782436f56',
      registeredAt: '2026-09-21 18:30 UTC',
    },
    {
      modelId: 'mdl-reversion-base-v1.0',
      strategyFamily: 'Mean Reversion',
      version: '1.0.0',
      status: 'Active',
      datasetHash: 'b45cffe084dd3d20...194c653a060cc8784',
      registeredAt: '2026-09-18 10:15 UTC',
    },
    {
      modelId: 'mdl-grid-experimental-v0.9',
      strategyFamily: 'Adaptive Grid',
      version: '0.9.1',
      status: 'Quarantined',
      datasetHash: 'a591a6d40bf42040...c26e10e786b402092',
      registeredAt: '2026-09-15 08:00 UTC',
    },
  ];

  return (
    <div className="space-y-6" data-testid="autonomy-dashboard-view">
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between bg-slate-900 border border-slate-800 rounded-lg p-5 gap-4">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-lg bg-emerald-500/10 border border-emerald-500/30 flex items-center justify-center text-emerald-400">
            <Cpu className="w-6 h-6" />
          </div>
          <div>
            <h2 className="text-xl font-bold tracking-tight text-slate-100">
              Controle de Autonomia & Multi-Estratégia
            </h2>
            <p className="text-xs text-slate-400 mt-0.5">
              Classificação contínua de regime de mercado, alocação dinâmica e ciclo lento de aprendizado (US-074,
              US-173, US-177)
            </p>
          </div>
        </div>

        {/* Current Regime Highlight */}
        <div className="flex items-center gap-3 bg-slate-950 px-4 py-2 rounded-lg border border-slate-800">
          <div className="w-3 h-3 rounded-full bg-emerald-400 animate-ping" />
          <div>
            <div className="text-[10px] text-slate-400 uppercase tracking-wider font-semibold">Regime Ativo</div>
            <div className="text-sm font-bold text-emerald-400 font-mono">
              BULL TREND ({currentRegime.confidence * 100}%)
            </div>
          </div>
        </div>
      </div>

      {/* Grid: Regime details and Meta-Allocation */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Market Regime Card */}
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-5 space-y-4">
          <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
            <Activity className="w-4 h-4 text-emerald-400" />
            Classificador de Regime de Mercado
          </h3>

          <div className="p-3 bg-slate-950/60 rounded border border-slate-800 space-y-2 text-xs">
            <div className="flex justify-between">
              <span className="text-slate-400">Inclinação da Linha de Tendência:</span>
              <span className="font-mono text-emerald-400 font-bold">{currentRegime.trendSlopeRate}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-400">Coeficiente de Variação (Volatilidade):</span>
              <span className="font-mono text-slate-200">{currentRegime.volatilityRate}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-400">Trava de High Volatility Chop:</span>
              <span className="font-mono text-emerald-400 font-bold">DESATIVADA (Normal)</span>
            </div>
          </div>

          <div className="text-xs text-slate-400 bg-slate-800/40 p-3 rounded border border-slate-700/50">
            <span className="font-semibold text-slate-300">Diagnóstico: </span>
            {currentRegime.reason}
          </div>
        </div>

        {/* Dynamic Risk Budget & Meta-Allocation (US-173) */}
        <div className="lg:col-span-2 bg-slate-900 border border-slate-800 rounded-lg p-5">
          <h3 className="text-base font-semibold text-slate-200 mb-4 flex items-center gap-2">
            <Scale className="w-4 h-4 text-cyan-400" />
            Alocação Dinâmica de Capital por Estratégia (Bandit Bayesiano)
          </h3>

          <div className="space-y-4">
            {allocations.map((alloc) => (
              <div key={alloc.id} className="space-y-1.5">
                <div className="flex justify-between text-xs">
                  <span className="font-semibold text-slate-200">{alloc.name}</span>
                  <span className="font-mono text-cyan-400 font-bold">
                    {(alloc.weight * 100).toFixed(0)}% (${alloc.allocatedNotional.toFixed(0)})
                  </span>
                </div>
                <div className="w-full bg-slate-950 rounded-full h-2 overflow-hidden border border-slate-800">
                  <div
                    className="bg-gradient-to-r from-cyan-500 to-indigo-500 h-full rounded-full transition-all duration-300"
                    style={{ width: `${alloc.weight * 100}%` }}
                  />
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>

      {/* Drift Monitoring & Model Strategy Registry */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Drift Monitoring Engine (US-174) */}
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-5 space-y-4">
          <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
            <Flame className="w-4 h-4 text-amber-400" />
            Monitoramento Contínuo de Drift de Execução & Retorno
          </h3>

          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                <tr>
                  <th className="py-2 px-3">Métrica de Drift</th>
                  <th className="py-2 px-3">Observado</th>
                  <th className="py-2 px-3">Limiar Máximo</th>
                  <th className="py-2 px-3">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-800 font-mono">
                {driftMetrics.map((dm) => (
                  <tr key={dm.type} className="hover:bg-slate-800/30">
                    <td className="py-2.5 px-3 font-sans font-medium text-slate-200">{dm.type}</td>
                    <td className="py-2.5 px-3 text-slate-300">{dm.observed}</td>
                    <td className="py-2.5 px-3 text-slate-400">{dm.threshold}</td>
                    <td className="py-2.5 px-3">
                      <span className="px-1.5 py-0.5 rounded text-[10px] font-bold bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
                        {dm.status}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>

        {/* Model Strategy Registry (US-170, US-175) */}
        <div className="bg-slate-900 border border-slate-800 rounded-lg p-5 space-y-4">
          <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
            <GitPullRequest className="w-4 h-4 text-indigo-400" />
            Model Strategy Registry & Champion-Challenger
          </h3>

          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                <tr>
                  <th className="py-2 px-3">Model ID</th>
                  <th className="py-2 px-3">Versão</th>
                  <th className="py-2 px-3">Linhagem Dataset</th>
                  <th className="py-2 px-3">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-800 font-mono">
                {registeredModels.map((m) => (
                  <tr key={m.modelId} className="hover:bg-slate-800/30">
                    <td className="py-2.5 px-3 font-medium text-indigo-300">{m.modelId}</td>
                    <td className="py-2.5 px-3 text-slate-300">v{m.version}</td>
                    <td className="py-2.5 px-3 text-slate-400">{m.datasetHash}</td>
                    <td className="py-2.5 px-3">
                      <span
                        className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                          m.status === 'Active'
                            ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                            : m.status === 'Candidate'
                              ? 'bg-cyan-500/20 text-cyan-400 border border-cyan-500/30'
                              : 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
                        }`}
                      >
                        {m.status}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </div>
  );
};
