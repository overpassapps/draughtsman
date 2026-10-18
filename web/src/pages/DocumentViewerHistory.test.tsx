// @vitest-environment jsdom
/**
 * The History and Review tabs inside the whole editor (design/atlas/version-history.md, critique-panel.md): they sit in the
 * panel's view switcher, read the server only when opened, and compare marks appear on the canvas and go again.
 */
import './loadTolerance.ts';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { must } from '../model/sequenceFixture.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const populated = parseDocument(JSON.parse(JSON.stringify(orderService)));
const someNode = must(populated.nodes.find((node) => node.geometry !== undefined));

function stubServer(calls: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      calls.push(`${init?.method ?? 'GET'} ${url}`);
      if (url === '/api/health') {
        return Promise.resolve(new Response(JSON.stringify({ export: { pdf: false }, ai: { enabled: false } }), { status: 200 }));
      }
      if (url.includes('/versions?')) {
        const items = [{ id: 'v_1', documentId: populated.id, name: 'Baseline', createdAt: new Date().toISOString(), actor: 'user:local', size: 1000 }];
        return Promise.resolve(new Response(JSON.stringify({ items, total: 1, offset: 0, limit: 50 }), { status: 200 }));
      }
      if (url.includes('/diff?')) {
        const diff = { addedNodes: [], removedNodes: [], changedNodes: [{ id: someNode.id, fields: [{ field: 'label' }] }], addedEdges: [], removedEdges: [], changedEdges: [] };
        return Promise.resolve(new Response(JSON.stringify({ fromVersion: 'v_1', toVersion: 'current', diff }), { status: 200 }));
      }
      if (url === '/api/critique') {
        return Promise.resolve(new Response(JSON.stringify({ findings: [{ rule: 'isolated-node', severity: 'warning', message: 'Nothing connects to this node.', nodeIds: [someNode.id], edgeIds: [] }] }), { status: 200 }));
      }
      return Promise.resolve(new Response('{}', { status: 200 }));
    }),
  );
}

async function mount(document: Document) {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  await act(() => Promise.resolve());
  return { ...view, canvas };
}

describe('DocumentViewer history and review', () => {
  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    window.localStorage.clear();
    window.history.replaceState(null, '', `/d/${populated.id}`);
  });
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    window.localStorage.clear();
    window.history.replaceState(null, '', '/');
  });

  it('reads neither the history nor the critique until their tab is opened', async () => {
    const calls: string[] = [];
    stubServer(calls);
    const { container } = await mount(populated);

    expect(calls.some((call) => call.includes('/versions') || call.includes('/api/critique'))).toBe(false);
    const switcher = must(container.querySelector<HTMLElement>('[aria-label="Panel view"]'));
    expect(within(switcher).getAllByRole('button').map((button) => button.textContent)).toEqual(['Properties', 'Text', 'Tour', 'History', 'Review']);

    fireEvent.click(within(switcher).getByRole('button', { name: 'History' }));
    await screen.findByText('Baseline');
    expect(within(switcher).getByRole('button', { name: 'History' }).getAttribute('aria-pressed')).toBe('true');
    expect(calls.some((call) => call.includes('/versions?'))).toBe(true);
    expect(calls.some((call) => call.includes('/api/critique'))).toBe(false);

    fireEvent.click(within(switcher).getByRole('button', { name: 'Review' }));
    expect(await screen.findByText(/Nothing connects to this node/)).toBeTruthy();
    expect(calls.filter((call) => call === 'POST /api/critique')).toHaveLength(1);
  });

  it('marks what changed on the canvas while a comparison is showing, and clears the marks when it is stopped or the tab is left', async () => {
    stubServer([]);
    const { container } = await mount(populated);
    fireEvent.click(screen.getByRole('button', { name: 'History' }));
    await screen.findByText('Baseline');
    expect(container.querySelector('.compare-marks')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Compare with Baseline' }));
    await waitFor(() => {
      expect(container.querySelector(`[data-compare="changed"][data-compare-id="${someNode.id}"]`)).not.toBeNull();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Stop comparing' }));
    await waitFor(() => {
      expect(container.querySelector('.compare-marks')).toBeNull();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Compare with Baseline' }));
    await waitFor(() => {
      expect(container.querySelector('.compare-marks')).not.toBeNull();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Properties' }));
    await waitFor(() => {
      expect(container.querySelector('.compare-marks')).toBeNull();
    });
  });

  it('is listed in the command palette as a View command like the other panels', async () => {
    stubServer([]);
    await mount(populated);

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    const dialog = await screen.findByRole('dialog', { name: /command palette/i });
    fireEvent.change(within(dialog).getByRole('combobox'), { target: { value: 'history' } });

    expect(within(dialog).getByText('Toggle History panel')).toBeTruthy();
    fireEvent.change(within(dialog).getByRole('combobox'), { target: { value: 'review' } });
    expect(within(dialog).getByText('Toggle Review panel')).toBeTruthy();
  });
});
