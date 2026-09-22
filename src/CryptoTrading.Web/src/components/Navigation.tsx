import React from 'react';
import { LayoutDashboard, FileSearch, ShieldCheck } from 'lucide-react';

export type NavTab = 'overview' | 'decisions' | 'risk';

interface NavigationProps {
  currentTab: NavTab;
  onSelectTab: (tab: NavTab) => void;
}

export const Navigation: React.FC<NavigationProps> = ({ currentTab, onSelectTab }) => {
  const navItems = [
    { id: 'overview' as NavTab, label: 'Visão Geral & Saúde', icon: LayoutDashboard },
    { id: 'decisions' as NavTab, label: 'Decisões & Auditoria', icon: FileSearch },
    { id: 'risk' as NavTab, label: 'Controle de Risco & Kill Switch', icon: ShieldCheck },
  ];

  return (
    <nav className="border-b border-slate-800 bg-slate-900/50 px-4">
      <div className="max-w-7xl mx-auto flex items-center gap-2">
        {navItems.map((item) => {
          const Icon = item.icon;
          const isActive = currentTab === item.id;
          return (
            <button
              key={item.id}
              onClick={() => onSelectTab(item.id)}
              className={`flex items-center gap-2 px-4 py-3 text-sm font-medium border-b-2 transition-colors duration-150 cursor-pointer ${
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
