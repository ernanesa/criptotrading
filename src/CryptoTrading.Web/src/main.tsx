import { StrictMode } from 'react';
import { createSafeRoot } from './root';
import './index.css';
import { App } from './App';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root element #root was not found in the DOM.');
}

const root = createSafeRoot(container);
root.render(
  <StrictMode>
    <App />
  </StrictMode>,
);
