// @vitest-environment jsdom
/**
 * Leaving the editor with unsaved edits (design/atlas/editor-safety.md): the in-app routes out (the palette's Back to
 * documents and New diagram, and the top bar's `navigate`), the browser's back button, and closing the tab.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { navigate } from '../app/router.ts';
import { parseDocument } from '../model/parse.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const document = parseDocument(orderService);

function renderEditor() {
  const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />);
  const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
  const shape = view.container.querySelector(`g[data-id="${document.nodes[0]?.id ?? ''}"] .shape`);
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

const status = () => screen.getByText(/^(Saved|Saving…|Unsaved changes|Not saved)$/).textContent;
const dialog = () => screen.queryByRole('dialog', { name: 'Unsaved changes' });
const openQuestion = (): HTMLElement => screen.getByRole('dialog', { name: 'Unsaved changes' });
const path = () => window.location.pathname;

const importedSql = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Imported schema',
  type: 'architecture',
  nodes: [{ id: 't_A', kind: 'entity', label: 'A', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null'] } }],
  edges: [],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'sql-import' },
};
const importedMermaid = { ...importedSql, title: 'Imported diagram', type: 'flowchart', nodes: [{ id: 'n_A', kind: 'process', label: 'A', shape: 'rounded' }] };
const importedDrawio = { ...importedMermaid, nodes: [{ id: 'n_A', kind: 'process', label: 'A', shape: 'rounded', geometry: { x: 0, y: 0, w: 120, h: 60 } }] };

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

/** A server that reads both imports, lays out, stores a new document as `doc_created` and accepts the editor's PUT. */
function importServer() {
  const fetchMock = vi.fn<typeof fetch>((input, init) => {
    const url = urlOf(input);
    const reply = (status: number, body: unknown, headers: Record<string, string> = {}) =>
      Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } }));
    if (url.startsWith('/api/import/sql')) {
      return reply(200, { document: importedSql, warnings: [] });
    }
    if (url.startsWith('/api/import/mermaid')) {
      return reply(200, { document: importedMermaid, warnings: [] });
    }
    if (url.startsWith('/api/import/drawio') || url.startsWith('/api/import/visio')) {
      return reply(200, { document: importedDrawio, warnings: [], pages: [] });
    }
    if (url.startsWith('/api/layout')) {
      return reply(200, { document: importedSql, warnings: [] });
    }
    if (init?.method === 'PUT') {
      return reply(200, {}, { ETag: '"dv1:next"' });
    }
    return reply(201, { id: 'doc_created' }, { ETag: '"dv1:new"' });
  });
  vi.stubGlobal('fetch', fetchMock);
  const posts = () => fetchMock.mock.calls.filter(([input, init]) => init?.method === 'POST' && urlOf(input) === '/api/documents');
  return { fetchMock, posts };
}
describe('leaving the editor with unsaved edits', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    // jsdom lays nothing out; give the canvas a real box so the SVG renders and presses land on nodes.
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    window.history.replaceState(null, '', `/d/${document.id}`);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('leaves straight away when nothing is unsaved', () => {
    renderEditor();
    expect(status()).toBe('Saved');
    act(() => { navigate('/'); });
    expect(dialog()).toBeNull();
    expect(path()).toBe('/');
  });

  it('holds an in-app navigation and asks; Cancel stays put with the edit intact', () => {
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    expect(status()).toBe('Unsaved changes');
    act(() => { navigate('/'); });
    expect(dialog()).not.toBeNull();
    expect(path()).toBe(`/d/${document.id}`);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(dialog()).toBeNull();
    expect(path()).toBe(`/d/${document.id}`);
    expect(status()).toBe('Unsaved changes');
  });

  it('also holds the File menu’s Back to documents, and Escape is Cancel', () => {
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    // The File menu's entries are the palette's actions; the viewer button is not in the tree, so drive the palette.
    fireEvent.keyDown(canvas, { key: 'k', metaKey: true });
    fireEvent.click(screen.getByRole('option', { name: 'Back to documents' }));
    expect(dialog()).not.toBeNull();
    expect(path()).toBe(`/d/${document.id}`);
    fireEvent.keyDown(screen.getByRole('dialog', { name: 'Unsaved changes' }), { key: 'Escape' });
    expect(dialog()).toBeNull();
    expect(path()).toBe(`/d/${document.id}`);
  });

  it('asks before File > New creates anything, so Cancel leaves no empty diagram on the server', async () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ id: 'doc_new' }), { status: 201, headers: { ETag: '"dv1:new"' } })));
    vi.stubGlobal('fetch', fetchMock);
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    fireEvent.keyDown(canvas, { key: 'k', metaKey: true });
    fireEvent.click(screen.getByRole('option', { name: 'New sequence diagram' }));
    expect(dialog()).not.toBeNull();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(0);
    expect(path()).toBe(`/d/${document.id}`);
    // Discarding is the go-ahead: the diagram is created and opened without a second question.
    fireEvent.keyDown(canvas, { key: 'k', metaKey: true });
    fireEvent.click(screen.getByRole('option', { name: 'New sequence diagram' }));
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    await waitFor(() => { expect(path()).toBe('/d/doc_new'); });
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
  });

  it('Discard changes leaves and drops the edit', () => {
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    act(() => { navigate('/'); });
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(path()).toBe('/');
  });

  it('Save and leave saves through the ETag-guarded PUT and leaves only once it succeeded', async () => {
    let finish: (response: Response) => void = () => undefined;
    const fetchMock = vi.fn<typeof fetch>(() => new Promise<Response>((resolve) => { finish = resolve; }));
    vi.stubGlobal('fetch', fetchMock);
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    act(() => { navigate('/'); });
    fireEvent.click(screen.getByRole('button', { name: 'Save and leave' }));
    const puts = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT');
    await waitFor(() => { expect(puts()).toHaveLength(1); });
    const [, init] = puts()[0] ?? [];
    expect(init?.method).toBe('PUT');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"dv1:loaded"');
    // Still on the page, and the question is waiting, while the request is in flight.
    expect(path()).toBe(`/d/${document.id}`);
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Saving…' }).disabled).toBe(true);
    await act(async () => { finish(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })); await Promise.resolve(); });
    await waitFor(() => { expect(path()).toBe('/'); });
  });

  it('stays, with the reason, when the save is refused because the document moved on', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((_input, init) => Promise.resolve(init?.method === 'PUT' ? new Response('{"title":"Document has changed"}', { status: 412 }) : new Response('{}', { status: 200 }))));
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    act(() => { navigate('/'); });
    fireEvent.click(screen.getByRole('button', { name: 'Save and leave' }));
    await waitFor(() => { expect(within(screen.getByRole('dialog', { name: 'Unsaved changes' })).getByRole('alert').textContent).toMatch(/changed somewhere else/); });
    expect(path()).toBe(`/d/${document.id}`);
    expect(dialog()).not.toBeNull();
    // Cancel and Discard are still there; nothing was lost or saved.
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Discard changes' }).disabled).toBe(false);
  });

  it('puts the browser’s back button through the same question and restores the page while it is open', () => {
    window.history.replaceState(null, '', '/');
    window.history.pushState(null, '', `/d/${document.id}`);
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    // What the browser does on Back: the URL is already the previous entry when popstate fires.
    act(() => {
      window.history.replaceState(null, '', '/');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(dialog()).not.toBeNull();
    expect(path()).toBe(`/d/${document.id}`);
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(path()).toBe('/');
  });

  it('asks the browser to confirm closing the tab while unsaved, and not otherwise', () => {
    const { canvas, shape } = renderEditor();
    const clean = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
    makeDirty(canvas, shape);
    const dirty = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(dirty);
    expect(dirty.defaultPrevented).toBe(true);
  });

  it('stops asking once the edit is undone back to the saved document', () => {
    const { canvas, shape } = renderEditor();
    makeDirty(canvas, shape);
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(status()).toBe('Saved');
    act(() => { navigate('/'); });
    expect(dialog()).toBeNull();
    expect(path()).toBe('/');
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
  });

  it('removes the guard when the editor goes away', () => {
    const { canvas, shape, unmount } = renderEditor();
    makeDirty(canvas, shape);
    unmount();
    act(() => { navigate('/'); });
    expect(path()).toBe('/');
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
  });

  describe('Import SQL, Import Mermaid, Import draw.io and Import Visio from a dirty editor ask before they create anything', () => {
    interface ImportItem { name: string; dialog: string; read: string; box?: string; text?: string; file?: string }
    const sqlImport: ImportItem = { name: 'Import SQL…', dialog: 'Import SQL', box: 'SQL', text: 'create table A (Id int primary key)', read: 'Read schema' };
    const imports: ImportItem[] = [
      sqlImport,
      { name: 'Import Mermaid…', dialog: 'Import Mermaid', box: 'Mermaid', text: 'flowchart TD\n  A', read: 'Read diagram' },
      { name: 'Import draw.io…', dialog: 'Import draw.io', box: 'draw.io XML', text: '<mxfile><diagram name="A"/></mxfile>', read: 'Read diagram' },
      // A .vsdx is binary, so it is chosen as a file, not pasted.
      { name: 'Import Visio…', dialog: 'Import Visio', file: 'Visio file', read: 'Read diagram' },
    ];

    async function readImport(canvas: HTMLElement, item: ImportItem): Promise<HTMLElement> {
      fireEvent.keyDown(canvas, { key: 'k', metaKey: true });
      fireEvent.click(screen.getByRole('option', { name: item.name }));
      const importDialog = screen.getByRole('dialog', { name: item.dialog });
      if (item.file !== undefined) {
        fireEvent.change(within(importDialog).getByLabelText(item.file), { target: { files: [new File([new Uint8Array(10)], 'a.vsdx')] } });
      } else {
        fireEvent.change(within(importDialog).getByRole('textbox', { name: item.box ?? '' }), { target: { value: item.text ?? '' } });
      }
      fireEvent.click(within(importDialog).getByRole('button', { name: item.read }));
      await waitFor(() => { expect(within(importDialog).getByRole<HTMLButtonElement>('button', { name: 'Create diagram' }).disabled).toBe(false); });
      return importDialog;
    }

    for (const item of imports) {
      it(`${item.name} Cancel leaves no document behind and the dialog usable`, async () => {
        const { posts } = importServer();
        const { canvas, shape } = renderEditor();
        makeDirty(canvas, shape);
        const importDialog = await readImport(canvas, item);
        fireEvent.click(within(importDialog).getByRole('button', { name: 'Create diagram' }));

        // Asked first: nothing has been created, and the question sits over the import dialog.
        expect(dialog()).not.toBeNull();
        expect(posts()).toHaveLength(0);
        expect(path()).toBe(`/d/${document.id}`);

        fireEvent.click(within(openQuestion()).getByRole('button', { name: 'Cancel' }));
        expect(dialog()).toBeNull();
        expect(posts()).toHaveLength(0);
        expect(path()).toBe(`/d/${document.id}`);
        expect(status()).toBe('Unsaved changes');
        // Not stuck on "Creating...": ready to create again, and Cancel and Escape close it.
        expect(within(importDialog).getByRole<HTMLButtonElement>('button', { name: 'Create diagram' }).disabled).toBe(false);
        expect(within(importDialog).getByRole<HTMLButtonElement>('button', { name: 'Cancel' }).disabled).toBe(false);
        fireEvent.keyDown(importDialog, { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: item.dialog })).toBeNull();
      });

      it(`${item.name} Discard changes creates the document once and opens it without a second question`, async () => {
        const { posts } = importServer();
        const { canvas, shape } = renderEditor();
        makeDirty(canvas, shape);
        const importDialog = await readImport(canvas, item);
        fireEvent.click(within(importDialog).getByRole('button', { name: 'Create diagram' }));
        fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
        await waitFor(() => { expect(path()).toBe('/d/doc_created'); });
        expect(posts()).toHaveLength(1);
      });

      it(`${item.name} Save and leave saves the edit, then creates the document and opens it`, async () => {
        const { fetchMock, posts } = importServer();
        const { canvas, shape } = renderEditor();
        makeDirty(canvas, shape);
        const importDialog = await readImport(canvas, item);
        fireEvent.click(within(importDialog).getByRole('button', { name: 'Create diagram' }));
        expect(posts()).toHaveLength(0);
        fireEvent.click(screen.getByRole('button', { name: 'Save and leave' }));
        await waitFor(() => { expect(path()).toBe('/d/doc_created'); });
        const writes = fetchMock.mock.calls.filter(([input, init]) => init?.method !== undefined && init.method !== 'GET' && urlOf(input).startsWith('/api/documents'));
        expect(writes.map(([, init]) => init?.method)).toEqual(['PUT', 'POST']);
      });
    }

    it('creates at once, with no question, when nothing is unsaved', async () => {
      const { posts } = importServer();
      const { canvas } = renderEditor();
      const importDialog = await readImport(canvas, sqlImport);
      fireEvent.click(within(importDialog).getByRole('button', { name: 'Create diagram' }));
      await waitFor(() => { expect(path()).toBe('/d/doc_created'); });
      expect(dialog()).toBeNull();
      expect(posts()).toHaveLength(1);
    });
  });
});
