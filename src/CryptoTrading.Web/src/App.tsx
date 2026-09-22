import React, { useState, useEffect } from 'react';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { apiClient } from './api/client';
import { EnvironmentBanner } from './components/EnvironmentBanner';
import { Navigation, type NavTab } from './components/Navigation';
import { ErrorBoundary } from './components/ErrorBoundary';
import { OperationalOverviewView } from './views/OperationalOverviewView';
import { DecisionTraceView } from './views/DecisionTraceView';
import { RiskControlView } from './views/RiskControlView';
import { Shield, Clock } from 'lucide-react';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      staleTime: 5000,
    },
  },
});

const AppContent: React.FC = () => {
  const [currentTab, setCurrentTab] = useState<NavTab>('overview');
  const [currentTimeUtc, setCurrentTimeUtc] = useState<string>(new Date().toISOString());

  useEffect(() => {
    const timer = setInterval(() => {
      setCurrentTimeUtc(new Date().toISOString().replace('T', ' ').substring(0, 19) + ' UTC');
    }, 1000);
    return () => clearInterval(timer);
  }, []);

  const statusQuery = useQuery({
    queryKey: ['operationalStatus'],
    queryFn: apiClient.getOperationalStatus,
    refetchInterval: 5000,
  });

  return (
    <div className="min-h-screen flex flex-col bg-slate-950 text-slate-100 font-sans selection:bg-cyan-500 selection:text-slate-950">
      {/* Top Banner */}
      <EnvironmentBanner
        status={statusQuery.data}
        isLoading={statusQuery.isLoading}
        environmentName="TESTNET"
      />

      {/* Navigation Bar */}
      <Navigation currentTab={currentTab} onSelectTab={setCurrentTab} />

      {/* Main Content Area */}
      <main className="flex-1 max-w-7xl w-full mx-auto p-6">
        <ErrorBoundary>
          {currentTab === 'overview' && <OperationalOverviewView />}
          {currentTab === 'decisions' && <DecisionTraceView />}
          {currentTab === 'risk' && <RiskControlView />}
        </ErrorBoundary>
      </main>

      {/* Footer */}
      <footer className="border-t border-slate-800 bg-slate-900/40 py-4 px-6 text-xs text-slate-500">
        <div className="max-w-7xl mx-auto flex flex-wrap items-center justify-between gap-4">
          <div className="flex items-center gap-2">
            <Shield className="w-3.5 h-3.5 text-cyan-500" />
            <span>CryptoTrading Autonomous System v1.0 — Console de Operação Read-Only</span>
          </div>

          <div className="flex items-center gap-4 font-mono text-[11px]">
            <div className="flex items-center gap-1.5 text-slate-400">
              <Clock className="w-3.5 h-3.5 text-slate-500" />
              <span>{currentTimeUtc}</span>
            </div>
            <span>|</span>
            <span>Local Loopback 127.0.0.1</span>
          </div>
        </div>
      </footer>
    </div>
  );
};

export const App: React.FC = () => {
  return (
    <QueryClientProvider client={queryClient}>
      <AppContent />
    </QueryClientProvider>
  );
};

export default App;
