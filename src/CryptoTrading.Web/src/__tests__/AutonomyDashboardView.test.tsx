import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/client';
import { AutonomyDashboardView } from '../views/AutonomyDashboardView';

const renderView = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <AutonomyDashboardView />
    </QueryClientProvider>,
  );
afterEach(() => vi.restoreAllMocks());

describe('AutonomyDashboardView evidence states', () => {
  it('fails closed and shows unknown values when no regime or learning history exists', async () => {
    vi.spyOn(apiClient, 'getCurrentMarketRegime').mockResolvedValue({
      symbol: 'BTCUSDT',
      regime: 'Unknown',
      confidence: 0,
      trendSlopeRate: 0,
      volatilityRate: 0,
      reason: 'NO_REGIME_RECORDED_YET',
      observedAtUtc: '2026-10-07T00:00:00Z',
      prohibitsNewExposure: false,
    });
    vi.spyOn(apiClient, 'getStrategyAllocations').mockResolvedValue([]);
    vi.spyOn(apiClient, 'getRecentDrifts').mockResolvedValue([]);
    vi.spyOn(apiClient, 'getRegisteredModels').mockResolvedValue([]);
    renderView();

    expect(await screen.findByText(/Indisponível \(Unknown\)/)).toBeInTheDocument();
    expect(screen.getAllByText('Indisponível').length).toBeGreaterThan(1);
    expect(screen.getByText(/Novas exposições bloqueadas/)).toBeInTheDocument();
    expect(screen.getByText(/Nenhum desempenho de estratégia registrado/)).toBeInTheDocument();
    expect(screen.queryByText(/BULL TREND|88%|\$5500/)).not.toBeInTheDocument();
  });

  it('uses the drift enum and does not turn unknown severities into normal', async () => {
    vi.spyOn(apiClient, 'getCurrentMarketRegime').mockResolvedValue({
      symbol: 'BTCUSDT',
      regime: 'BullTrend',
      confidence: 0.61,
      trendSlopeRate: 0.01,
      volatilityRate: 0.02,
      reason: 'API snapshot',
      observedAtUtc: '2026-10-07T00:00:00Z',
      prohibitsNewExposure: false,
    });
    vi.spyOn(apiClient, 'getStrategyAllocations').mockResolvedValue([
      {
        strategyId: 'strategy-a',
        regime: 'BullTrend',
        observationCount: 4,
        cumulativeReward: 0.12,
        averageReward: 0.03,
      },
    ]);
    vi.spyOn(apiClient, 'getRecentDrifts').mockResolvedValue([
      {
        observationId: 'd1',
        strategyId: 'strategy-a',
        type: 'ExecutionSlippageDrift',
        expectedValue: 1,
        observedValue: 2,
        allowedDeviationThreshold: 3,
        absoluteDeviation: 1,
        relativeRate: 1,
        severity: 'UnexpectedSeverity',
        requiresQuarantine: false,
        message: 'test',
        observedAtUtc: '2026-10-07T00:00:00Z',
      },
    ]);
    vi.spyOn(apiClient, 'getRegisteredModels').mockResolvedValue([]);
    renderView();

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
    expect(screen.getByText(/4 observações · recompensa acumulada 0.12 · média 0.03/)).toBeInTheDocument();
    expect(screen.queryByText(/12%|\$4000/)).not.toBeInTheDocument();
  });
});
