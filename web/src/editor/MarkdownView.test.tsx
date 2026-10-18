// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { MarkdownView } from './MarkdownView.tsx';

it('renders a known node id as a button that names the node and reports the click', () => {
  const onCitation = vi.fn();
  render(<MarkdownView markdown="The cart [n_cart] talks to payment [n_missing]." nodeIds={new Set(['n_cart'])} onCitation={onCitation} />);

  const button = screen.getByRole('button', { name: 'n_cart' });
  // Two-sided: an id the diagram does not have is not interactive - it stays in the text.
  expect(screen.queryByRole('button', { name: 'n_missing' })).toBeNull();
  fireEvent.click(button);
  expect(onCitation).toHaveBeenCalledWith('n_cart');
});

it('shows a citation as the node\'s label, never its id, and keeps the id for the click (P9)', () => {
  const onCitation = vi.fn();
  render(<MarkdownView markdown="The cart [n_sign_in] talks to [n_unlabelled]." nodeIds={new Set(['n_sign_in', 'n_unlabelled'])} labels={new Map([['n_sign_in', 'Sign in']])} onCitation={onCitation} />);
  const labelled = screen.getByRole('button', { name: 'Sign in' });
  expect(labelled.textContent).toBe('Sign in');
  expect(labelled.getAttribute('title')).toBe('Show Sign in on the canvas');
  expect(screen.queryByText('n_sign_in')).toBeNull();
  // A node with no label falls back to its id rather than an empty button.
  expect(screen.getByRole('button', { name: 'n_unlabelled' })).toBeTruthy();
  fireEvent.click(labelled);
  expect(onCitation).toHaveBeenCalledWith('n_sign_in');
});

it('renders headings and list items from the model markdown', () => {
  render(<MarkdownView markdown={'# Runbook\n\n- step one\n- step two\n'} nodeIds={new Set()} onCitation={() => undefined} />);

  expect(screen.getByRole('heading', { level: 1, name: 'Runbook' })).toBeTruthy();
  expect(screen.getByText('step one')).toBeTruthy();
  expect(screen.getByText('step two')).toBeTruthy();
});
