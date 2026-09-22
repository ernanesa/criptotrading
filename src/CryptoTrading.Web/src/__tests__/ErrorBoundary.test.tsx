import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { ErrorBoundary } from '../components/ErrorBoundary';

const ProblemChild: React.FC<{ shouldThrow?: boolean }> = ({ shouldThrow }) => {
  if (shouldThrow) {
    throw new Error('Test crash in UI component');
  }
  return <div>Operação normal do componente</div>;
};

describe('ErrorBoundary', () => {
  it('renders children when no error occurs', () => {
    render(
      <ErrorBoundary>
        <ProblemChild shouldThrow={false} />
      </ErrorBoundary>
    );

    expect(screen.getByText('Operação normal do componente')).toBeInTheDocument();
  });

  it('catches render error and displays correlation ID without secrets', () => {
    // Suppress console.error during expected throw
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ErrorBoundary>
        <ProblemChild shouldThrow={true} />
      </ErrorBoundary>
    );

    expect(screen.getByTestId('error-boundary-card')).toBeInTheDocument();
    expect(screen.getByText('Falha inesperada no console')).toBeInTheDocument();
    expect(screen.getByTestId('error-correlation-id')).toBeInTheDocument();

    consoleSpy.mockRestore();
  });
});
