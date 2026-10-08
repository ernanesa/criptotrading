import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/client';
import { OperationalOverviewView } from '../views/OperationalOverviewView';

const renderView = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <OperationalOverviewView />
    </QueryClientProvider>,
  );
afterEach(() => vi.restoreAllMocks());

const stubBaseline = () => {
  vi.spyOn(apiClient, 'getOperationalStatus').mockResolvedValue({
    source: 'test',
    mode: 'Normal',
    blocksNewEntries: false,
    revision: 1,
    changedAtUtc: '2026-10-07T00:00:00Z',
    changedBy: null,
    reason: null,
    mutatingEndpointsEnabled: false,
  });
  vi.spyOn(apiClient, 'getAutonomyCheckpoint').mockResolvedValue({ scope: 'BTCUSDT', lastProcessedCloseUtc: null });
  vi.spyOn(apiClient, 'getPortfolioBalances').mockResolvedValue({
    environment: 'test',
    canTrade: false,
    totalNavUsd: 0,
    cashUsdt: 0,
    positionsUsd: 0,
    balances: [],
    observedAtUtc: '2026-10-07T00:00:00Z',
  });
};

describe('OperationalOverviewView evidence states', () => {
  it('reports returned degraded and unhealthy statuses instead of converting success to healthy', async () => {
    stubBaseline();
    vi.spyOn(apiClient, 'getHealthLive').mockResolvedValue({ status: 'Unhealthy' });
    vi.spyOn(apiClient, 'getHealthReady').mockResolvedValue({ status: 'Degraded' });
    vi.spyOn(apiClient, 'getTelemetryMetrics').mockResolvedValue({
      apiRequests: 20,
      apiErrors: 1,
      workerHeartbeats: 2,
      apiDurationMilliseconds: 15,
      alertDeliveries: 0,
      alertDeliveryFailures: 0,
      alertDeduplicated: 0,
      reconciliationRuns: 3,
      reconciliationMismatches: 1,
      outboxDispatches: 4,
      outboxDispatchFailures: 0,
      outboxDeadLetters: 0,
      invalidMarketDataPayloads: 0,
    });
    renderView();

    expect(await screen.findByText('Unhealthy')).toBeInTheDocument();
    expect(screen.getByText('Degraded')).toBeInTheDocument();
    expect(screen.getByText('Taxa de erro: 5.0%')).toBeInTheDocument();
    expect(screen.queryByText(/Configuração segura de Testnet carregada e validada/)).not.toBeInTheDocument();
  });

  it('keeps readiness and telemetry unknown when their endpoints fail', async () => {
    stubBaseline();
    vi.spyOn(apiClient, 'getHealthLive').mockResolvedValue({ status: 'Healthy' });
    vi.spyOn(apiClient, 'getHealthReady').mockRejectedValue(new Error('not available'));
    vi.spyOn(apiClient, 'getTelemetryMetrics').mockRejectedValue(new Error('not available'));
    renderView();

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
    expect(screen.getAllByText('Indisponível').length).toBeGreaterThan(0);
    expect(screen.queryByText('Taxa de erro: 0.0%')).not.toBeInTheDocument();
  });
});
