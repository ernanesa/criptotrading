import { Activity, Lock, ShieldAlert } from 'lucide-react';
import type React from 'react';
import type { OperationalStatusResponse } from '../types/api';
import { StatusBadge } from './StatusBadge';

interface EnvironmentBannerProps {
  status?: OperationalStatusResponse | null;
  isLoading?: boolean;
  environmentName?: string;
}

export const EnvironmentBanner: React.FC<EnvironmentBannerProps> = ({
  status,
  isLoading = false,
  environmentName = 'TESTNET',
}) => {
  const mode = status?.mode ?? 'Normal';
  const isSafeMode = mode === 'SafeMode';
  const isKillSwitch = mode === 'KillSwitch';

  return (
    <header
      className={`border-b transition-colors duration-300 ${
        isKillSwitch
          ? 'bg-rose-950/90 border-rose-800 text-rose-100'
          : isSafeMode
            ? 'bg-amber-950/90 border-amber-800 text-amber-100'
            : 'bg-slate-900/90 border-slate-800 text-slate-200'
      } backdrop-blur sticky top-0 z-50 px-4 py-2.5 shadow-md`}
      data-testid="environment-banner"
    >
      <div className="max-w-7xl mx-auto flex flex-wrap items-center justify-between gap-3 text-xs">
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-1.5 font-bold tracking-wider uppercase text-cyan-400">
            <Activity className="w-4 h-4 animate-spin-slow" />
            <span>CryptoTrading Ops</span>
          </div>

          <div className="h-4 w-px bg-slate-700 mx-1" />

          <div className="flex items-center gap-1.5">
            <span className="text-slate-400 font-mono">ENV:</span>
            <span
              className="px-2 py-0.5 rounded bg-cyan-950/80 text-cyan-300 font-mono font-bold border border-cyan-800"
              data-testid="env-badge"
            >
              {environmentName}
            </span>
          </div>

          <div className="flex items-center gap-1.5">
            <span className="text-slate-400 font-mono">SUPERVISOR:</span>
            {isLoading ? (
              <span className="text-slate-500 italic">carregando...</span>
            ) : (
              <StatusBadge status={mode} size="sm" />
            )}
          </div>
        </div>

        <div className="flex items-center gap-3">
          {status?.blocksNewEntries && (
            <div className="flex items-center gap-1 px-2.5 py-0.5 rounded bg-amber-900/60 text-amber-300 border border-amber-700 font-mono text-[11px]">
              <ShieldAlert className="w-3.5 h-3.5" />
              <span>BLOQUEIO DE NOVAS ENTRADAS ATIVO</span>
            </div>
          )}

          {isKillSwitch && (
            <div className="flex items-center gap-1 px-2.5 py-0.5 rounded bg-rose-900/80 text-rose-200 border border-rose-600 font-mono text-[11px] animate-pulse">
              <ShieldAlert className="w-3.5 h-3.5" />
              <span>KILL SWITCH ACIONADO</span>
            </div>
          )}

          <div className="flex items-center gap-1.5 px-2.5 py-0.5 rounded bg-slate-800 text-slate-400 border border-slate-700 font-mono text-[11px]">
            <Lock className="w-3 h-3 text-emerald-400" />
            <span>API MUTÁVEL: DESATIVADA (READ-ONLY)</span>
          </div>

          {status && (
            <div className="text-[11px] text-slate-500 font-mono hidden md:inline-block">Rev #{status.revision}</div>
          )}
        </div>
      </div>
    </header>
  );
};
