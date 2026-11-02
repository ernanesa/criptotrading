import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { EnvironmentBanner } from '../components/EnvironmentBanner';
import type { OperationalStatusResponse } from '../types/api';

describe('EnvironmentBanner', () => {
  it('does not infer an environment or Normal status from missing evidence', () => {
    render(<EnvironmentBanner />);
    expect(screen.getByTestId('env-badge')).toHaveTextContent('Unknown');
    expect(screen.getByTestId('status-badge-unknown')).toBeInTheDocument();
    expect(screen.queryByText('TESTNET')).not.toBeInTheDocument();
    expect(screen.queryByText(/API MUTÁVEL: DESATIVADA/)).not.toBeInTheDocument();
  });

  it('labels cached operational status after a failed refresh', () => {
    render(
      <EnvironmentBanner
        status={{
          source: 'test',
          mode: 'SafeMode',
          blocksNewEntries: true,
          revision: 2,
          changedAtUtc: '2026-11-02T00:00:00Z',
          changedBy: null,
          reason: null,
          mutatingEndpointsEnabled: false,
        }}
        isError
        dataUpdatedAt={Date.now() - 60_000}
      />,
    );
    expect(screen.getByTestId('status-badge-safemode')).toBeInTheDocument();
    expect(screen.getByText(/dados em cache desatualizados/i)).toBeInTheDocument();
  });

  it('renders normal operational state with environment name', () => {
    const status: OperationalStatusResponse = {
      source: 'api-process',
      mode: 'Normal',
      blocksNewEntries: false,
      revision: 1,
      changedAtUtc: '2026-09-21T12:00:00Z',
      changedBy: 'startup',
      reason: 'normal-operation',
      mutatingEndpointsEnabled: false,
    };

    render(<EnvironmentBanner status={status} environmentName="TESTNET" />);

    expect(screen.getByTestId('environment-banner')).toBeInTheDocument();
    expect(screen.getByTestId('env-badge')).toHaveTextContent('TESTNET');
    expect(screen.getByTestId('status-badge-normal')).toBeInTheDocument();
    expect(screen.getByText(/API MUTÁVEL: DESATIVADA/i)).toBeInTheDocument();
  });

  it('renders safe mode with entry block warning', () => {
    const status: OperationalStatusResponse = {
      source: 'api-process',
      mode: 'SafeMode',
      blocksNewEntries: true,
      revision: 2,
      changedAtUtc: '2026-09-21T12:05:00Z',
      changedBy: 'supervisor',
      reason: 'risk-breaker',
      mutatingEndpointsEnabled: false,
    };

    render(<EnvironmentBanner status={status} environmentName="TESTNET" />);

    expect(screen.getByTestId('status-badge-safemode')).toBeInTheDocument();
    expect(screen.getByText(/BLOQUEIO DE NOVAS ENTRADAS ATIVO/i)).toBeInTheDocument();
  });

  it('renders kill switch alert state', () => {
    const status: OperationalStatusResponse = {
      source: 'api-process',
      mode: 'KillSwitch',
      blocksNewEntries: true,
      revision: 3,
      changedAtUtc: '2026-09-21T12:10:00Z',
      changedBy: 'admin',
      reason: 'emergency-stop',
      mutatingEndpointsEnabled: false,
    };

    render(<EnvironmentBanner status={status} environmentName="TESTNET" />);

    expect(screen.getByTestId('status-badge-killswitch')).toBeInTheDocument();
    expect(screen.getByText(/KILL SWITCH ACIONADO/i)).toBeInTheDocument();
  });
});
