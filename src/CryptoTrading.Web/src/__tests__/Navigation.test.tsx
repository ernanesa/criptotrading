import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Navigation } from '../components/Navigation';

describe('Navigation', () => {
  it('highlights current tab and triggers onSelectTab on click', () => {
    const onSelectTab = vi.fn();

    render(<Navigation currentTab="overview" onSelectTab={onSelectTab} />);

    const decisionsTab = screen.getByTestId('nav-tab-decisions');
    expect(decisionsTab).toBeInTheDocument();

    fireEvent.click(decisionsTab);
    expect(onSelectTab).toHaveBeenCalledWith('decisions');
  });
});
