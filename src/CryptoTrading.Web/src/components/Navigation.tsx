import { Activity, Compass, Cpu, FileSearch, Landmark, LayoutDashboard, ShieldCheck } from 'lucide-react';
import type React from 'react';

export type NavTab = 'overview' | 'realtime' | 'decisions' | 'research' | 'benchmarks' | 'autonomy' | 'risk';

interface NavigationProps {
  currentTab: NavTab;
  onSelectTab: (tab: NavTab) => void;
}

export const Navigation: React.FC<NavigationProps> = ({ currentTab, onSelectTab }) => {
  const navItems = [
    { id: 'overview' as NavTab, label: 'Visão Geral & Saúde', icon: LayoutDashboard },
    { id: 'realtime' as NavTab, label: 'Trading em Tempo Real', icon: Activity },
    { id: 'decisions' as NavTab, label: 'Decisões & Auditoria', icon: FileSearch },
    { id: 'research' as NavTab, label: 'Laboratório Quantitativo', icon: Compass },
    { id: 'benchmarks' as NavTab, label: 'Benchmarks Brasileiros', icon: Landmark },
    { id: 'autonomy' as NavTab, label: 'Autonomia & Aprendizado', icon: Cpu },
    { id: 'risk' as NavTab, label: 'Controle de Risco & Kill Switch', icon: ShieldCheck },
  ];

  return (
    <nav className="border-b border-slate-800 bg-slate-900/50 px-4">
      <div className="max-w-7xl mx-auto flex items-center gap-2 overflow-x-auto">
        {navItems.map((item) => {
          const Icon = item.icon;
          const isActive = currentTab === item.id;
          return (
            <button
              type="button"
              key={item.id}
              onClick={() => onSelectTab(item.id)}
              className={`flex items-center gap-2 px-4 py-3 text-sm font-medium border-b-2 transition-colors duration-150 cursor-pointer whitespace-nowrap ${
                isActive
                  ? 'border-cyan-500 text-cyan-400 bg-slate-800/30'
                  : 'border-transparent text-slate-400 hover:text-slate-200 hover:border-slate-700'
              }`}
              data-testid={`nav-tab-${item.id}`}
            >
              <Icon className="w-4 h-4" />
              <span>{item.label}</span>
            </button>
          );
        })}
      </div>
    </nav>
  );
};
