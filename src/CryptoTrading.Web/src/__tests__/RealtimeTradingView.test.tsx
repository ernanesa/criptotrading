import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/client';
import { RealtimeTradingView } from '../views/RealtimeTradingView';

const renderView = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RealtimeTradingView />
    </QueryClientProvider>,
  );
afterEach(() => vi.restoreAllMocks());

const stubEmptyAccount = () => {
  vi.spyOn(apiClient, 'getPortfolioBalances').mockResolvedValue({
    environment: 'test',
    canTrade: false,
    totalNavUsd: 0,
    cashUsdt: 0,
    positionsUsd: 0,
    balances: [],
    observedAtUtc: '2026-10-07T00:00:00Z',
  });
  vi.spyOn(apiClient, 'getOpenOrders').mockResolvedValue([]);
  vi.spyOn(apiClient, 'getTrades').mockResolvedValue([]);
  vi.spyOn(apiClient, 'getCurrentMarketRegime').mockResolvedValue({
    symbol: 'BTCUSDT',
    regime: 'Unknown',
    confidence: 0,
    trendSlopeRate: 0,
    volatilityRate: 0,
    reason: 'NO_REGIME_RECORDED_YET',
    observedAtUtc: '2026-10-07T00:00:00Z',
  });
};

describe('RealtimeTradingView unavailable data', () => {
  it('does not render zero-valued market quotes or invented account balances when ticker is unavailable', async () => {
    stubEmptyAccount();
    vi.spyOn(apiClient, 'getMarketTicker').mockRejectedValue(new Error('ticker unavailable'));
    renderView();

    expect((await screen.findAllByText('Indisponível')).length).toBeGreaterThan(0);
    expect(screen.getByText(/Livro de Ofertas L1 \(indisponível\)/)).toBeInTheDocument();
    expect(screen.queryByText(/Topo Real/)).not.toBeInTheDocument();
    expect(screen.queryByText(/USDT \$10\.000,00|BTC 1\.0/)).not.toBeInTheDocument();
    expect(screen.getByText('Ticker indisponível')).toBeInTheDocument();
  });

  it('shows empty results only after successful order and trade queries', async () => {
    stubEmptyAccount();
    vi.spyOn(apiClient, 'getMarketTicker').mockRejectedValue(new Error('ticker unavailable'));
    renderView();

    expect(await screen.findByText('A API não retornou ordens abertas.')).toBeInTheDocument();
    expect(screen.getByText('A API não retornou execuções para esta consulta.')).toBeInTheDocument();
    expect(screen.queryByText(/motor de risco e o OMS estão com todas as regras/)).not.toBeInTheDocument();
  });

  it('does not report empty order or trade history when the endpoints fail', async () => {
    vi.spyOn(apiClient, 'getPortfolioBalances').mockRejectedValue(new Error('balances unavailable'));
    vi.spyOn(apiClient, 'getOpenOrders').mockRejectedValue(new Error('orders unavailable'));
    vi.spyOn(apiClient, 'getTrades').mockRejectedValue(new Error('trades unavailable'));
    vi.spyOn(apiClient, 'getCurrentMarketRegime').mockRejectedValue(new Error('regime unavailable'));
    vi.spyOn(apiClient, 'getMarketTicker').mockRejectedValue(new Error('ticker unavailable'));
    renderView();

    expect(await screen.findByText('Ordens indisponíveis.')).toBeInTheDocument();
    expect(screen.getByText('Execuções indisponíveis.')).toBeInTheDocument();
    expect(screen.getByText('Saldos indisponíveis.')).toBeInTheDocument();
    expect(screen.queryByText('A API não retornou ordens abertas.')).not.toBeInTheDocument();
  });
});
