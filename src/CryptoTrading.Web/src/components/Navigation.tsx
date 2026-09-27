import {
  Activity,
  Compass,
  Cpu,
  FileSearch,
  FlaskConical,
  Landmark,
  LayoutDashboard,
  ShieldCheck,
  Wallet,
} from 'lucide-react';
import type React from 'react';

export type NavTab =
  | 'overview'
  | 'realtime'
  | 'performance'
  | 'decisions'
  | 'research'
  | 'benchmarks'
  | 'autonomy'
  | 'risk';

interface NavigationProps {
  currentTab: NavTab;
  onSelectTab: (tab: NavTab) => void;
}

export const Navigation: React.FC<NavigationProps> = ({ currentTab, onSelectTab }) => {
  // 1. Core live operational tabs (real Testnet data)
  const operationalItems = [
    { id: 'overview' as NavTab, label: 'Visão Geral', icon: LayoutDashboard },
    { id: 'realtime' as NavTab, label: 'Trading ao Vivo', icon: Activity },
    { id: 'performance' as NavTab, label: 'Patrimônio & P&L', icon: Wallet },
    { id: 'decisions' as NavTab, label: 'Auditoria', icon: FileSearch },
    { id: 'risk' as NavTab, label: 'Risco & Segurança', icon: ShieldCheck },
  ];

  // 2. Offline research / theoretical modeling tabs (simulation / backtest)
  const researchItems = [
    { id: 'research' as NavTab, label: 'Laboratório', icon: Compass, tag: 'Backtest' },
    { id: 'benchmarks' as NavTab, label: 'Benchmarks BR', icon: Landmark, tag: 'CDI/Selic' },
    { id: 'autonomy' as NavTab, label: 'Autonomia', icon: Cpu, tag: 'Bandit' },
  ];

  return (
    <nav className="border-b border-slate-800 bg-slate-900/60 px-4 backdrop-blur-sm">
      <div className="max-w-7xl mx-auto flex items-center justify-between gap-2 overflow-x-auto [scrollbar-width:none] [-ms-overflow-style:none] [&::-webkit-scrollbar]:hidden py-1.5">
        {/* Operational Group */}
        <div className="flex items-center gap-1 sm:gap-1.5 shrink-0">
          {operationalItems.map((item) => {
            const Icon = item.icon;
            const isActive = currentTab === item.id;
            return (
              <button
                type="button"
                key={item.id}
                onClick={() => onSelectTab(item.id)}
                className={`flex items-center gap-1.5 px-3 py-2 text-xs font-semibold rounded-lg transition-all duration-150 cursor-pointer whitespace-nowrap ${
                  isActive
                    ? 'bg-cyan-500/15 text-cyan-400 border border-cyan-500/30 shadow-sm shadow-cyan-950'
                    : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/60 border border-transparent'
                }`}
                data-testid={`nav-tab-${item.id}`}
              >
                <Icon className={`w-3.5 h-3.5 ${isActive ? 'text-cyan-400' : 'text-slate-400'}`} />
                <span>{item.label}</span>
              </button>
            );
          })}
        </div>

        {/* Separator */}
        <div className="h-5 w-px bg-slate-800 shrink-0 mx-1 hidden sm:block" />

        {/* Research / Simulation Group */}
        <div className="flex items-center gap-1 sm:gap-1.5 shrink-0">
          <div className="hidden lg:flex items-center gap-1 text-[11px] text-slate-500 uppercase tracking-wider font-mono mr-1">
            <FlaskConical className="w-3 h-3 text-purple-400" />
            <span>Simulação:</span>
          </div>

          {researchItems.map((item) => {
            const Icon = item.icon;
            const isActive = currentTab === item.id;
            return (
              <button
                type="button"
                key={item.id}
                onClick={() => onSelectTab(item.id)}
                className={`flex items-center gap-1.5 px-2.5 py-2 text-xs font-medium rounded-lg transition-all duration-150 cursor-pointer whitespace-nowrap ${
                  isActive
                    ? 'bg-purple-500/15 text-purple-300 border border-purple-500/30 shadow-sm shadow-purple-950'
                    : 'text-slate-400 hover:text-slate-200 hover:bg-slate-800/60 border border-transparent'
                }`}
                data-testid={`nav-tab-${item.id}`}
              >
                <Icon className={`w-3.5 h-3.5 ${isActive ? 'text-purple-400' : 'text-slate-400'}`} />
                <span>{item.label}</span>
                <span className="hidden xl:inline text-[9px] px-1 py-0.5 rounded bg-slate-800 text-slate-400 border border-slate-700 font-mono">
                  {item.tag}
                </span>
              </button>
            );
          })}
        </div>
      </div>
    </nav>
  );
};
