import { useQuery } from '@tanstack/react-query';
import { Activity, Cpu, Flame, GitPullRequest, RefreshCw, Scale, ShieldAlert } from 'lucide-react';
import type React from 'react';
import { apiClient } from '../api/client';

export const AutonomyDashboardView: React.FC = () => {
  const regimeQuery = useQuery({
    queryKey: ['marketRegime', 'BTCUSDT'],
    queryFn: () => apiClient.getCurrentMarketRegime('BTCUSDT'),
    refetchInterval: 5000,
  });

  const allocationsQuery = useQuery({
    queryKey: ['strategyAllocations'],
    queryFn: apiClient.getStrategyAllocations,
    refetchInterval: 5000,
  });

  const driftsQuery = useQuery({
    queryKey: ['recentDrifts'],
    queryFn: () => apiClient.getRecentDrifts(undefined, 20),
    refetchInterval: 5000,
  });

  const modelsQuery = useQuery({
    queryKey: ['registeredModels'],
    queryFn: () => apiClient.getRegisteredModels('trend-following'),
    refetchInterval: 10000,
  });

  const isRefreshing =
    regimeQuery.isFetching || allocationsQuery.isFetching || driftsQuery.isFetching || modelsQuery.isFetching;

  const handleRefresh = () => {
    regimeQuery.refetch();
    allocationsQuery.refetch();
    driftsQuery.refetch();
    modelsQuery.refetch();
  };

  // Only display regime evidence returned by the API. Missing/unknown evidence blocks exposure.
  const regimeData = regimeQuery.data;
  const knownRegime = Boolean(
    regimeData &&
      ['BullTrend', 'BearTrend', 'MeanRevertingRange', 'HighVolatilityChop', 'IlliquidOrStale'].includes(
        regimeData.regime,
      ),
  );
  const isChopProhibited = !knownRegime || regimeData?.prohibitsNewExposure !== false;
  const regimeDisplayName = knownRegime ? regimeData?.regime : 'Indisponível (Unknown)';
  const confidencePct = knownRegime ? Math.round((regimeData?.confidence ?? 0) * 100) : 0;
  const trendSlopeRate =
    regimeData && knownRegime ? `${(regimeData.trendSlopeRate * 100).toFixed(2)}% / bar` : 'Indisponível';
  const volatilityRate =
    regimeData && knownRegime ? `${(regimeData.volatilityRate * 100).toFixed(2)}%` : 'Indisponível';
  const regimeReason = knownRegime
    ? regimeData?.reason
    : 'Sem evidência de regime; novas exposições permanecem bloqueadas.';

  const allocations = allocationsQuery.data || [];

  // Preserve API values and enum meaning; an unrecognized status is never reported as normal.
  const liveDrifts = driftsQuery.data || [];
  const driftMetrics = liveDrifts.map((d) => ({
    type: `${d.type} (${d.strategyId || 'global'})`,
    observed: Number.isFinite(d.observedValue) ? d.observedValue.toString() : 'Indisponível',
    threshold: Number.isFinite(d.allowedDeviationThreshold) ? d.allowedDeviationThreshold.toString() : 'Indisponível',
    status:
      d.severity === 'BreachedThreshold'
        ? 'Breached'
        : d.severity === 'ElevatedWarning'
          ? 'Warning'
          : d.severity === 'WithinTolerance'
            ? 'Normal'
            : 'Unknown',
  }));

  // Resolved Models
  const liveModels = modelsQuery.data || [];
  const registeredModels = liveModels.map((m) => ({
    modelId: m.modelId,
    strategyFamily: m.strategyId,
    version: m.version,
    status: m.status,
    datasetHash: m.datasetHash ? `${m.datasetHash.substring(0, 16)}...` : 'Indisponível',
  }));

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

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={handleRefresh}
            disabled={isRefreshing}
            className="flex items-center gap-2 px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium border border-slate-700 transition-colors disabled:opacity-50 cursor-pointer"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${isRefreshing ? 'animate-spin' : ''}`} />
            <span>Atualizar Telemetria</span>
          </button>

          {/* Current Regime Highlight */}
          <div className="flex items-center gap-3 bg-slate-950 px-4 py-2 rounded-lg border border-slate-800">
            <div
              className={`w-3 h-3 rounded-full ${isChopProhibited ? 'bg-rose-400 animate-ping' : 'bg-emerald-400 animate-ping'}`}
            />
            <div>
              <div className="text-[10px] text-slate-400 uppercase tracking-wider font-semibold">Regime Ativo</div>
              <div className={`text-sm font-bold font-mono ${isChopProhibited ? 'text-rose-400' : 'text-emerald-400'}`}>
                {regimeDisplayName} ({confidencePct}%)
              </div>
            </div>
          </div>
        </div>
      </div>

      {isChopProhibited && (
        <div className="bg-rose-950/40 border border-rose-800/60 rounded-xl p-4 flex items-start gap-3">
          <ShieldAlert className="w-5 h-5 text-rose-400 flex-shrink-0 mt-0.5" />
          <div className="text-xs text-rose-200/90 leading-relaxed">
            <strong className="text-rose-300 block mb-0.5">Novas exposições bloqueadas:</strong>Não há evidência de
            regime suficiente ou a política retornou bloqueio. A interface não autoriza entradas.
          </div>
        </div>
      )}

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
              <span className="font-mono text-emerald-400 font-bold">{trendSlopeRate}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-400">Coeficiente de Variação (Volatilidade):</span>
              <span className="font-mono text-slate-200">{volatilityRate}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-400">Bloqueio de novas exposições:</span>
              <span className={`font-mono font-bold ${isChopProhibited ? 'text-rose-400' : 'text-emerald-400'}`}>
                {isChopProhibited ? 'ATIVADA (NO_TRADE)' : 'DESATIVADA (API informou permissão)'}
              </span>
            </div>
          </div>

          <div className="text-xs text-slate-400 bg-slate-800/40 p-3 rounded border border-slate-700/50">
            <span className="font-semibold text-slate-300">Diagnóstico: </span>
            {regimeReason}
          </div>
        </div>

        {/* Dynamic Risk Budget & Meta-Allocation (US-173) */}
        <div className="lg:col-span-2 bg-slate-900 border border-slate-800 rounded-lg p-5">
          <h3 className="text-base font-semibold text-slate-200 mb-4 flex items-center gap-2">
            <Scale className="w-4 h-4 text-cyan-400" />
            Histórico de Recompensas por Estratégia e Regime
          </h3>

          <div className="space-y-4">
            {allocationsQuery.isError ? (
              <p className="text-xs text-rose-400">Histórico de recompensas indisponível.</p>
            ) : allocations.length === 0 ? (
              <p className="text-xs text-slate-400">Nenhum desempenho de estratégia registrado.</p>
            ) : (
              allocations.map((alloc) => (
                <div key={`${alloc.strategyId}-${alloc.regime}`} className="space-y-1.5">
                  <div className="flex justify-between text-xs">
                    <span className="font-semibold text-slate-200">
                      {alloc.strategyId} · {alloc.regime}
                    </span>
                    <span className="font-mono text-cyan-400 font-bold">
                      {alloc.observationCount} observações · recompensa acumulada {alloc.cumulativeReward} · média{' '}
                      {alloc.averageReward}
                    </span>
                  </div>
                </div>
              ))
            )}
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
                {driftsQuery.isError ? (
                  <tr>
                    <td colSpan={4} className="py-4 text-center text-rose-400">
                      Observações de drift indisponíveis.
                    </td>
                  </tr>
                ) : driftMetrics.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="py-4 text-center text-slate-400">
                      Nenhuma observação de drift registrada.
                    </td>
                  </tr>
                ) : (
                  driftMetrics.map((dm, idx) => (
                    <tr key={`${dm.type}-${idx}`} className="hover:bg-slate-800/30">
                      <td className="py-2.5 px-3 font-sans font-medium text-slate-200">{dm.type}</td>
                      <td className="py-2.5 px-3 text-slate-300">{dm.observed}</td>
                      <td className="py-2.5 px-3 text-slate-400">{dm.threshold}</td>
                      <td className="py-2.5 px-3">
                        <span
                          className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                            dm.status === 'Breached'
                              ? 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
                              : dm.status === 'Warning'
                                ? 'bg-amber-500/20 text-amber-400 border border-amber-500/30'
                                : dm.status === 'Normal'
                                  ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                                  : 'bg-slate-700 text-slate-300 border border-slate-600'
                          }`}
                        >
                          {dm.status}
                        </span>
                      </td>
                    </tr>
                  ))
                )}
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
                {modelsQuery.isError ? (
                  <tr>
                    <td colSpan={4} className="py-4 text-center text-rose-400">
                      Registro de modelos indisponível.
                    </td>
                  </tr>
                ) : registeredModels.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="py-4 text-center text-slate-400">
                      Nenhum modelo registrado.
                    </td>
                  </tr>
                ) : (
                  registeredModels.map((m) => (
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
                                : ['Quarantined', 'Superseded', 'Revoked'].includes(m.status)
                                  ? 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
                                  : 'bg-slate-700 text-slate-300 border border-slate-600'
                          }`}
                        >
                          {m.status}
                        </span>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </div>
  );
};
