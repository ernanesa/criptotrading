import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
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
