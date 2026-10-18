// @vitest-environment jsdom
/**
 * Autosave in the whole editor (design/atlas/editor-safety.md, "Autosave"): the status the user reads, the leave guard
 * staying honest while saves are made, failing or refused, and the three answers to a stale save. The hook's timing and
 * ordering rules are pinned in editor/useDocumentEditorAutosave.test.ts; this is what the page shows and does about them.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { navigate } from '../app/router.ts';
import { readLocalCopies, storeLocalCopy } from '../editor/localCopy.ts';
import { parseDocument } from '../model/parse.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const document = parseDocument(orderService);
const firstId = document.nodes[0]?.id ?? '';

/** Short enough to wait out; the retry backoff doubles it (40, 80, ...). */
const DELAY = 40;

function renderEditor() {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} autosaveDelayMs={DELAY} />);
  const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
  const shape = view.container.querySelector(`g[data-id="${firstId}"] .shape`);
  if (!canvas || !shape) {
    throw new Error('editor not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  return { ...view, canvas, shape };
}

/** Selects the first node and nudges it one step: an unsaved change. */
function makeDirty(canvas: HTMLElement, shape: Element): void {
  fireEvent.pointerDown(shape, { pointerId: 7, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
  fireEvent.pointerUp(canvas, { pointerId: 7 });
  fireEvent.keyDown(canvas, { key: 'ArrowRight' });
}

const status = () => screen.getByText(/^(Saved|Saving…|Unsaved changes|Not saved|Not saved, retrying…|Out of date)$/).textContent;
const path = () => window.location.pathname;
const beforeUnloadPrevented = (): boolean => {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
};

/** A server with one document at version n; a PUT quoting anything else is refused as stale, as the real one does. */
function server(behaviour: { put?: number } = {}) {
  let version = 1;
  const state = { putStatus: behaviour.put ?? 200, serverLabel: 'theirs', movedNode: false };
  const puts: { ifMatch: string | null; body: typeof document }[] = [];
  const posts: unknown[] = [];
  const gets: (string | null)[] = [];
  const etag = () => (version === 1 ? '"dv1:loaded"' : `"dv1:${String(version)}"`);
  // `movedNode` is the other writer touching the very field the test's own edit changes (the first node's position), a real clash;
  // without it their change (the title) is one a save can take in by itself (QA S10) and no question is asked.
  const theirs = () => ({
    ...orderService,
    title: state.serverLabel,
    nodes: orderService.nodes.map((node, index) => (state.movedNode && index === 0 ? { ...node, geometry: { ...node.geometry, x: node.geometry.x + 500 } } : node)),
  });
  const fetchMock = vi.fn<typeof fetch>((_input, init) => {
    const method = init?.method ?? 'GET';
    if (method === 'PUT') {
      const ifMatch = new Headers(init?.headers).get('If-Match');
      puts.push({ ifMatch, body: JSON.parse(init?.body as string) as typeof document });
      if (state.putStatus !== 200) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: state.putStatus }));
      }
      if (ifMatch !== etag()) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: 412 }));
      }
      version += 1;
      return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: etag() } }));
    }
    if (method === 'POST') {
      posts.push(JSON.parse(init?.body as string));
      return Promise.resolve(new Response('{"id":"doc_copy"}', { status: 201 }));
    }
    gets.push(new Headers(init?.headers).get('If-None-Match'));
    return Promise.resolve(new Response(JSON.stringify(theirs()), { status: 200, headers: { ETag: etag() } }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    state,
    puts,
    posts,
    gets,
    /** Another writer saved: the version moves on, and a read returns their title. */
    otherWriterSaves(title = 'Changed elsewhere', clash = false) {
      version += 1;
      state.serverLabel = title;
      state.movedNode = clash;
    },
    get etag() {
      return etag();
    },
  };
}

describe('autosave in the editor', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    window.history.replaceState(null, '', `/d/${document.id}`);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('goes from Unsaved changes to Saving to Saved by itself, with one PUT quoting the loaded version', async () => {
    const s = server();
    const { canvas, shape } = renderEditor();
    expect(status()).toBe('Saved');
    makeDirty(canvas, shape);
    expect(status()).toBe('Unsaved changes');
    expect(s.puts).toHaveLength(0);

    await waitFor(() => { expect(status()).toBe('Saved'); });
    expect(s.puts).toHaveLength(1);
    expect(s.puts[0]?.ifMatch).toBe('"dv1:loaded"');
  });

  it('asks the browser to confirm closing the tab while unsaved, and not once everything is saved', async () => {
    server();
    const { canvas, shape } = renderEditor();
    expect(beforeUnloadPrevented()).toBe(false);
    makeDirty(canvas, shape);
    expect(beforeUnloadPrevented()).toBe(true);
    await waitFor(() => { expect(status()).toBe('Saved'); });
    expect(beforeUnloadPrevented()).toBe(false);
  });

  it('keeps asking the browser while a save is failing, shows that it will retry, and stops once the retry lands', async () => {
    const s = server({ put: 503 });
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    await waitFor(() => { expect(status()).toBe('Not saved, retrying…'); });
    expect(beforeUnloadPrevented()).toBe(true);
    expect(screen.getByRole('alert').textContent).toContain('503');

    s.state.putStatus = 200;
    await waitFor(() => { expect(status()).toBe('Saved'); });
    expect(beforeUnloadPrevented()).toBe(false);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('saves first when the user navigates away inside the app, and leaves without asking once it is stored', async () => {
    const s = server();
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    act(() => { navigate('/'); });

    await waitFor(() => { expect(path()).toBe('/'); });
    expect(s.puts).toHaveLength(1);
    expect(screen.queryByRole('dialog', { name: 'Unsaved changes' })).toBeNull();
  });

  it('still asks, with the reason, when leaving and the save it tried first fails', async () => {
    const s = server({ put: 500 });
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    act(() => { navigate('/'); });

    const dialog = await screen.findByRole('dialog', { name: 'Unsaved changes' });
    expect(within(dialog).getByRole('alert').textContent).toContain('500');
    expect(path()).toBe(`/d/${document.id}`);
    expect(s.puts.length).toBeGreaterThan(0);
  });

  it('does not save, or ask, for a document shown without an id (a preview)', async () => {
    const s = server();
    render(<DocumentViewer document={document} autosaveDelayMs={DELAY} />);
    await act(() => new Promise((resolve) => setTimeout(resolve, DELAY * 4)));
    expect(s.puts).toHaveLength(0);
    expect(screen.queryByText('Saved')).toBeNull();
  });
});

describe('a save refused as stale, in the editor', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    window.history.replaceState(null, '', `/d/${document.id}`);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  /** Edits, with the server already moved on, and waits for the question. */
  async function refusedEditor() {
    const s = server();
    s.otherWriterSaves('Changed elsewhere', true);
    const view = renderEditor();
    makeDirty(view.canvas, view.shape);
    const bar = await screen.findByTestId('stale-save');
    return { s, bar, ...view };
  }

  it('shows the three answers and Out of date, stops saving, and keeps the tab guarded', async () => {
    const { s, bar } = await refusedEditor();
    expect(status()).toBe('Out of date');
    expect(within(bar).getByRole('button', { name: 'Reload theirs' })).toBeTruthy();
    expect(within(bar).getByRole('button', { name: 'Keep mine…' })).toBeTruthy();
    expect(within(bar).getByRole('button', { name: 'Save mine as a new document' })).toBeTruthy();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save' }).disabled).toBe(true);
    expect(beforeUnloadPrevented()).toBe(true);

    await act(() => new Promise((resolve) => setTimeout(resolve, DELAY * 6)));
    expect(s.puts).toHaveLength(1);
    // Leaving is refused too: the question is the same one the guard asks, with the reason.
    act(() => { navigate('/'); });
    expect(await screen.findByRole('dialog', { name: 'Unsaved changes' })).toBeTruthy();
    expect(path()).toBe(`/d/${document.id}`);
  });

  it('asks before overwriting: Keep mine… writes nothing until Overwrite with mine, and Cancel goes back to the three answers', async () => {
    const { s, bar } = await refusedEditor();
    fireEvent.click(within(bar).getByRole('button', { name: 'Keep mine…' }));
    expect(within(bar).getByText(/Overwrite the newer version with yours\?/)).toBeTruthy();
    expect(s.puts).toHaveLength(1);

    fireEvent.click(within(bar).getByRole('button', { name: 'Cancel' }));
    expect(within(bar).getByRole('button', { name: 'Reload theirs' })).toBeTruthy();
    expect(s.puts).toHaveLength(1);

    fireEvent.click(within(bar).getByRole('button', { name: 'Keep mine…' }));
    fireEvent.click(within(bar).getByRole('button', { name: 'Overwrite with mine' }));
    await waitFor(() => { expect(status()).toBe('Saved'); });
    expect(s.puts).toHaveLength(2);
    // It quoted the version the server has now, so the write is accepted.
    expect(s.puts[1]?.ifMatch).toBe('"dv1:2"');
    expect(screen.queryByTestId('stale-save')).toBeNull();
    expect(readLocalCopies(document.id)).toEqual([]);
  });

  it('reloads theirs, takes their title, and offers the set-aside copy instead of discarding it', async () => {
    const { s } = await refusedEditor();
    fireEvent.click(screen.getByRole('button', { name: 'Reload theirs' }));

    await waitFor(() => { expect(status()).toBe('Saved'); });
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Changed elsewhere');
    expect(screen.queryByTestId('stale-save')).toBeNull();
    const copy = screen.getByTestId('local-copy');
    expect(copy.textContent).toContain('kept in this browser');
    expect(readLocalCopies(document.id)).toHaveLength(1);
    expect(s.puts).toHaveLength(1);

    // Saving it as a new document puts it on the server as a document of its own and removes the notice.
    fireEvent.click(within(copy).getByRole('button', { name: 'Save it as a new document' }));
    await waitFor(() => { expect(screen.queryByTestId('local-copy')).toBeNull(); });
    expect(s.posts).toHaveLength(1);
    expect((s.posts[0] as { title: string }).title).toBe(`${document.title} (my copy)`);
    expect(screen.getByTestId('saved-copy')).toBeTruthy();
    expect(readLocalCopies(document.id)).toEqual([]);
  });

  it('saves mine as a new document and shows theirs here, with a way to open the copy', async () => {
    const { s, bar } = await refusedEditor();
    fireEvent.click(within(bar).getByRole('button', { name: 'Save mine as a new document' }));

    await waitFor(() => { expect(screen.getByTestId('saved-copy')).toBeTruthy(); });
    expect(s.posts).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Changed elsewhere');
    expect(status()).toBe('Saved');
    expect(screen.queryByTestId('local-copy')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Open it' }));
    await waitFor(() => { expect(path()).toBe('/d/doc_copy'); });
  });
});

describe('a local copy from an earlier visit, in the editor', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    window.history.replaceState(null, '', `/d/${document.id}`);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('is offered on load, and discarding it removes it without a request', () => {
    const s = server();
    storeLocalCopy({ copyId: 'earlier', documentId: document.id, document: { ...document, title: 'Unsaved last time' }, keptAt: '2026-09-29T10:00:00.000Z' });
    renderEditor();
    const copy = screen.getByTestId('local-copy');
    expect(copy.textContent).toContain('kept in this browser');

    fireEvent.click(within(copy).getByRole('button', { name: 'Discard copy' }));
    expect(screen.queryByTestId('local-copy')).toBeNull();
    expect(readLocalCopies(document.id)).toEqual([]);
    expect(s.posts).toHaveLength(0);
  });

  it('is not shown when there is none, or when it is what was loaded', () => {
    server();
    storeLocalCopy({ copyId: 'same', documentId: document.id, document, keptAt: '2026-09-29T10:00:00.000Z' });
    renderEditor();
    expect(screen.queryByTestId('local-copy')).toBeNull();
    expect(readLocalCopies(document.id)).toEqual([]);
  });
});
