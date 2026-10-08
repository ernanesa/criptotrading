import { useQuery } from '@tanstack/react-query';
import { Activity, AlertCircle, CheckCircle2, Clock, Database, RefreshCw, ShieldAlert } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient, formatIsoDate, formatUtcTimestamp } from '../api/client';
import { StatusBadge } from '../components/StatusBadge';

export const OperationalOverviewView: React.FC = () => {
  const [scope, setScope] = useState('BTCUSDT');

  const statusQuery = useQuery({
    queryKey: ['operationalStatus'],
    queryFn: apiClient.getOperationalStatus,
    refetchInterval: 5000,
  });

  const livenessQuery = useQuery({
    queryKey: ['healthLive'],
    queryFn: apiClient.getHealthLive,
    refetchInterval: 10000,
  });

  const readinessQuery = useQuery({
    queryKey: ['healthReady'],
    queryFn: apiClient.getHealthReady,
    refetchInterval: 10000,
  });

  const checkpointQuery = useQuery({
    queryKey: ['checkpoint', scope],
    queryFn: () => apiClient.getAutonomyCheckpoint(scope),
    refetchInterval: 10000,
    retry: false,
  });

  const telemetryQuery = useQuery({
    queryKey: ['telemetryMetrics'],
    queryFn: apiClient.getTelemetryMetrics,
    refetchInterval: 5000,
  });

  const balancesQuery = useQuery({
    queryKey: ['portfolioBalances'],
    queryFn: apiClient.getPortfolioBalances,
    refetchInterval: 5000,
  });

  const isRefreshing =
    statusQuery.isFetching || livenessQuery.isFetching || readinessQuery.isFetching || telemetryQuery.isFetching;

  const handleManualRefresh = () => {
    statusQuery.refetch();
    livenessQuery.refetch();
    readinessQuery.refetch();
    checkpointQuery.refetch();
    telemetryQuery.refetch();
    balancesQuery.refetch();
  };

  const telemetry = telemetryQuery.data;
  const unavailable = 'Indisponível';
  const apiErrorRate =
    telemetry && telemetry.apiRequests > 0 && telemetry.apiErrors !== undefined
      ? `${((telemetry.apiErrors / telemetry.apiRequests) * 100).toFixed(1)}%`
      : unavailable;

  return (
    <div className="space-y-6" data-testid="operational-overview-view">
      {/* Top action row */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-bold text-slate-100 flex items-center gap-2">
            <span>Console de Saúde & Operação do Runtime</span>
          </h1>
          <p className="text-xs text-slate-400 mt-1">
            Monitoramento de alta frequência do supervisor de processo, integridade de readiness e checkpoints duráveis.
          </p>
        </div>

        <button
          type="button"
          onClick={handleManualRefresh}
          disabled={isRefreshing}
          className="flex items-center gap-2 px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium border border-slate-700 transition-colors disabled:opacity-50 cursor-pointer"
        >
          <RefreshCw className={`w-3.5 h-3.5 ${isRefreshing ? 'animate-spin' : ''}`} />
          <span>Atualizar Agora</span>
        </button>
      </div>

      {/* Cards Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Supervisor Status */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Supervisor</span>
            <Activity className="w-4 h-4 text-cyan-400" />
          </div>
          <div className="text-2xl font-bold text-slate-100 mb-2">
            {statusQuery.data ? (
              <StatusBadge status={statusQuery.data.mode} size="lg" />
            ) : statusQuery.isLoading ? (
              <span className="text-sm text-slate-500">carregando...</span>
            ) : (
              <span className="text-sm text-rose-400">Offline / Erro</span>
            )}
          </div>
          <p className="text-xs text-slate-400">
            {statusQuery.data
              ? statusQuery.data.blocksNewEntries
                ? 'A API reporta bloqueio de novas exposições.'
                : 'A API não reporta bloqueio de novas exposições.'
              : 'Estado de bloqueio indisponível.'}
          </p>
        </div>

        {/* Liveness */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">API Liveness</span>
            {livenessQuery.data?.status === 'Healthy' ? (
              <CheckCircle2 className="w-4 h-4 text-emerald-400" />
            ) : (
              <AlertCircle className="w-4 h-4 text-rose-400" />
            )}
          </div>
          <div className="text-2xl font-bold text-slate-100 mb-2">
            <StatusBadge status={livenessQuery.data?.status || 'Unknown'} size="lg" />
          </div>
          <p className="text-xs text-slate-400">
            {livenessQuery.data?.status
              ? `Status reportado pela checagem: ${livenessQuery.data.status}.`
              : 'Status de liveness indisponível.'}
          </p>
        </div>

        {/* Readiness */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Trading Readiness</span>
            {readinessQuery.data?.status === 'Healthy' ? (
              <CheckCircle2 className="w-4 h-4 text-emerald-400" />
            ) : (
              <AlertCircle className="w-4 h-4 text-amber-400" />
            )}
          </div>
          <div className="text-2xl font-bold text-slate-100 mb-2">
            <StatusBadge status={readinessQuery.data?.status || 'Unknown'} size="lg" />
          </div>
          <p className="text-xs text-slate-400">
            {readinessQuery.data?.status === 'Healthy'
              ? 'A API reportou readiness Healthy.'
              : readinessQuery.data?.status
                ? `A API reportou readiness ${readinessQuery.data.status}.`
                : 'Readiness indisponível; estado não confirmado.'}
          </p>
        </div>

        {/* Checkpoint Watermark */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Último Checkpoint</span>
            <Clock className="w-4 h-4 text-indigo-400" />
          </div>
          <div className="text-sm font-mono font-bold text-slate-200 mb-2 break-all">
            {checkpointQuery.data?.lastProcessedCloseUtc ? (
              formatUtcTimestamp(checkpointQuery.data.lastProcessedCloseUtc)
            ) : checkpointQuery.isLoading ? (
              <span className="text-slate-500">consultando...</span>
            ) : checkpointQuery.isError ? (
              <span className="text-rose-400 font-normal">Checkpoint indisponível</span>
            ) : (
              <span className="text-slate-500 font-normal">A API não reportou watermark</span>
            )}
          </div>
          <div className="flex items-center justify-between text-xs text-slate-400">
            <span>Escopo:</span>
            <input
              type="text"
              value={scope}
              onChange={(e) => setScope(e.target.value)}
              className="bg-slate-950 border border-slate-800 rounded px-2 py-0.5 text-xs font-mono text-cyan-400 w-24 text-right"
            />
          </div>
        </div>
      </div>

      {/* Telemetria e Conectividade do Runtime em Tempo Real */}
      <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mb-4">
          <div className="flex items-center gap-2">
            <Activity className="w-4 h-4 text-cyan-400" />
            <h2 className="text-sm font-bold text-slate-200 uppercase tracking-wider">
              Telemetria & Métricas do Runtime (OpenTelemetry / Prometheus)
            </h2>
          </div>
          <span className="text-xs text-slate-400 font-mono">
            NAV Real: ${balancesQuery.data?.totalNavUsd?.toLocaleString('en-US', { minimumFractionDigits: 2 }) ?? '...'}{' '}
            USD
          </span>
        </div>

        <div className="grid grid-cols-2 lg:grid-cols-5 gap-3 text-xs font-mono">
          {/* Total HTTP Requests */}
          <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
            <span className="text-slate-400 font-sans block mb-1">Requisições HTTP</span>
            <span className="text-slate-100 font-bold text-sm block">{telemetry?.apiRequests ?? unavailable}</span>
            <span className="text-cyan-400 text-[11px]">
              Duração: {telemetry?.apiDurationMilliseconds ?? unavailable}
              {telemetry?.apiDurationMilliseconds !== undefined ? ' ms' : ''}
            </span>
          </div>

          {/* Erros 5xx / Falhas */}
          <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
            <span className="text-slate-400 font-sans block mb-1">Erros de API</span>
            <span
              className={`font-bold text-sm block ${
                telemetry?.apiErrors === undefined
                  ? 'text-slate-400'
                  : telemetry.apiErrors === 0
                    ? 'text-emerald-400'
                    : 'text-rose-400'
              }`}
            >
              {telemetry?.apiErrors ?? unavailable}
            </span>
            <span className="text-slate-400 text-[11px]">Taxa de erro: {apiErrorRate}</span>
          </div>

          {/* Reconciliações */}
          <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
            <span className="text-slate-400 font-sans block mb-1">Reconciliações</span>
            <span className="text-cyan-400 font-bold text-sm block">
              {telemetry?.reconciliationRuns ?? unavailable}
            </span>
            <span className="text-slate-400 text-[11px]">
              Divergências: {telemetry?.reconciliationMismatches ?? unavailable}
            </span>
          </div>

          {/* Heartbeats do Worker */}
          <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
            <span className="text-slate-400 font-sans block mb-1">Heartbeats</span>
            <span className="text-slate-100 font-bold text-sm block">{telemetry?.workerHeartbeats ?? unavailable}</span>
            <span className="text-slate-400 text-[11px]">Contagem reportada pela telemetria</span>
          </div>

          {/* Outbox / Eventos */}
          <div className="p-3 bg-slate-950 rounded-lg border border-slate-800 col-span-2 lg:col-span-1">
            <span className="text-slate-400 font-sans block mb-1">Outbox Dispatches</span>
            <span className="text-purple-400 font-bold text-sm block">
              {telemetry?.outboxDispatches ?? unavailable}
            </span>
            <span className="text-slate-400 text-[11px]">
              Dead-letters: {telemetry?.outboxDeadLetters ?? unavailable}
            </span>
          </div>
        </div>
      </div>

      {/* Operational Details Card */}
      <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
        <h2 className="text-sm font-bold text-slate-200 uppercase tracking-wider mb-4 flex items-center gap-2">
          <Database className="w-4 h-4 text-cyan-400" />
          <span>Metadados do Processo Operacional</span>
        </h2>

        {statusQuery.data ? (
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs font-mono">
            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
              <span className="text-slate-500 block mb-1">Origem do Status:</span>
              <span className="text-slate-200 font-bold">{statusQuery.data.source}</span>
            </div>

            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
              <span className="text-slate-500 block mb-1">Revisão do Estado:</span>
              <span className="text-cyan-400 font-bold">#{statusQuery.data.revision}</span>
            </div>

            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
              <span className="text-slate-500 block mb-1">Endpoints Mutáveis:</span>
              <span className="text-emerald-400 font-bold">
                {statusQuery.data.mutatingEndpointsEnabled ? 'HABILITADOS' : 'DESABILITADOS (SEGURO)'}
              </span>
            </div>

            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800 md:col-span-2">
              <span className="text-slate-500 block mb-1">Última Transição UTC:</span>
              <span className="text-slate-300">
                {formatIsoDate(statusQuery.data.changedAtUtc)}
                {statusQuery.data.reason ? ` (${statusQuery.data.reason})` : ''}
              </span>
            </div>

            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
              <span className="text-slate-500 block mb-1">Autor da Transição:</span>
              <span className="text-slate-300">{statusQuery.data.changedBy || 'Sistema / Startup'}</span>
            </div>
          </div>
        ) : (
          <div className="p-8 text-center text-slate-500 text-xs italic">
            Aguardando conexão com a API local em 127.0.0.1...
          </div>
        )}
      </div>

      {/* Safety Notice Callout */}
      <div className="bg-cyan-950/30 border border-cyan-800/40 rounded-xl p-4 flex items-start gap-3">
        <ShieldAlert className="w-5 h-5 text-cyan-400 flex-shrink-0 mt-0.5" />
        <div className="text-xs text-cyan-200/90 leading-relaxed">
          <strong className="text-cyan-300 block mb-0.5">Invariante de Isolamento do Console Web:</strong>
          Esta interface atua como um visualizador de diagnóstico somente leitura para observabilidade e auditoria.
          Nenhuma ordem é submetida e nenhuma chave privada de exchange transita pelo navegador. O supervisor no Worker
          governa as transições de estado duravelmente no PostgreSQL.
        </div>
      </div>
    </div>
  );
};
