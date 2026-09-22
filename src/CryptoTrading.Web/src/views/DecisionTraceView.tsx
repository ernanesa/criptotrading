import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiClient, ApiError } from '../api/client';
import { StatusBadge } from '../components/StatusBadge';
import { Search, CheckCircle2, XCircle, FileCode, AlertTriangle, ArrowRight } from 'lucide-react';

export const DecisionTraceView: React.FC = () => {
  const [decisionIdInput, setDecisionIdInput] = useState('');
  const [searchedId, setSearchedId] = useState('');

  const decisionQuery = useQuery({
    queryKey: ['decision', searchedId],
    queryFn: () => apiClient.getOperationalDecision(searchedId),
    enabled: searchedId.length > 0,
    retry: false,
  });

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    if (decisionIdInput.trim()) {
      setSearchedId(decisionIdInput.trim());
    }
  };

  const report = decisionQuery.data;
  const isNotFound = decisionQuery.error instanceof ApiError && decisionQuery.error.status === 404;
  const isUnavailable = decisionQuery.error instanceof ApiError && decisionQuery.error.status === 503;

  return (
    <div className="space-y-6" data-testid="decision-trace-view">
      <div>
        <h1 className="text-xl font-bold text-slate-100 flex items-center gap-2">
          <span>Rastreabilidade & Reconstrução de Decisões Autônomas</span>
        </h1>
        <p className="text-xs text-slate-400 mt-1">
          Inspeção forense e verificação de identidade canônica SHA-256 de decisões do ciclo autônomo (US-198).
        </p>
      </div>

      {/* Search Bar */}
      <form onSubmit={handleSearch} className="flex gap-2">
        <div className="relative flex-1">
          <Search className="absolute left-3.5 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
          <input
            type="text"
            value={decisionIdInput}
            onChange={(e) => setDecisionIdInput(e.target.value)}
            placeholder="Informe o DecisionId (ex: ad-...) para reconstruir a cadeia de evidências..."
            className="w-full bg-slate-900 border border-slate-800 rounded-xl pl-10 pr-4 py-2.5 text-xs font-mono text-slate-200 placeholder:text-slate-500 focus:outline-none focus:border-cyan-500 transition-colors"
          />
        </div>
        <button
          type="submit"
          disabled={!decisionIdInput.trim() || decisionQuery.isFetching}
          className="px-5 py-2.5 bg-cyan-600 hover:bg-cyan-500 disabled:opacity-50 text-white font-medium text-xs rounded-xl transition-colors cursor-pointer flex items-center gap-2"
        >
          <span>Buscar Decisão</span>
          <ArrowRight className="w-3.5 h-3.5" />
        </button>
      </form>

      {/* Loading state */}
      {decisionQuery.isFetching && (
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-12 text-center text-slate-400 text-xs font-mono animate-pulse">
          Reconstruindo evidências e recalculando digest canônico SHA-256 no backend...
        </div>
      )}

      {/* Error state: 503 or 404 */}
      {isUnavailable && (
        <div className="bg-amber-950/40 border border-amber-800/60 rounded-xl p-6 flex items-start gap-4">
          <AlertTriangle className="w-5 h-5 text-amber-400 flex-shrink-0 mt-0.5" />
          <div className="text-xs">
            <h3 className="font-bold text-amber-200 mb-1">Persistência Indisponível (503)</h3>
            <p className="text-amber-300/80">
              O banco de dados PostgreSQL não está conectado na API. Configure `POSTGRES_CONNECTION_STRING` para permitir consultas aos commits de decisão.
            </p>
          </div>
        </div>
      )}

      {isNotFound && (
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-8 text-center text-slate-400 text-xs">
          Nenhum commit de decisão encontrado para o ID <code className="text-cyan-400 font-mono">"{searchedId}"</code>.
        </div>
      )}

      {/* Report Result Card */}
      {report && (
        <div className="space-y-6">
          {/* Header Card with Audit Verification Badge */}
          <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-4 mb-4 pb-4 border-b border-slate-800">
              <div>
                <span className="text-xs font-mono text-slate-400 block">ID da Decisão:</span>
                <span className="text-sm font-mono font-bold text-slate-100 break-all">{report.commit.decisionId}</span>
              </div>

              <div className="flex items-center gap-3">
                {report.canonicalIdentityHashMatches ? (
                  <div className="flex items-center gap-1.5 px-3 py-1 rounded-full bg-emerald-950/90 text-emerald-300 border border-emerald-700/60 font-mono text-xs">
                    <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                    <span>SHA-256 CANÔNICO VÁLIDO</span>
                  </div>
                ) : (
                  <div className="flex items-center gap-1.5 px-3 py-1 rounded-full bg-rose-950/90 text-rose-300 border border-rose-700/60 font-mono text-xs">
                    <XCircle className="w-4 h-4 text-rose-400" />
                    <span>HASH CANÔNICO ADULTERADO</span>
                  </div>
                )}

                <StatusBadge status={report.isAuditVerified ? 'Verified' : 'Tampered'} size="md" />
              </div>
            </div>

            {/* Commit Details Grid */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs font-mono">
              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Resultado (Outcome):</span>
                <span
                  className={`font-bold ${
                    report.commit.outcome === 'NO_TRADE'
                      ? 'text-amber-400'
                      : report.commit.outcome === 'BLOCKED'
                      ? 'text-rose-400'
                      : 'text-emerald-400'
                  }`}
                >
                  {report.commit.outcome}
                </span>
                {report.commit.outcomeReason && (
                  <span className="text-slate-400 block mt-0.5 text-[11px]">({report.commit.outcomeReason})</span>
                )}
              </div>

              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Sinal & Estratégia:</span>
                <span className="text-slate-200 font-bold">
                  {report.commit.strategy} v{report.commit.strategyVersion}
                </span>
                <span className="text-cyan-400 block mt-0.5 text-[11px]">Sinal: {report.commit.signal}</span>
              </div>

              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Símbolo & Escopo:</span>
                <span className="text-slate-200 font-bold">{report.commit.symbol}</span>
                <span className="text-slate-400 block mt-0.5 text-[11px]">{report.commit.scope}</span>
              </div>

              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Fechamento Avaliado:</span>
                <span className="text-slate-300">{new Date(report.commit.evaluatedCloseUtc).toISOString()}</span>
              </div>

              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Instante da Decisão UTC:</span>
                <span className="text-slate-300">{new Date(report.commit.decisionUtc).toISOString()}</span>
              </div>

              <div className="p-3 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Política de Risco:</span>
                <span className="text-slate-300">v{report.commit.riskPolicyVersion}</span>
              </div>
            </div>

            {/* Feature Snapshot SHA-256 */}
            <div className="mt-4 p-3 bg-slate-950 rounded-lg border border-slate-800 text-xs font-mono">
              <span className="text-slate-500 block mb-1">Digest de Features (SHA-256):</span>
              <span className="text-cyan-400 break-all">{report.commit.featureSnapshotSha256}</span>
            </div>
          </div>

          {/* Decision Trace JSON */}
          <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
            <h3 className="text-xs font-bold uppercase tracking-wider text-slate-400 mb-3 flex items-center gap-2">
              <FileCode className="w-4 h-4 text-cyan-400" />
              <span>Trace Estruturado da Decisão (JSON)</span>
            </h3>
            <pre className="p-4 bg-slate-950 rounded-lg border border-slate-800 text-[11px] font-mono text-slate-300 overflow-x-auto max-h-64">
              {JSON.stringify(JSON.parse(report.commit.decisionTraceJson || '{}'), null, 2)}
            </pre>
          </div>
        </div>
      )}
    </div>
  );
};
