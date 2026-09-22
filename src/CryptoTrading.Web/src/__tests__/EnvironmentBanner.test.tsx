import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { EnvironmentBanner } from '../components/EnvironmentBanner';
import type { OperationalStatusResponse } from '../types/api';

describe('EnvironmentBanner', () => {
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
