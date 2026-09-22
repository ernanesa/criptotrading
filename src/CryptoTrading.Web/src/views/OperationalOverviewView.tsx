import { useQuery } from '@tanstack/react-query';
import { Activity, AlertCircle, CheckCircle2, Clock, Database, RefreshCw, ShieldAlert } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient } from '../api/client';
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

  const isRefreshing = statusQuery.isFetching || livenessQuery.isFetching || readinessQuery.isFetching;

  const handleManualRefresh = () => {
    statusQuery.refetch();
    livenessQuery.refetch();
    readinessQuery.refetch();
    checkpointQuery.refetch();
  };

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
            {statusQuery.data?.blocksNewEntries
              ? 'Novas exposições estão estritamente bloqueadas.'
              : 'Operação padrão sem restrições de emergência.'}
          </p>
        </div>

        {/* Liveness */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">API Liveness</span>
            <CheckCircle2 className="w-4 h-4 text-emerald-400" />
          </div>
          <div className="text-2xl font-bold text-slate-100 mb-2">
            {livenessQuery.isSuccess ? (
              <StatusBadge status="Healthy" size="lg" />
            ) : (
              <StatusBadge status="Unhealthy" size="lg" />
            )}
          </div>
          <p className="text-xs text-slate-400">Processo web respondendo a checagens locais de liveness.</p>
        </div>

        {/* Readiness */}
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between mb-3">
            <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Trading Readiness</span>
            {readinessQuery.isSuccess ? (
              <CheckCircle2 className="w-4 h-4 text-emerald-400" />
            ) : (
              <AlertCircle className="w-4 h-4 text-amber-400" />
            )}
          </div>
          <div className="text-2xl font-bold text-slate-100 mb-2">
            {readinessQuery.isSuccess ? (
              <StatusBadge status="Healthy" size="lg" />
            ) : (
              <StatusBadge status="Degraded" size="lg" />
            )}
          </div>
          <p className="text-xs text-slate-400">
            {readinessQuery.isSuccess
              ? 'Configuração segura de Testnet carregada e validada.'
              : 'Verificação de configuração pendente ou incompleta.'}
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
              new Date(checkpointQuery.data.lastProcessedCloseUtc).toISOString().replace('T', ' ').substring(0, 19) +
              ' UTC'
            ) : checkpointQuery.isLoading ? (
              <span className="text-slate-500">consultando...</span>
            ) : (
              <span className="text-slate-500 font-normal">Sem watermark recente</span>
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
                {new Date(statusQuery.data.changedAtUtc).toISOString()}
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
