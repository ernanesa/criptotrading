import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiClient } from '../api/client';
import { DecisionTraceView } from '../views/DecisionTraceView';
import { RiskControlView } from '../views/RiskControlView';

afterEach(() => vi.restoreAllMocks());
const wrap = (view: React.ReactNode) =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      {view}
    </QueryClientProvider>,
  );

describe('operational error states', () => {
  it('shows RiskPolicy loading without inventing a document', () => {
    vi.spyOn(apiClient, 'getActiveRiskPolicy').mockReturnValue(new Promise(() => {}));
    wrap(<RiskControlView />);
    expect(screen.getByText('RiskPolicy: carregando...')).toBeInTheDocument();
    expect(screen.queryByText(/RiskPolicy Document v/)).not.toBeInTheDocument();
  });

  it.each([403, 404, 503, 500])('shows a safe RiskPolicy error for HTTP %s', async (status) => {
    vi.spyOn(apiClient, 'getActiveRiskPolicy').mockRejectedValue(new ApiError(status, 'credential=hidden', 'risk-id'));
    wrap(<RiskControlView />);
    expect(await screen.findByText(/RiskPolicy: consulta indisponível/)).toBeInTheDocument();
    expect(screen.queryByText(/credential=hidden|PostgreSQL|POSTGRES_CONNECTION_STRING/)).not.toBeInTheDocument();
  });

  it.each([403, 503, 500])('shows a safe Decision error for HTTP %s', async (status) => {
    vi.spyOn(apiClient, 'getOperationalDecision').mockRejectedValue(
      new ApiError(status, 'credential=hidden', 'decision-id'),
    );
    wrap(<DecisionTraceView />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'ad-test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Buscar Decisão' }));
    expect(await screen.findByText(/Decisão: consulta indisponível/)).toBeInTheDocument();
    expect(screen.queryByText(/credential=hidden|PostgreSQL|POSTGRES_CONNECTION_STRING/)).not.toBeInTheDocument();
  });
});
