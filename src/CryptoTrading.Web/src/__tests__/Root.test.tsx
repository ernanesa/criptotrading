import { act } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ErrorBoundary } from '../components/ErrorBoundary';
import { createSafeRoot } from '../root';

describe('application root error reporting', () => {
  it('does not let React log raw caught errors outside the boundary reporter', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const element = document.createElement('div');
    const root = createSafeRoot(element);
    const Broken = () => {
      throw new Error('credential=sentinel');
    };
    try {
      await act(async () =>
        root.render(
          <ErrorBoundary>
            <Broken />
          </ErrorBoundary>,
        ),
      );
      expect(spy.mock.calls).toEqual([
        [
          'ui.render_failure',
          {
            correlationId: element.querySelector('[data-testid="error-correlation-id"]')?.textContent,
            source: 'ErrorBoundary',
          },
        ],
      ]);
      expect(element).not.toHaveTextContent('credential=sentinel');
    } finally {
      await act(async () => root.unmount());
      spy.mockRestore();
    }
  });
});
