// @vitest-environment jsdom
/**
 * An automated accessibility check of the home screen's two layouts (design/atlas/document-list.md), the same axe-core pattern
 * as editorAxe.test.tsx and loginAxe.test.tsx: the grid of cards with thumbnails (loading, loaded and failed), the table, a
 * card's open kebab menu and a card being renamed. Colour contrast is off in jsdom, which has no layout; the chrome's pairs are
 * checked at token compile (themeContrast.ts, chromeContrastCoverage.test.ts).
 */

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { HOME_LAYOUT_KEY } from '../app/homeLayout.ts';
import { HomePage } from '../pages/HomePage.tsx';

const documents = [
  { id: 'doc_a', title: 'Order service', type: 'flowchart', workspace: 'default', createdAt: '2026-09-21T09:30:15.123Z', updatedAt: '2026-09-21T10:02:44.001Z' },
  { id: 'doc_s', title: 'Checkout', type: 'sequence', workspace: 'default', createdAt: '2026-09-22T09:30:15.123Z', updatedAt: '2026-09-22T10:02:44.001Z' },
  { id: 'doc_e', title: 'Shop schema', type: 'architecture', workspace: 'default', createdAt: '2026-09-23T09:30:15.123Z', updatedAt: '2026-09-23T10:02:44.001Z' },
];

async function violations(root: Element): Promise<string[]> {
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

async function openHome(): Promise<Element> {
  const { container } = render(<HomePage />);
  await waitFor(() => {
    expect(screen.getByText('Order service')).toBeTruthy();
  });
  return container;
}

describe('home screen accessibility (axe-core)', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ items: documents, total: 3, offset: 0, limit: 50 }), { status: 200 }))));
    window.history.replaceState(null, '', '/');
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('finds nothing on the grid while the thumbnails load', async () => {
    const container = await openHome();

    expect(container.querySelectorAll('.home-card')).toHaveLength(3);
    expect(container.querySelectorAll('.home-thumb[data-state="loading"]')).toHaveLength(3);
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the grid once the thumbnails have loaded, or when one could not be had', async () => {
    const container = await openHome();

    fireEvent.load(screen.getByRole('img', { name: 'Preview of Order service' }));
    fireEvent.error(screen.getByRole('img', { name: 'Preview of Checkout' }));

    expect(container.querySelectorAll('.home-thumb[data-state="loaded"]')).toHaveLength(1);
    expect(container.querySelectorAll('.home-thumb[data-state="failed"]')).toHaveLength(1);
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the grid with a card menu open', async () => {
    const container = await openHome();

    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));

    expect(screen.getByRole('menu', { name: 'Actions for Order service' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the grid with a card being renamed', async () => {
    const container = await openHome();
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));

    fireEvent.click(screen.getByRole('menuitem', { name: 'Rename' }));

    expect(screen.getByRole('textbox', { name: 'Rename Order service' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the table', async () => {
    window.localStorage.setItem(HOME_LAYOUT_KEY, 'list');
    const container = await openHome();

    expect(container.querySelector('table')).not.toBeNull();
    expect(await violations(container)).toEqual([]);
  });
});
