import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ResearchDashboardView } from '../views/ResearchDashboardView';

describe('ResearchDashboardView evidence boundary', () => {
  it('does not present numerical outcomes or chart series without loaded research artifacts', () => {
    const { container } = render(<ResearchDashboardView />);
    const unavailable = screen.getByTestId('research-evidence-unavailable');

    expect(unavailable).toHaveTextContent('Indisponível');
    expect(unavailable).toHaveTextContent(/resultados e as séries do benchmark não foram acessados/);
    expect(unavailable).not.toHaveTextContent(/\d+(?:[.,]\d+)?\s*%/);
    expect(container.querySelector('polyline, polygon')).toBeNull();
  });
});
