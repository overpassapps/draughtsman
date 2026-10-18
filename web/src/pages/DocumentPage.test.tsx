// @vitest-environment jsdom
import { act, render, screen } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { createAndOpen } from '../app/newDocument.ts';
import { DocumentPage } from './DocumentPage.tsx';

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

describe('DocumentPage', () => {
  beforeEach(() => {
    // jsdom has no 2D canvas; the viewer falls back to the heuristic measurer, without the "not implemented" noise.
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('opens a diagram just created from New diagram with the title input already active', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('{"id":"doc_new"}', { status: 201 }))));
    await createAndOpen('flowchart');

    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ ...orderService, id: 'doc_new' }), { status: 200, headers: { ETag: '"dv1:new"' } }));
    render(<DocumentPage id="doc_new" />);
    await flush();

    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Diagram title' });
    expect(input.selectionStart).toBe(0);
    expect(input.selectionEnd).toBe(input.value.length);
  });

  it('opens an existing diagram, not just created, with the title shown as plain text, not an input', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(orderService), { status: 200, headers: { ETag: '"dv1:loaded"' } }))));
    render(<DocumentPage id={orderService.id} />);
    await flush();

    expect(screen.queryByRole('textbox', { name: 'Diagram title' })).toBeNull();
    expect(screen.getByRole('button', { name: orderService.title })).toBeTruthy();
  });
});
