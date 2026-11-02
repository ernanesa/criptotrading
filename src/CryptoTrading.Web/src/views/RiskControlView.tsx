import { useQuery } from '@tanstack/react-query';
import { AlertTriangle, FileText, ShieldAlert, ShieldCheck } from 'lucide-react';
import type React from 'react';
import { useEffect, useRef, useState } from 'react';
import { ApiError, apiClient, formatIsoDate } from '../api/client';
import { QueryStatus } from '../components/QueryStatus';
import { StatusBadge } from '../components/StatusBadge';

const formatMoneyLimit = (value: number | null | undefined) => (value == null ? 'Indisponível' : `$${value} USDT`);

export const RiskControlView: React.FC = () => {
  const [showKillSwitchModal, setShowKillSwitchModal] = useState(false);
  const [interventionNotice, setInterventionNotice] = useState<string | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const opener = useRef<HTMLButtonElement>(null);
  const cancelButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (showKillSwitchModal) {
      dialog.current?.showModal();
      cancelButton.current?.focus();
    }
  }, [showKillSwitchModal]);

  const closeDialog = () => {
    dialog.current?.close();
    setShowKillSwitchModal(false);
    opener.current?.focus();
  };

  const riskPolicyQuery = useQuery({
    queryKey: ['activeRiskPolicy'],
    queryFn: ({ signal }) => apiClient.getActiveRiskPolicy({ signal }),
    retry: false,
  });

  const doc = riskPolicyQuery.data;
  const isUnavailable = riskPolicyQuery.error instanceof ApiError && riskPolicyQuery.error.status === 503;
  const isNotFound = riskPolicyQuery.error instanceof ApiError && riskPolicyQuery.error.status === 404;

  return (
    <div className="space-y-6" data-testid="risk-control-view">
      <div>
        <h1 className="text-xl font-bold text-slate-100 flex items-center gap-2">
          <span>Controle de Risco & Governança da RiskPolicy</span>
        </h1>
        <p className="text-xs text-slate-400 mt-1">
          Limites matemáticos rígidos, aprovações de dois fatores e barreiras operacionais do motor de risco
          (US-040..US-044).
        </p>
      </div>

      {/* Intervention confirmation notice */}
      {interventionNotice && (
        <div className="bg-rose-950/40 border border-rose-800/80 rounded-xl p-5 flex items-start justify-between gap-4">
          <div className="flex items-start gap-3">
            <ShieldAlert className="w-5 h-5 text-rose-400 flex-shrink-0 mt-0.5" />
            <div className="text-xs space-y-1">
              <strong className="block text-rose-300 font-bold">Nenhuma intervenção foi enviada</strong>
              <p className="text-slate-300 leading-relaxed">{interventionNotice}</p>
              <div className="p-2 bg-slate-950 rounded border border-rose-900/40 font-mono text-[11px] text-cyan-300 mt-2">
                $ ./scripts/ops-control.sh kill-switch --scope global
              </div>
            </div>
          </div>
          <button
            type="button"
            onClick={() => setInterventionNotice(null)}
            className="text-slate-400 hover:text-slate-200 text-xs px-2 py-1 bg-slate-900 rounded border border-slate-700"
          >
            Fechar
          </button>
        </div>
      )}

      {/* Persistence / Load alerts */}
      <QueryStatus label="RiskPolicy" query={riskPolicyQuery} />
      {isUnavailable && (
        <div className="bg-amber-950/40 border border-amber-800/60 rounded-xl p-5 flex items-start gap-3">
          <AlertTriangle className="w-5 h-5 text-amber-400 flex-shrink-0 mt-0.5" />
          <div className="text-xs text-amber-200">
            <strong className="block mb-0.5 text-amber-300">Consulta indisponível (503):</strong>A API não conseguiu
            atender à consulta da RiskPolicy. A causa precisa ser verificada nos diagnósticos do serviço.
          </div>
        </div>
      )}

      {isNotFound && (
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 text-center text-xs text-slate-400">
          Nenhuma política de risco ativa foi encontrada no registro durável para o instante corrente.
        </div>
      )}

      {/* Active Policy Details */}
      {doc && (
        <div className="space-y-6">
          <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-4 mb-4 pb-4 border-b border-slate-800">
              <div className="flex items-center gap-3">
                <ShieldCheck className="w-6 h-6 text-emerald-400" />
                <div>
                  <h2 className="text-sm font-bold text-slate-100">
                    RiskPolicy Document v{doc.policy?.version || 'Indisponível'}
                  </h2>
                  <span className="text-[11px] font-mono text-slate-400">
                    Hash: {(doc.documentHash || '').substring(0, 16)}...
                  </span>
                </div>
              </div>

              <div className="flex items-center gap-2">
                <StatusBadge status={doc.lifecycle} size="md" />
              </div>
            </div>

            {/* Hard Limits Grid */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs font-mono">
              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Max Order Notional:</span>
                <span className="text-lg font-bold text-slate-100">
                  {formatMoneyLimit(doc.policy?.maxOrderNotional)}
                </span>
                <span className="text-[11px] text-slate-500 block mt-1">Limite máximo por ordem individual</span>
              </div>

              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Max Exposure Notional:</span>
                <span className="text-lg font-bold text-slate-100">
                  {formatMoneyLimit(doc.policy?.maxExposureNotional)}
                </span>
                <span className="text-[11px] text-slate-500 block mt-1">Exposição total simultânea em carteira</span>
              </div>

              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Max Daily Loss:</span>
                <span className="text-lg font-bold text-amber-400">{formatMoneyLimit(doc.policy?.maxDailyLoss)}</span>
                <span className="text-[11px] text-slate-500 block mt-1">Perda máxima permitida em 24h</span>
              </div>

              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Max Drawdown:</span>
                <span className="text-lg font-bold text-rose-400">{formatMoneyLimit(doc.policy?.maxDrawdown)}</span>
                <span className="text-[11px] text-slate-500 block mt-1">Rebaixamento máximo acumulado</span>
              </div>

              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Max Market Data Age:</span>
                <span className="text-lg font-bold text-slate-200">
                  {doc.policy?.maxMarketDataAge || 'Indisponível'}
                </span>
                <span className="text-[11px] text-slate-500 block mt-1">Frescor máximo de candles / klines</span>
              </div>

              <div className="p-4 bg-slate-950 rounded-lg border border-slate-800">
                <span className="text-slate-500 block mb-1">Retiradas na API (Withdrawals):</span>
                <span className="text-lg font-bold text-emerald-400">
                  {doc.policy?.withdrawalsEnabled === true
                    ? 'HABILITADAS (INSEGURO)'
                    : doc.policy?.withdrawalsEnabled === false
                      ? 'BLOQUEADAS (0/1)'
                      : 'Indisponível'}
                </span>
                <span className="text-[11px] text-emerald-500/80 block mt-1">Invariante de segurança obrigatória</span>
              </div>
            </div>

            {/* Approval Metadata */}
            <div className="mt-4 p-4 bg-slate-950 rounded-lg border border-slate-800 text-xs font-mono space-y-2">
              <div className="text-slate-400 font-bold uppercase tracking-wider flex items-center gap-1.5 text-[11px]">
                <FileText className="w-3.5 h-3.5 text-cyan-400" />
                <span>Trilha de Governança & 4-Eyes Approval</span>
              </div>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-2 text-slate-300">
                <div>
                  <span className="text-slate-500">Criado por:</span> {doc.createdBy}
                </div>
                <div>
                  <span className="text-slate-500">Aprovado por:</span> {doc.approvedBy || 'N/A'}
                </div>
                <div>
                  <span className="text-slate-500">Approval ID:</span> {doc.approvalId || 'N/A'}
                </div>
                <div className="md:col-span-2">
                  <span className="text-slate-500">Janela de Validade:</span> {formatIsoDate(doc.effectiveFromUtc)} até{' '}
                  {formatIsoDate(doc.expiresAtUtc)}
                </div>
                <div>
                  <span className="text-slate-500">Aprovado em:</span> {formatIsoDate(doc.approvedAtUtc)}
                </div>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Emergency Kill Switch Control Card */}
      <div className="bg-slate-900 border border-rose-900/50 rounded-xl p-6 shadow-sm">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-base font-bold text-rose-300 flex items-center gap-2">
              <ShieldAlert className="w-5 h-5 text-rose-400" />
              <span>Intervenção Operacional de Emergência (Kill Switch)</span>
            </h2>
            <p className="text-xs text-slate-400 mt-1 max-w-2xl leading-relaxed">
              Esta interface apenas exibe instruções locais. Não ativa o Kill Switch, não cancela ordens e não altera o
              runtime. Qualquer intervenção exige autorização humana e o control plane autenticado.
            </p>
          </div>

          <button
            ref={opener}
            type="button"
            onClick={() => setShowKillSwitchModal(true)}
            className="px-4 py-2.5 bg-rose-600 hover:bg-rose-500 text-white font-bold text-xs rounded-xl shadow-lg shadow-rose-900/30 transition-colors cursor-pointer flex items-center gap-2 whitespace-nowrap"
          >
            <ShieldAlert className="w-4 h-4" />
            <span>Consultar protocolo de Kill Switch</span>
          </button>
        </div>
      </div>

      {/* Kill Switch Modal */}
      {showKillSwitchModal && (
        <dialog
          ref={dialog}
          aria-labelledby="kill-switch-title"
          aria-describedby="kill-switch-description"
          onCancel={(event) => {
            event.preventDefault();
            closeDialog();
          }}
          onClose={() => {
            setShowKillSwitchModal(false);
            opener.current?.focus();
          }}
          className="m-auto bg-transparent text-slate-100 p-4 max-w-lg w-full backdrop:bg-black/80 backdrop:backdrop-blur-sm"
        >
          <div className="bg-slate-900 border border-rose-800 max-w-md w-full rounded-2xl p-6 shadow-2xl space-y-4">
            <div className="flex items-center gap-3 text-rose-400">
              <AlertTriangle className="w-8 h-8 flex-shrink-0" />
              <div>
                <h3 id="kill-switch-title" className="text-base font-bold text-slate-100">
                  Protocolo de Parada Emergencial
                </h3>
                <span className="text-xs text-rose-300">Consulta somente leitura</span>
              </div>
            </div>

            <p id="kill-switch-description" className="text-xs text-slate-300 leading-relaxed">
              Nenhuma intervenção será enviada. As instruções devem ser avaliadas pelo operador, com step-up e
              autorização administrativa segregada no control plane externo.
            </p>

            <div className="p-3 bg-slate-950 rounded-lg border border-slate-800 text-xs font-mono text-slate-400 space-y-1">
              <div>
                <strong>Política:</strong> API Web permanece estritamente read-only
              </div>
              <div>
                <strong>Comando:</strong> Executável via control plane externo autenticado
              </div>
            </div>

            <div className="flex gap-3 pt-2">
              <button
                ref={cancelButton}
                type="button"
                onClick={closeDialog}
                className="flex-1 py-2 bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium rounded-lg transition-colors cursor-pointer"
              >
                Cancelar
              </button>
              <button
                type="button"
                onClick={() => {
                  setInterventionNotice(
                    'Como a API Web opera em modo estritamente somente leitura (DenyApiOperationalControlResetAuthorizer), a parada de emergência do runtime deve ser emitida pelo operador via console autenticado local:',
                  );
                  closeDialog();
                }}
                className="flex-1 py-2 bg-rose-600 hover:bg-rose-500 text-white text-xs font-bold rounded-lg transition-colors cursor-pointer"
              >
                Exibir instruções locais
              </button>
            </div>
          </div>
        </dialog>
      )}
    </div>
  );
};
