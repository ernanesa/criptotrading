import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Navigation, type NavTab } from '../components/Navigation';
import { AutonomyDashboardView } from '../views/AutonomyDashboardView';
import { BenchmarkDashboardView } from '../views/BenchmarkDashboardView';
import { RealtimeTradingView } from '../views/RealtimeTradingView';
import { ResearchDashboardView } from '../views/ResearchDashboardView';

describe('RealtimeTradingView (US-101)', () => {
  it('renders ticker and open positions table', () => {
    render(<RealtimeTradingView />);
    expect(screen.getByTestId('realtime-trading-view')).toBeInTheDocument();
    expect(screen.getByText('BTC / USDT')).toBeInTheDocument();
    expect(screen.getByText('SPOT TESTNET')).toBeInTheDocument();
    expect(screen.getByText('Posições Abertas')).toBeInTheDocument();
    expect(screen.getByText('Ordens Abertas no Livro (OMS)')).toBeInTheDocument();
    expect(screen.getByText('ct-ord-20260921-001')).toBeInTheDocument();
  });
});

describe('ResearchDashboardView (US-103)', () => {
  it('renders quantitative KPIs and switches strategy families and benchmarks', () => {
    render(<ResearchDashboardView />);
    expect(screen.getByTestId('research-dashboard-view')).toBeInTheDocument();
    expect(screen.getByText(/Laboratório Quantitativo/)).toBeInTheDocument();
    expect(screen.getByText(/Benchmark Ativo de Comparação/)).toBeInTheDocument();
    expect(screen.getByText(/Custo de Oportunidade Brasil/)).toBeInTheDocument();
    expect(screen.getByText(/Intervalos de Confiança Monte Carlo \(95%\)/)).toBeInTheDocument();

    // Switch to Breakout
    const breakoutBtn = screen.getByText('Breakout / Momentum');
    fireEvent.click(breakoutBtn);
    expect(screen.getByText('Donchian Channel Breakout')).toBeInTheDocument();
  });
});

describe('BenchmarkDashboardView (Brazilian Benchmarks)', () => {
  it('renders CDI, Selic, IPCA, Tesouro and switches time horizon', () => {
    render(<BenchmarkDashboardView />);
    expect(screen.getByTestId('benchmark-dashboard-view')).toBeInTheDocument();
    expect(screen.getByText('Benchmarks Financeiros & Macroeconômicos Brasileiros')).toBeInTheDocument();
    expect(screen.getByText('Alpha s/ CDI (em BRL)')).toBeInTheDocument();
    expect(screen.getByText('Equivalente do CDI')).toBeInTheDocument();
    expect(screen.getByText(/IN RFB nº 1.888\/2019/)).toBeInTheDocument();

    // Switch horizon to 3 Meses
    const threeMonthsBtn = screen.getByText('3 Meses');
    fireEvent.click(threeMonthsBtn);
    expect(screen.getByText('Horizonte: 63 dias úteis')).toBeInTheDocument();
  });
});

describe('AutonomyDashboardView (US-104)', () => {
  it('renders regime classifier, dynamic weights, drift metrics and model registry', () => {
    render(<AutonomyDashboardView />);
    expect(screen.getByTestId('autonomy-dashboard-view')).toBeInTheDocument();
    expect(screen.getByText('Controle de Autonomia & Multi-Estratégia')).toBeInTheDocument();
    expect(screen.getByText(/BULL TREND/)).toBeInTheDocument();
    expect(screen.getByText('Alocação Dinâmica de Capital por Estratégia (Bandit Bayesiano)')).toBeInTheDocument();
    expect(screen.getByText('Execution Slippage Drift (US-172)')).toBeInTheDocument();
    expect(screen.getByText('mdl-trend-prod-v1.2')).toBeInTheDocument();
  });
});

describe('Navigation tab bar', () => {
  it('renders all 7 navigation tabs and invokes callback', () => {
    let selected: NavTab = 'overview';
    const onSelect = (tab: NavTab) => {
      selected = tab;
    };

    render(<Navigation currentTab={selected} onSelectTab={onSelect} />);
    const realtimeTab = screen.getByTestId('nav-tab-realtime');
    expect(realtimeTab).toBeInTheDocument();
    fireEvent.click(realtimeTab);
    expect(selected).toBe('realtime');

    const benchmarkTab = screen.getByTestId('nav-tab-benchmarks');
    expect(benchmarkTab).toBeInTheDocument();
    fireEvent.click(benchmarkTab);
    expect(selected).toBe('benchmarks');

    const autonomyTab = screen.getByTestId('nav-tab-autonomy');
    expect(autonomyTab).toBeInTheDocument();
    fireEvent.click(autonomyTab);
    expect(selected).toBe('autonomy');
  });
});
