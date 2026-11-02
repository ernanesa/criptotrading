import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '../api/client';
import { RiskControlView } from '../views/RiskControlView';

const originals = ['showModal', 'close'].map(
  (key) => [key, Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, key)] as const,
);
beforeEach(() => {
  vi.spyOn(apiClient, 'getActiveRiskPolicy').mockReturnValue(new Promise(() => {}));
  // jsdom does not implement the browser top layer, focus trap or Escape handling.
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.setAttribute('open', '');
      },
    },
    close: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.removeAttribute('open');
        this.dispatchEvent(new Event('close'));
      },
    },
  });
});
afterEach(() => {
  vi.restoreAllMocks();
  for (const [key, descriptor] of originals) {
    if (descriptor) Object.defineProperty(HTMLDialogElement.prototype, key, descriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, key);
  }
});

describe('read-only intervention dialog', () => {
  const open = () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <RiskControlView />
      </QueryClientProvider>,
    );
    const button = screen.getByRole('button', { name: 'Consultar protocolo de Kill Switch' });
    button.focus();
    fireEvent.click(button);
    return button;
  };

  it('has a name, moves focus and restores it after cancel or Escape', () => {
    const opener = open();
    expect(screen.getByRole('dialog', { name: 'Protocolo de Parada Emergencial' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancelar' })).toHaveFocus();
    fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(opener).toHaveFocus();
    fireEvent.click(opener);
    fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(opener).toHaveFocus();
  });

  it('shows local instructions without making a mutable request', () => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    open();
    fireEvent.click(screen.getByRole('button', { name: 'Exibir instruções locais' }));
    expect(screen.getByText(/Nenhuma intervenção foi enviada/)).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });
});
