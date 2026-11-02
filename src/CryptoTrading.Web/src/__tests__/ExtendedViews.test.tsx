import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/client';
import { Navigation, type NavTab } from '../components/Navigation';
import { AutonomyDashboardView } from '../views/AutonomyDashboardView';
import { BenchmarkDashboardView } from '../views/BenchmarkDashboardView';
import { FinancialPerformanceView } from '../views/FinancialPerformanceView';
import { RealtimeTradingView } from '../views/RealtimeTradingView';
import { ResearchDashboardView } from '../views/ResearchDashboardView';

const createTestQueryClient = () =>
  new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

afterEach(() => vi.restoreAllMocks());

describe('RealtimeTradingView (US-101)', () => {
  it('renders ticker and open positions table', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <RealtimeTradingView />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('realtime-trading-view')).toBeInTheDocument();
    expect(screen.getByText('BTC / USDT')).toBeInTheDocument();
    expect(screen.getByText('SPOT · AMBIENTE NÃO VERIFICADO')).toBeInTheDocument();
    expect(screen.getByText(/Saldos e Ativos da Conta/)).toBeInTheDocument();
    expect(screen.getByText('Ordens Abertas no Livro (OMS)')).toBeInTheDocument();
    expect(screen.getByText('Ordens indisponíveis')).toBeInTheDocument();
  });
});

describe('FinancialPerformanceView (Capital & PnL)', () => {
  it('renders nullable period P&L as unavailable instead of zero or a render error', async () => {
    vi.spyOn(apiClient, 'getPortfolioPerformance').mockResolvedValue({
      totalNavUsd: 500,
      initialCapitalUsd: null,
      initialCapitalAvailabilityReason: 'Unknown',
      cashBalanceUsd: null,
      positionsValueUsd: null,
      totalProfitUsd: 2,
      totalProfitPct: null,
      totalProfitPctAvailabilityReason: 'Unknown',
      totalRealizedPnlUsd: 0,
      totalUnrealizedPnlUsd: 2,
      totalFeesPaidUsd: 0,
      totalTradesCount: 0,
      winRatePercentage: 0,
      profitFactor: null,
      profitFactorAvailabilityReason: 'Unknown',
      feesAreComplete: true,
      unvaluedFeeAmounts: [],
      scopeSymbol: 'BTCUSDT',
      quoteAsset: 'USDT',
      asOfUtc: '2026-11-02T00:00:00Z',
      periods: [
        {
          periodName: 'Dia',
          profitUsd: null,
          profitPercentage: null,
          realizedProfitUsd: 0,
          unrealizedProfitUsd: null,
          feesPaidUsd: 0,
          tradesCount: 0,
          winRatePercentage: 0,
          snapshotAvailability: 'Available',
        },
      ],
    });
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <FinancialPerformanceView />
      </QueryClientProvider>,
    );
    const row = await screen.findByRole('row', { name: /^Dia / });
    expect(within(row).getAllByText('Indisponível')).toHaveLength(4);
  });

  it('does not render invented zero balances before auditable P&L data is available', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <FinancialPerformanceView />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('financial-performance-view')).toBeInTheDocument();
    expect(screen.getByText('Patrimônio, Capital Investido & Lucro (P&L)')).toBeInTheDocument();
    expect(screen.getByTestId('performance-unavailable')).toBeInTheDocument();
    expect(screen.queryByText('Capital Investido (Aporte)')).not.toBeInTheDocument();
  });

  it('shows known NAV while leaving unavailable capital, return and profit factor unknown', async () => {
    vi.spyOn(apiClient, 'getPortfolioPerformance').mockResolvedValue({
      totalNavUsd: 500,
      initialCapitalUsd: null,
      initialCapitalAvailabilityReason: 'UNKNOWN_UNTIL_BASELINE_AND_EXTERNAL_CASH_FLOWS_ARE_DURABLE',
      cashBalanceUsd: null,
      positionsValueUsd: null,
      totalProfitUsd: 2,
      totalProfitPct: null,
      totalProfitPctAvailabilityReason: 'UNKNOWN_UNTIL_BASELINE_AND_EXTERNAL_CASH_FLOWS_ARE_DURABLE',
      totalRealizedPnlUsd: 0,
      totalUnrealizedPnlUsd: 2,
      totalFeesPaidUsd: 0,
      totalTradesCount: 0,
      winRatePercentage: 0,
      profitFactor: null,
      profitFactorAvailabilityReason: 'UNKNOWN_WITHOUT_REALIZED_LOSSES',
      feesAreComplete: false,
      unvaluedFeeAmounts: [],
      scopeSymbol: 'BTCUSDT',
      quoteAsset: 'USDT',
      asOfUtc: '2026-10-07T12:00:00Z',
      periods: [],
    });

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <FinancialPerformanceView />
      </QueryClientProvider>,
    );

    expect((await screen.findAllByText('Indisponível')).length).toBeGreaterThan(0);
    expect(screen.getByText(/P&L parcial/)).toBeInTheDocument();
    expect(screen.queryByText(/0\.00%/)).not.toBeInTheDocument();
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação manual USD/BRL' }), { target: { value: '2' } });
    expect(screen.getByText('R$ 1.000,00')).toBeInTheDocument();
    expect(screen.getByText(/Conversões em BRL são simulações/)).toBeInTheDocument();
    for (const value of ['0', '-1', '', '1e309']) {
      fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação manual USD/BRL' }), { target: { value } });
      expect(screen.getByRole('alert')).toHaveTextContent('Cotação inválida');
      expect(screen.queryByText('R$ 1.000,00')).not.toBeInTheDocument();
      expect(screen.queryByText(/NaN|Infinity|∞/)).not.toBeInTheDocument();
    }
  });
});

describe('ResearchDashboardView (US-103)', () => {
  it('keeps strategy and benchmark choices while research evidence is unavailable', () => {
    const { container } = render(<ResearchDashboardView />);
    expect(screen.getByTestId('research-dashboard-view')).toBeInTheDocument();
    expect(screen.getByTestId('research-evidence-unavailable')).toBeInTheDocument();
    expect(screen.getByText(/Não há artefato OOS\/backtest verificado carregado/)).toBeInTheDocument();

    fireEvent.change(screen.getByRole('combobox', { name: 'Estratégia' }), {
      target: { value: 'breakout' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Benchmark' }), {
      target: { value: 'btc' },
    });

    expect(screen.getByText(/Estratégia selecionada: Donchian Channel Breakout/)).toBeInTheDocument();
    expect(screen.getByText(/Benchmark selecionado: Bitcoin Buy & Hold/)).toBeInTheDocument();
    expect(screen.getByText(/Métricas, curvas de patrimônio, lucro e comparação:/)).toHaveTextContent('Indisponível');
    expect(container.querySelector('polyline, polygon')).toBeNull();
  });
});

describe('BenchmarkDashboardView (Brazilian Benchmarks)', () => {
  it('renders CDI, Selic, IPCA, Tesouro and switches time horizon', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <BenchmarkDashboardView />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('benchmark-dashboard-view')).toBeInTheDocument();
    expect(screen.getByText('Benchmarks Financeiros & Macroeconômicos Brasileiros')).toBeInTheDocument();
    expect(screen.queryByText(/Alpha s\/ CDI|Equivalente do CDI/)).not.toBeInTheDocument();
    expect(
      screen.getByText('Comparação indisponível: a API não fornece séries históricas alinhadas.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/IN RFB nº 1.888\/2019/)).toBeInTheDocument();

    // Switch horizon to 3 Meses
    const threeMonthsBtn = screen.getByText('3 Meses');
    fireEvent.click(threeMonthsBtn);
    expect(screen.getByText('Horizonte: 63 dias úteis')).toBeInTheDocument();
  });

  it('uses exchange rate only for an explicitly hypothetical capital conversion', () => {
    const { container } = render(
      <QueryClientProvider client={createTestQueryClient()}>
        <BenchmarkDashboardView />
      </QueryClientProvider>,
    );
    expect(container.querySelector('svg title')).not.toBeInTheDocument();
    expect(screen.getByText(/Taxas fixas hipotéticas/)).toBeInTheDocument();
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação do Dólar em Reais' }), { target: { value: '5' } });
    expect(screen.getByTestId('simulated-capital-usd')).toHaveTextContent('20.000');
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação do Dólar em Reais' }), {
      target: { value: '10' },
    });
    expect(screen.getByTestId('simulated-capital-usd')).toHaveTextContent('10.000');
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação do Dólar em Reais' }), {
      target: { value: '-1' },
    });
    expect(screen.getByRole('alert')).toHaveTextContent('Cotação inválida');
    expect(screen.getByTestId('simulated-capital-usd')).toHaveTextContent('Indisponível');
    expect(screen.queryByText(/Alpha|Equivalente do CDI|Poder de compra preservado/)).not.toBeInTheDocument();
    for (const value of ['0', '-1', '', '1e309']) {
      fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação do Dólar em Reais' }), { target: { value } });
      expect(screen.getByRole('alert')).toHaveTextContent('Cotação inválida');
      expect(screen.getByTestId('simulated-capital-usd')).toHaveTextContent('Indisponível');
    }
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Capital Inicial Simulado em Reais' }), {
      target: { value: '1e308' },
    });
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Cotação do Dólar em Reais' }), {
      target: { value: '0.01' },
    });
    expect(screen.getByTestId('simulated-capital-usd')).toHaveTextContent('Indisponível');
    expect(screen.queryByText(/NaN|Infinity|∞/)).not.toBeInTheDocument();
  });
});

describe('AutonomyDashboardView (US-104)', () => {
  it('renders regime classifier, dynamic weights, drift metrics and model registry', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AutonomyDashboardView />
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('autonomy-dashboard-view')).toBeInTheDocument();
    expect(screen.getByText('Controle de Autonomia & Multi-Estratégia')).toBeInTheDocument();
    expect(screen.getByText(/Indisponível \(Unknown\)/)).toBeInTheDocument();
    expect(screen.getByText('Histórico de Recompensas por Estratégia e Regime')).toBeInTheDocument();
    expect(screen.getByText(/Monitoramento/)).toBeInTheDocument();
    expect(screen.queryByText('mdl-trend-prod-v1.2')).not.toBeInTheDocument();
  });
});

describe('Navigation tab bar', () => {
  it('renders all 8 navigation tabs and invokes callback', () => {
    let selected: NavTab = 'overview';
    const onSelect = (tab: NavTab) => {
      selected = tab;
    };

    render(<Navigation currentTab={selected} onSelectTab={onSelect} />);
    const realtimeTab = screen.getByTestId('nav-tab-realtime');
    expect(realtimeTab).toBeInTheDocument();
    fireEvent.click(realtimeTab);
    expect(selected).toBe('realtime');

    const performanceTab = screen.getByTestId('nav-tab-performance');
    expect(performanceTab).toBeInTheDocument();
    fireEvent.click(performanceTab);
    expect(selected).toBe('performance');

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
