// @vitest-environment jsdom
/**
 * The home screen's grid of cards with thumbnails and its Grid/List toggle (HomePage.tsx, DocumentCards.tsx,
 * DocumentThumbnail.tsx; design/atlas/document-list.md). The table's own behaviour is HomePage.test.tsx.
 */

import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { HOME_LAYOUT_KEY } from '../app/homeLayout.ts';
import { DocumentThumbnail } from './DocumentThumbnail.tsx';
import { HomePage } from './HomePage.tsx';

const flowchart = { id: 'doc_a', title: 'Order service', type: 'flowchart' as const, workspace: 'default', createdAt: '2026-09-21T09:30:15.123Z', updatedAt: '2026-09-21T10:02:44.001Z' };
const sequence = { id: 'doc_s', title: 'Checkout', type: 'sequence' as const, workspace: 'default', createdAt: '2026-09-22T09:30:15.123Z', updatedAt: '2026-09-22T10:02:44.001Z' };

function listResponse(...items: (typeof flowchart | typeof sequence)[]): Response {
  return new Response(JSON.stringify({ items, total: items.length, offset: 0, limit: 50 }), { status: 200 });
}

function thumbnailOf(title: string): HTMLImageElement {
  return screen.getByRole('img', { name: `Preview of ${title}` });
}

describe('HomePage grid and layout toggle', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse(flowchart, sequence))));
    window.history.replaceState(null, '', '/');
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function openHome(): Promise<void> {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
  }

  it('opens on the grid: a list of cards, no table, with Grid pressed', async () => {
    await openHome();

    const cards = screen.getByRole('list', { name: 'Documents' });
    expect(within(cards).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.queryByRole('table')).toBeNull();
    const layout = screen.getByRole('group', { name: 'Layout' });
    expect(within(layout).getByRole('button', { name: 'Grid' }).getAttribute('aria-pressed')).toBe('true');
    expect(within(layout).getByRole('button', { name: 'List' }).getAttribute('aria-pressed')).toBe('false');
  });

  it('shows each document as a lazy image of its thumbnail with its size set and its title in the alt text', async () => {
    await openHome();

    const image = thumbnailOf('Order service');
    // The address carries the last-modified time, so an edited document is a different address and shows its new picture.
    expect(image.getAttribute('src')).toBe('/api/documents/doc_a/thumbnail?v=2026-09-21T10%3A02%3A44.001Z');
    expect(image.getAttribute('loading')).toBe('lazy');
    expect(image.getAttribute('width')).toBe('320');
    expect(image.getAttribute('height')).toBe('200');
    expect(thumbnailOf('Checkout').getAttribute('src')).toBe('/api/documents/doc_s/thumbnail?v=2026-09-22T10%3A02%3A44.001Z');
  });

  it('keeps a neutral skeleton over the box until the image has loaded, then shows the image alone', async () => {
    await openHome();
    const thumb = thumbnailOf('Order service').closest('.home-thumb');

    expect(thumb?.getAttribute('data-state')).toBe('loading');
    expect(thumb?.querySelector('.home-thumb-skeleton')).not.toBeNull();

    fireEvent.load(thumbnailOf('Order service'));

    expect(thumb?.getAttribute('data-state')).toBe('loaded');
    expect(thumb?.querySelector('.home-thumb-skeleton')).toBeNull();
    // The other card is still waiting: each one tracks its own picture.
    expect(thumbnailOf('Checkout').closest('.home-thumb')?.getAttribute('data-state')).toBe('loading');
  });

  it('shows the type glyph instead of a broken image when the thumbnail cannot be had', async () => {
    await openHome();
    const thumb = thumbnailOf('Order service').closest('.home-thumb');

    fireEvent.error(thumbnailOf('Order service'));

    expect(thumb?.getAttribute('data-state')).toBe('failed');
    expect(thumb?.querySelector('img')).toBeNull();
    const fallback = thumb?.querySelector('.home-thumb-fallback');
    expect(fallback?.querySelector('svg')).not.toBeNull();
    // Decoration only: nothing for a screen reader to stumble over.
    expect(fallback?.getAttribute('aria-hidden')).toBe('true');
  });

  it('switches to the table and back, remembers the choice for this browser and opens on it next time', async () => {
    await openHome();

    fireEvent.click(screen.getByRole('button', { name: 'List' }));

    expect(screen.getByRole('table')).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Documents' })).toBeNull();
    expect(screen.queryByRole('img', { name: 'Preview of Order service' })).toBeNull();
    expect(window.localStorage.getItem(HOME_LAYOUT_KEY)).toBe('list');
    expect(screen.getByRole('button', { name: 'List' }).getAttribute('aria-pressed')).toBe('true');

    // A new visit (a fresh mount) opens on the table the person chose.
    cleanup();
    await openHome();
    expect(screen.getByRole('table')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Grid' }));
    expect(window.localStorage.getItem(HOME_LAYOUT_KEY)).toBe('grid');
    expect(screen.getByRole('list', { name: 'Documents' })).toBeTruthy();
  });

  it('uses the grid and keeps working when this browser will not let a page remember anything', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('storage blocked'); });

    await openHome();
    expect(screen.getByRole('list', { name: 'Documents' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'List' }));
    expect(screen.getByRole('table')).toBeTruthy();
  });

  it('treats a stored value it does not know as the grid', async () => {
    window.localStorage.setItem(HOME_LAYOUT_KEY, 'carousel');

    await openHome();

    expect(screen.getByRole('list', { name: 'Documents' })).toBeTruthy();
  });

  it('gives a card the accessible names and the focus order a table row has: the title opens it, then its actions', async () => {
    await openHome();
    const card = screen.getByText('Order service').closest('li');
    if (card === null) {
      throw new Error('card not rendered');
    }

    const open = within(card).getByRole('button', { name: 'Order service' });
    const actions = within(card).getByRole('button', { name: 'Actions for Order service' });
    expect(within(card).getByText('Flowchart')).toBeTruthy();
    // Tab order is document order: the title, then the actions menu. The picture is not a focus stop of its own.
    const focusable = [...card.querySelectorAll<HTMLElement>('button, a[href], input, select, textarea, [tabindex]')];
    expect(focusable).toEqual([open, actions]);
    expect(card.querySelector('img')?.getAttribute('tabindex')).toBeNull();

    fireEvent.click(open);
    expect(window.location.pathname).toBe('/d/doc_a');
  });

  it('offers the same actions from a card as from a row, and renames in place', async () => {
    await openHome();
    const fetchSpy = vi.mocked(fetch);

    const card = screen.getByText('Order service').closest('li');
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));
    const menu = screen.getByRole('menu', { name: 'Actions for Order service' });
    expect(within(menu).getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Rename', 'Duplicate', 'Delete']);

    fetchSpy.mockResolvedValueOnce(new Response(null, { status: 200, headers: { ETag: '"dv1:abc"' } }));
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Rename' }));
    const field = screen.getByRole('textbox', { name: 'Rename Order service' });
    // In the card it was opened from, not somewhere else on the page.
    expect(field.closest('li')).toBe(card);
    fireEvent.change(field, { target: { value: 'Orders' } });
    fetchSpy.mockResolvedValueOnce(new Response(null, { status: 204 }));
    fireEvent.keyDown(field, { key: 'Enter' });

    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledWith('/api/documents/doc_a', expect.objectContaining({ method: 'PATCH' }));
    });
  });

  it('moves a card to the bin from its menu, with the same Undo line', async () => {
    await openHome();
    const fetchSpy = vi.mocked(fetch);
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));
    fetchSpy.mockResolvedValueOnce(new Response(null, { status: 204 }));

    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Moved “Order service” to the bin.');
    });
    expect(fetchSpy).toHaveBeenCalledWith('/api/documents/doc_a', expect.objectContaining({ method: 'DELETE' }));
  });

  it('sorts the grid from a Sort by menu, as the table does from its column headers, and keeps the order across layouts', async () => {
    await openHome();
    expect(screen.queryByRole('columnheader')).toBeNull();
    const sortBy = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Sort by' });
    expect(sortBy.value).toBe('modified:desc');

    fireEvent.change(sortBy, { target: { value: 'title:asc' } });
    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents?sort=title&dir=asc', expect.anything());
    });

    fireEvent.click(screen.getByRole('button', { name: 'List' }));
    expect(screen.queryByRole('combobox', { name: 'Sort by' })).toBeNull();
    expect(screen.getByRole('columnheader', { name: /Title/ }).getAttribute('aria-sort')).toBe('ascending');
  });

  it('shows the layout toggle with the search and filter, never on the first-run empty state', async () => {
    vi.mocked(fetch).mockResolvedValue(listResponse());
    render(<HomePage />);

    await screen.findByRole('heading', { name: 'No documents yet' });

    expect(screen.queryByRole('group', { name: 'Layout' })).toBeNull();
    expect(screen.queryByRole('list', { name: 'Documents' })).toBeNull();
  });

  it('keeps the search and the type filter working in the grid', async () => {
    await openHome();
    vi.mocked(fetch).mockResolvedValue(listResponse(sequence));

    fireEvent.change(screen.getByRole('combobox', { name: 'Show only one type' }), { target: { value: 'sequence' } });

    await waitFor(() => {
      expect(screen.queryByText('Order service')).toBeNull();
    });
    expect(screen.getByText('Checkout')).toBeTruthy();
  });
});

describe('DocumentThumbnail', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('asks again, behind the skeleton, when the document is edited and its address changes', () => {
    const { rerender } = render(<DocumentThumbnail id="doc_a" title="Orders" updatedAt="2026-09-21T10:00:00.000Z" fallback={null} />);
    fireEvent.load(screen.getByRole('img', { name: 'Preview of Orders' }));
    expect(document.querySelector('.home-thumb')?.getAttribute('data-state')).toBe('loaded');

    rerender(<DocumentThumbnail id="doc_a" title="Orders" updatedAt="2026-09-21T11:00:00.000Z" fallback={null} />);

    expect(screen.getByRole('img', { name: 'Preview of Orders' }).getAttribute('src')).toContain('v=2026-09-21T11%3A00%3A00.000Z');
    expect(document.querySelector('.home-thumb')?.getAttribute('data-state')).toBe('loading');
  });

  it('treats an image the browser already holds as loaded without waiting for an event that has already gone by', () => {
    vi.spyOn(HTMLImageElement.prototype, 'complete', 'get').mockReturnValue(true);
    vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(320);

    render(<DocumentThumbnail id="doc_a" title="Orders" updatedAt="2026-09-21T10:00:00.000Z" fallback={null} />);

    expect(document.querySelector('.home-thumb')?.getAttribute('data-state')).toBe('loaded');
  });

  it('escapes an id and a time that are not plain, so the address cannot be bent', () => {
    render(<DocumentThumbnail id="a/b?c" title="Odd" updatedAt="x&y=1" fallback={null} />);

    expect(screen.getByRole('img', { name: 'Preview of Odd' }).getAttribute('src')).toBe('/api/documents/a%2Fb%3Fc/thumbnail?v=x%26y%3D1');
  });
});
