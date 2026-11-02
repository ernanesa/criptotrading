import { createRoot } from 'react-dom/client';
import { generateCorrelationId } from './api/client';

export function createSafeRoot(container: Element | DocumentFragment) {
  return createRoot(container, {
    // ErrorBoundary owns the single correlated report for caught render errors.
    onCaughtError: () => {},
    onUncaughtError: () => console.error('ui.uncaught_failure', { correlationId: generateCorrelationId() }),
    onRecoverableError: () => console.error('ui.recoverable_failure', { correlationId: generateCorrelationId() }),
  });
}
