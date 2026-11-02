import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { QueryStatus } from '../components/QueryStatus';

describe('QueryStatus', () => {
  it('distinguishes receipt time from the age of the source snapshot', () => {
    render(
      <QueryStatus
        label="Dados"
        observedAt="2020-01-01T00:00:00Z"
        query={{ data: [], isPending: false, isError: false, isFetching: false, dataUpdatedAt: Date.now() }}
      />,
    );
    expect(screen.getByText(/Snapshot de origem: 2020-01-01T00:00:00.000Z/)).toHaveTextContent('Snapshot antigo');
  });
  it('retains but clearly marks cached data when a refetch fails', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['sample'], 'cached');
    const View = () => {
      const query = useQuery({
        queryKey: ['sample'],
        queryFn: async () => {
          throw new Error('credential=hidden');
        },
        enabled: false,
      });
      return (
        <>
          <QueryStatus label="Dados" query={query} />
          <p>{query.data}</p>
        </>
      );
    };
    render(
      <QueryClientProvider client={client}>
        <View />
      </QueryClientProvider>,
    );
    await act(async () => {
      await client
        .fetchQuery({
          queryKey: ['sample'],
          queryFn: async () => {
            throw new Error('credential=hidden');
          },
        })
        .catch(() => {});
    });
    expect(await screen.findByText(/dados em cache desatualizados/)).toBeInTheDocument();
    expect(screen.getByText('cached')).toBeInTheDocument();
    expect(screen.queryByText(/credential=hidden/)).not.toBeInTheDocument();
    expect(screen.getByText(/Última atualização/)).toBeInTheDocument();
    client.clear();
  });

  it('marks aged data even without a failed request', () => {
    render(
      <QueryStatus
        label="Dados"
        query={{ data: [], isPending: false, isError: false, isFetching: false, dataUpdatedAt: Date.now() - 60_000 }}
      />,
    );
    expect(screen.getByText(/Dados antigos/)).toBeInTheDocument();
  });

  it('updates the displayed age as time passes', async () => {
    vi.useFakeTimers();
    try {
      render(
        <QueryStatus
          label="Dados"
          query={{ data: [], isPending: false, isError: false, isFetching: false, dataUpdatedAt: Date.now() }}
        />,
      );
      expect(screen.queryByText(/Dados antigos/)).not.toBeInTheDocument();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(35_000);
      });
      expect(screen.getByText(/idade 35s.*Dados antigos/)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});
