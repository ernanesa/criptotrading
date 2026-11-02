import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ErrorBoundary } from '../components/ErrorBoundary';

const ProblemChild: React.FC<{ shouldThrow?: boolean }> = ({ shouldThrow }) => {
  if (shouldThrow) {
    throw new Error('Test crash in UI component');
  }
  return <div>Operação normal do componente</div>;
};

describe('ErrorBoundary', () => {
  it('recovers when the selected view changes', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { rerender } = render(
      <ErrorBoundary resetKey="broken">
        <ProblemChild shouldThrow />
      </ErrorBoundary>,
    );
    expect(screen.getByTestId('error-boundary-card')).toBeInTheDocument();
    rerender(
      <ErrorBoundary resetKey="healthy">
        <ProblemChild />
      </ErrorBoundary>,
    );
    expect(screen.getByText('Operação normal do componente')).toBeInTheDocument();
    spy.mockRestore();
  });

  it('logs only safe diagnostic metadata with the same ID shown to the operator', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const SecretChild = () => {
      throw new Error('credential=never-expose');
    };
    const { container } = render(
      <ErrorBoundary>
        <SecretChild />
      </ErrorBoundary>,
    );
    const id = screen.getByTestId('error-correlation-id').textContent;
    const ownLogs = spy.mock.calls.filter(([event]) => event === 'ui.render_failure');
    expect(ownLogs).toEqual([['ui.render_failure', { correlationId: id, source: 'ErrorBoundary' }]]);
    expect(container).not.toHaveTextContent('credential=never-expose');
    expect(screen.queryByText(/Exibir detalhes técnicos/)).not.toBeInTheDocument();
    spy.mockRestore();
  });

  it('retries locally without reloading the application', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    let broken = true;
    const Recoverable = () => {
      if (broken) throw new Error('crash');
      return <div>Recovered</div>;
    };
    render(
      <ErrorBoundary>
        <Recoverable />
      </ErrorBoundary>,
    );
    broken = false;
    fireEvent.click(screen.getByRole('button', { name: 'Tentar Novamente' }));
    expect(screen.getByText('Recovered')).toBeInTheDocument();
    spy.mockRestore();
  });

  it('renders children when no error occurs', () => {
    render(
      <ErrorBoundary>
        <ProblemChild shouldThrow={false} />
      </ErrorBoundary>,
    );

    expect(screen.getByText('Operação normal do componente')).toBeInTheDocument();
  });

  it('catches render error and displays correlation ID without secrets', () => {
    // Suppress console.error during expected throw
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ErrorBoundary>
        <ProblemChild shouldThrow={true} />
      </ErrorBoundary>,
    );

    expect(screen.getByTestId('error-boundary-card')).toBeInTheDocument();
    expect(screen.getByText('Falha inesperada no console')).toBeInTheDocument();
    expect(screen.getByTestId('error-correlation-id')).toBeInTheDocument();

    consoleSpy.mockRestore();
  });
});
