// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Document } from '../model/types.ts';
import { TryPage } from './TryPage.tsx';

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

/** jsdom measures every element as 0x0, and the canvas culls its whole content to an empty viewport. */
function giveTheCanvasABox(): void {
  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
    x: 0,
    y: 0,
    width: 800,
    height: 600,
    top: 0,
    left: 0,
    right: 800,
    bottom: 600,
    toJSON: () => ({}),
  });
}

function drawnIds(container: HTMLElement): string[] {
  return [...container.querySelectorAll('g[data-id]')].map((g) => g.getAttribute('data-id') ?? '');
}

const meta = { createdAt: '2026-09-20T22:00:00.000Z', updatedAt: '2026-09-20T22:00:00.000Z', createdBy: 'dsl' };

function importedDocument(): Document {
  return {
    id: 'doc_order_flow',
    schemaVersion: 1,
    title: 'Order flow',
    type: 'architecture',
    nodes: [
      { id: 'n_web', kind: 'service', label: 'Web app', shape: 'rounded' },
      { id: 'n_api', kind: 'service', label: 'Orders API', shape: 'rounded' },
    ],
    edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_api' } }],
    meta,
  };
}

function laidOutDocument(): Document {
  const doc = importedDocument();
  return {
    ...doc,
    nodes: doc.nodes.map((n, i) => ({ ...n, geometry: { x: 0, y: i * 100, w: 160, h: 80 } })),
  };
}

function savedDocument(doc: Document, id: string): Document {
  return { ...doc, id };
}

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response>;
type FetchMock = ReturnType<typeof vi.fn<(url: string, init?: RequestInit) => Promise<Response>>>;

/** A write answers with the version it produced, which the page has to quote back as the next save's base version. */
function jsonResponse(status: number, body: unknown, etag?: string, otherHeaders: Record<string, string> = {}): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (name: string) => (name.toLowerCase() === 'etag' && etag !== undefined ? etag : (otherHeaders[name.toLowerCase()] ?? null)) },
    json: () => Promise.resolve(body),
  } as Response;
}

function handlersToFetch(handlers: Record<string, Handler>): (url: string, init?: RequestInit) => Promise<Response> {
  return (url: string, init?: RequestInit) => {
    for (const [prefix, handler] of Object.entries(handlers)) {
      if (url.startsWith(prefix)) {
        return Promise.resolve(handler(url, init));
      }
    }
    throw new Error(`Unexpected fetch: ${url}`);
  };
}

/** The document a request carried, as the page actually sent it. */
function readDocument(init?: RequestInit): Document {
  const body = init?.body;
  if (typeof body !== 'string') {
    throw new Error('Expected a request with a JSON body');
  }
  return JSON.parse(body) as Document;
}

/** What PUT /api/documents/{id} answers: the sent body, with meta the caller omitted filled from the stored row. */
function storedAfterReplace(init?: RequestInit): Document {
  const sent = readDocument(init) as Document & { meta: Partial<Document['meta']> };
  return { ...sent, meta: { ...meta, ...sent.meta } };
}

function sentMeta(init?: RequestInit): Record<string, unknown> | undefined {
  const body = init?.body;
  if (typeof body !== 'string') {
    return undefined;
  }
  return (JSON.parse(body) as { meta?: Record<string, unknown> }).meta;
}

function place(doc: Document): Document {
  return { ...doc, nodes: doc.nodes.map((n, i) => ({ ...n, geometry: { x: 0, y: i * 100, w: 160, h: 80 } })) };
}

/** The base version the nth /api/documents call was written from, as the server reads it. */
function ifMatchOf(fetchMock: FetchMock, index: number): string | undefined {
  const calls = fetchMock.mock.calls.filter(([url]) => url.startsWith('/api/documents'));
  const headers = calls[index]?.[1]?.headers as Record<string, string> | undefined;
  return headers?.['If-Match'];
}

function documentCalls(fetchMock: FetchMock): string[] {
  return fetchMock.mock.calls.filter(([url]) => url.startsWith('/api/documents')).map(([url, init]) => `${init?.method ?? ''} ${url}`);
}

function installFetch(handlers: Record<string, Handler>): FetchMock {
  const fn: FetchMock = vi.fn(handlersToFetch(handlers));
  vi.stubGlobal('fetch', fn);
  return fn;
}

describe('TryPage', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    window.history.pushState(null, '', '/try');
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('seeds the box with a runnable example for each source kind', () => {
    const { container } = render(<TryPage />);
    const textarea = () => container.querySelector<HTMLTextAreaElement>('.try-textarea');
    expect(textarea()?.value).toContain('Web app');
    expect(textarea()?.value).toContain('draughtsman 1');

    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    expect(textarea()?.value).toContain('flowchart LR');
  });

  it('runs DSL source through import, layout and save, then renders it and updates the address bar', async () => {
    installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': () => jsonResponse(200, { document: laidOutDocument(), warnings: [] }),
      '/api/documents': () => jsonResponse(201, savedDocument(laidOutDocument(), 'doc_9f1c2b3a4d5e')),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText('Saved as doc_9f1c2b3a4d5e')).toBeTruthy();
    });
    flushFrame();

    expect(screen.getByRole('img', { name: 'Diagram: Order flow' })).toBeTruthy();
    expect(window.location.pathname).toBe('/d/doc_9f1c2b3a4d5e');
  });

  it('runs Mermaid source through import, layout and save', async () => {
    installFetch({
      '/api/import/mermaid': () => jsonResponse(200, { document: importedDocument(), warnings: [], direction: 'LR' }),
      '/api/layout': () => jsonResponse(200, { document: laidOutDocument(), warnings: [] }),
      '/api/documents': () => jsonResponse(201, savedDocument(laidOutDocument(), 'doc_mermaid')),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText('Saved as doc_mermaid')).toBeTruthy();
    });
    flushFrame();
    expect(screen.getByRole('img', { name: 'Diagram: Order flow' })).toBeTruthy();
  });

  it('shows the parser message and position for invalid input, and does not blank an existing diagram', async () => {
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': () => jsonResponse(200, { document: laidOutDocument(), warnings: [] }),
      '/api/documents': () => jsonResponse(201, savedDocument(laidOutDocument(), 'doc_first')),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_first')).toBeTruthy();
    });
    flushFrame();
    expect(screen.getByRole('img', { name: 'Diagram: Order flow' })).toBeTruthy();

    fetchMock.mockImplementation(
      handlersToFetch({
        '/api/import/dsl': () =>
          jsonResponse(422, { title: 'The DSL could not be parsed.', detail: "expected the label in double quotes but found 'k'", line: 5, column: 16 }),
      }),
    );

    const textarea = screen.getByRole('textbox');
    fireEvent.change(textarea, { target: { value: 'draughtsman 1\ntype architecture\ntitle "X"\n\nnode n_a bogus kind here\n' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeTruthy();
    });
    expect(screen.getByRole('alert').textContent).toContain('line 5, column 16');
    expect(screen.getByRole('alert').textContent).toContain('double quotes');
    // The diagram from the earlier successful run is still on screen.
    expect(screen.getByRole('img', { name: 'Diagram: Order flow' })).toBeTruthy();
  });

  it('draws the nodes on a fallback grid when the sidecar is down, and saves nothing', async () => {
    giveTheCanvasABox();
    // handlersToFetch throws on any URL it was not given, so an attempted save would fail this test loudly.
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': () => jsonResponse(503, { title: 'The layout engine is not available.', detail: 'The sidecar is not running.' }),
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(container.querySelector('.try-banner-warning')).not.toBeNull();
    });
    flushFrame();

    const notice = container.querySelector('.try-banner-warning')?.textContent ?? '';
    expect(notice).toContain('the sidecar is not running');
    expect(notice).toContain('placeholder grid positions');
    expect(notice).toContain('not saved');

    // Import returns nodes with no geometry, and the canvas drops every node it cannot place, edges with them.
    expect(drawnIds(container)).toEqual(expect.arrayContaining(['n_web', 'n_api', 'e_1']));

    // Grid positions are scaffolding: nothing marks them provisional once stored, so they must not be stored.
    expect(documentCalls(fetchMock)).toEqual([]);
    expect(screen.queryByText(/^Saved as /)).toBeNull();
    expect(window.location.pathname).toBe('/try');
  });

  it('draws a placed node flat when layout fails, rather than clipping it away inside a container', async () => {
    giveTheCanvasABox();
    const withContainer: Document = {
      ...importedDocument(),
      nodes: [
        { id: 'c_s1', kind: 'container', label: 'Backend', shape: 'rectangle' },
        { id: 'n_web', kind: 'service', label: 'Web app', shape: 'rounded' },
        { id: 'n_api', kind: 'service', label: 'Orders API', shape: 'rounded', parent: 'c_s1' },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_api' } }],
    };
    installFetch({
      '/api/import/mermaid': () => jsonResponse(200, { document: withContainer, warnings: [] }),
      '/api/layout': () => jsonResponse(503, { title: 'The layout engine is not available.', detail: 'The sidecar is not running.' }),
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(container.querySelector('.try-banner-warning')).not.toBeNull();
    });
    flushFrame();

    // A grid cell is not a container's box: nesting n_api inside c_s1's cell clipped it, and its edge, out of the
    // picture, while the banner claimed the nodes were on screen.
    expect(drawnIds(container)).toEqual(expect.arrayContaining(['c_s1', 'n_web', 'n_api', 'e_1']));
    expect(container.querySelector('.try-banner-warning')?.textContent).toContain('grouping is not shown');
  });

  it('keeps authored positions and containment, and still saves, when layout fails but nothing needed placing', async () => {
    giveTheCanvasABox();
    const authored: Document = {
      ...importedDocument(),
      nodes: [
        { id: 'c_vpc', kind: 'container', label: 'VPC', shape: 'rectangle', geometry: { x: 0, y: 0, w: 600, h: 400 } },
        { id: 'n_api', kind: 'service', label: 'Orders API', shape: 'rounded', geometry: { x: 40, y: 60, w: 160, h: 80 }, parent: 'c_vpc' },
        { id: 'n_db', kind: 'database', label: 'Postgres', shape: 'cylinder', geometry: { x: 300, y: 60, w: 160, h: 80 }, parent: 'c_vpc' },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_api' }, to: { node: 'n_db' } }],
    };
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: authored, warnings: [] }),
      '/api/layout': () => jsonResponse(503, { title: 'The layout engine is not available.', detail: 'The sidecar is not running.' }),
      '/api/documents': (_url, init) => jsonResponse(201, readDocument(init)),
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText('Saved as doc_order_flow')).toBeTruthy();
    });
    flushFrame();

    // Layout places what the source left unplaced; here it left nothing unplaced, so nothing on screen is a guess.
    const notice = container.querySelector('.try-banner-warning')?.textContent ?? '';
    expect(notice).toContain('the sidecar is not running');
    expect(notice).toContain('positions written in the source stand unchanged');
    expect(notice).not.toContain('placeholder');
    expect(notice).not.toContain('not saved');

    const saved = readDocument(fetchMock.mock.calls.find(([url]) => url.startsWith('/api/documents'))?.[1]);
    expect(saved.nodes.map((n) => n.parent)).toEqual([undefined, 'c_vpc', 'c_vpc']);
    expect(saved.nodes.map((n) => n.geometry)).toEqual(authored.nodes.map((n) => n.geometry));
    expect(drawnIds(container)).toEqual(expect.arrayContaining(['c_vpc', 'n_api', 'n_db', 'e_1']));
  });

  it('places only the nodes the source left unplaced, keeping authored containment intact', async () => {
    giveTheCanvasABox();
    const mixed: Document = {
      ...importedDocument(),
      nodes: [
        { id: 'c_vpc', kind: 'container', label: 'VPC', shape: 'rectangle', geometry: { x: 0, y: 0, w: 600, h: 400 } },
        { id: 'n_api', kind: 'service', label: 'Orders API', shape: 'rounded', geometry: { x: 40, y: 60, w: 160, h: 80 }, parent: 'c_vpc' },
        { id: 'n_new', kind: 'service', label: 'Fresh', shape: 'rounded' },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_api' }, to: { node: 'n_new' } }],
    };
    installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: mixed, warnings: [] }),
      '/api/layout': () => jsonResponse(503, { title: 'The layout engine is not available.', detail: 'The sidecar is not running.' }),
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(container.querySelector('.try-banner-warning')).not.toBeNull();
    });
    flushFrame();

    expect(container.querySelector('.try-banner-warning')?.textContent).toContain('placeholder grid positions');
    // n_api keeps the position and the container the source gave it; only n_new is a guess.
    expect(drawnIds(container)).toEqual(expect.arrayContaining(['c_vpc', 'n_api', 'n_new', 'e_1']));
    expect(container.querySelector('g[data-id="c_vpc"] g[data-id="n_api"]')).not.toBeNull();
  });

  it('names a layout timeout as one rather than blaming a stopped sidecar', async () => {
    installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': () => jsonResponse(504, { title: 'The layout engine did not answer in time.', detail: 'The sidecar did not answer within 10s.' }),
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(container.querySelector('.try-banner-warning')).not.toBeNull();
    });
    const notice = container.querySelector('.try-banner-warning')?.textContent ?? '';
    expect(notice).toContain('did not finish in time');
    expect(notice).toContain('did not answer within 10s');
    expect(notice).not.toContain('the sidecar is not running');
  });

  it('updates the document it already saved when Mermaid is run again under a fresh import id', async () => {
    let imports = 0;
    const fetchMock = installFetch({
      '/api/import/mermaid': () => {
        imports += 1;
        return jsonResponse(200, { document: { ...importedDocument(), id: `doc_guid${String(imports)}` }, warnings: [] });
      },
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (_url, init) => jsonResponse(200, storedAfterReplace(init)),
      '/api/documents': (_url, init) => jsonResponse(201, readDocument(init)),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_guid1')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(documentCalls(fetchMock).length).toBe(2);
    });

    // The second import minted doc_guid2, but the run must replace the diagram this page already stored.
    expect(documentCalls(fetchMock)).toEqual(['POST /api/documents', 'PUT /api/documents/doc_guid1']);
    expect(screen.getByText('Saved as doc_guid1')).toBeTruthy();
    expect(window.location.pathname).toBe('/d/doc_guid1');
  });

  it('quotes back X-Document-Version, not the weakened ETag a gateway left on the create', async () => {
    let writes = 0;
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (_url, init) => {
        writes += 1;
        return jsonResponse(200, storedAfterReplace(init), `W/"dv1:gateway${String(writes)}"`, { 'x-document-version': `dv1:v${String(writes)}` });
      },
      '/api/documents': (_url, init) => {
        writes += 1;
        return jsonResponse(201, readDocument(init), `W/"dv1:gateway${String(writes)}"`, { 'x-document-version': `dv1:v${String(writes)}` });
      },
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_order_flow')).toBeTruthy();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(documentCalls(fetchMock).length).toBe(2);
    });

    expect(ifMatchOf(fetchMock, 1)).toBe('"dv1:v1"');
  });

  it('saves a re-run from the version of its own last write, and reports a refusal rather than retrying', async () => {
    let writes = 0;
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (_url, init) => {
        writes += 1;
        return jsonResponse(200, storedAfterReplace(init), `"dv1:v${String(writes)}"`);
      },
      '/api/documents': (_url, init) => {
        writes += 1;
        return jsonResponse(201, readDocument(init), `"dv1:v${String(writes)}"`);
      },
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_order_flow')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(documentCalls(fetchMock).length).toBe(2);
    });

    // The replace quotes the version the create handed back, so the server can tell this save is not stale.
    expect(ifMatchOf(fetchMock, 1)).toBe('"dv1:v1"');

    // Something else writes to the document; the server refuses the next save and the page says so.
    fetchMock.mockImplementation(
      handlersToFetch({
        '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
        '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
        '/api/documents/': () =>
          jsonResponse(412, {
            title: 'Document has changed',
            detail: 'This save was made from version "dv1:v2", but document \'doc_order_flow\' is now at "dv1:v9"; it was written to in between.',
          }),
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText(/Could not save the document: This save was made from version/)).toBeTruthy();
    });
    // The refusal is reported, not worked around: the page does not fall back to a write that would overwrite.
    expect(documentCalls(fetchMock).length).toBe(3);
  });

  it('takes a fresh id instead of replacing a document this page did not create', async () => {
    const fetchMock = installFetch({
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/documents': (_url, init) => {
        const sent = readDocument(init) as Partial<Document>;
        // The server assigns an id only when the body leaves it out; an id already in use is a 409.
        return sent.id === undefined
          ? jsonResponse(201, { ...importedDocument(), ...sent, id: 'doc_9f1c2b3a4d5e' })
          : jsonResponse(409, { title: 'Document already exists', detail: "A document with id 'doc_order_flow' already exists." });
      },
    });

    const { container } = render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText('Saved as doc_9f1c2b3a4d5e')).toBeTruthy();
    });

    // doc_order_flow is derived from the title alone, so it may be someone else's document: never PUT over it.
    expect(documentCalls(fetchMock).some((call) => call.startsWith('PUT'))).toBe(false);
    expect(documentCalls(fetchMock)).toEqual(['POST /api/documents', 'POST /api/documents']);
    const notice = container.querySelector('.try-id-taken')?.textContent ?? '';
    expect(notice).toContain('doc_order_flow');
    expect(notice).toContain('saved under a new id');
    expect(window.location.pathname).toBe('/d/doc_9f1c2b3a4d5e');
  });

  it('keeps the stored creation stamp when it replaces its own document', async () => {
    const fetchMock = installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (_url, init) => jsonResponse(200, storedAfterReplace(init)),
      '/api/documents': (_url, init) => jsonResponse(201, readDocument(init)),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_order_flow')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(documentCalls(fetchMock).length).toBe(2);
    });

    // PUT fills createdAt and createdBy from the stored document when they are left out; sending the importer's
    // freshly minted ones would rewrite the document's creation time on every repeat run.
    const put = fetchMock.mock.calls.find(([url, init]) => url.startsWith('/api/documents/') && init?.method === 'PUT');
    const body = sentMeta(put?.[1]);
    expect(body).toBeDefined();
    expect(body).not.toHaveProperty('createdAt');
    expect(body).not.toHaveProperty('createdBy');
    expect(body).toHaveProperty('updatedAt');
  });

  it('replaces the address-bar entry on a repeat run so Back still reaches the page in one press', async () => {
    installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: importedDocument(), warnings: [] }),
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (_url, init) => jsonResponse(200, storedAfterReplace(init)),
      '/api/documents': (_url, init) => jsonResponse(201, readDocument(init)),
    });
    const pushState = vi.spyOn(window.history, 'pushState');
    const replaceState = vi.spyOn(window.history, 'replaceState');

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_order_flow')).toBeTruthy();
    });
    expect(pushState).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(replaceState).toHaveBeenCalledTimes(1);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(replaceState).toHaveBeenCalledTimes(2);
    });
    expect(pushState).toHaveBeenCalledTimes(1);
    expect(window.location.pathname).toBe('/d/doc_order_flow');
  });

  it('shows the unsupported-diagram warning from a Mermaid 422 rather than a generic failure', async () => {
    // POST /api/import/mermaid answers 422 with the ImportResult envelope (docs/api.md), not problem details.
    installFetch({
      '/api/import/mermaid': () =>
        jsonResponse(422, {
          document: { ...importedDocument(), nodes: [], edges: [] },
          warnings: [{ code: 'unsupported-diagram', message: "The source declares 'sequenceDiagram'; this importer reads flowchart and graph only." }],
        }),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'sequenceDiagram\n  A->>B: hi\n' } });
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeTruthy();
    });
    const alert = screen.getByRole('alert').textContent;
    expect(alert).toContain('sequenceDiagram');
    expect(alert).toContain('flowchart and graph only');
    expect(alert).not.toContain('could not be read');
  });

  it('creates a new document when the one it saved earlier has since been deleted', async () => {
    const deleted = new Set<string>();
    let imports = 0;
    const fetchMock = installFetch({
      '/api/import/mermaid': () => {
        imports += 1;
        return jsonResponse(200, { document: { ...importedDocument(), id: `doc_guid${String(imports)}` }, warnings: [] });
      },
      '/api/layout': (_url, init) => jsonResponse(200, { document: place(readDocument(init)), warnings: [] }),
      '/api/documents/': (url, init) =>
        deleted.has(url.slice('/api/documents/'.length))
          ? jsonResponse(404, { title: 'Document not found', detail: `No document with id '${url.slice('/api/documents/'.length)}'.` })
          : jsonResponse(200, storedAfterReplace(init)),
      '/api/documents': (_url, init) => jsonResponse(201, readDocument(init)),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('radio', { name: 'Mermaid' }));
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_guid1')).toBeTruthy();
    });

    deleted.add('doc_guid1');
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(screen.getByText('Saved as doc_guid2')).toBeTruthy();
    });
    expect(screen.queryByText(/Could not save the document/)).toBeNull();

    // The stale target was cleared and replaced by what the recovery created, so the next run updates that one.
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    await waitFor(() => {
      expect(documentCalls(fetchMock).length).toBe(4);
    });
    expect(documentCalls(fetchMock)).toEqual([
      'POST /api/documents',
      'PUT /api/documents/doc_guid1',
      'POST /api/documents',
      'PUT /api/documents/doc_guid2',
    ]);
    expect(screen.getByText('Saved as doc_guid2')).toBeTruthy();
  });

  it('lists the warnings the import and layout steps returned', async () => {
    const { container } = ((): { container: HTMLElement } => {
      installFetch({
        '/api/import/dsl': () =>
          jsonResponse(200, { document: importedDocument(), warnings: [{ code: 'dsl-lint', message: "unknown node kind 'widget' for architecture documents", line: 7 }] }),
        '/api/layout': () => jsonResponse(200, { document: laidOutDocument(), warnings: ['edge e_9 references a missing node (n_ghost); left unrouted'] }),
        '/api/documents': () => jsonResponse(201, savedDocument(laidOutDocument(), 'doc_warned')),
      });
      return render(<TryPage />);
    })();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByText('Saved as doc_warned')).toBeTruthy();
    });
    const warnings = container.querySelector('.try-warnings')?.textContent ?? '';
    expect(warnings).toContain('2 warnings');
    expect(warnings).toContain("line 7: unknown node kind 'widget'");
    expect(warnings).toContain('edge e_9 references a missing node (n_ghost); left unrouted');
  });

  it('shows a banner when a 2xx response is not a document, rather than resetting silently', async () => {
    installFetch({
      '/api/import/dsl': () => jsonResponse(200, { document: { id: 'nope' } }),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('The run could not be completed');
    });
    expect(screen.getByRole('button', { name: 'Run' })).toBeTruthy();
  });

  it('says the API is unreachable when a network error occurs', async () => {
    installFetch({
      '/api/import/dsl': () => Promise.reject(new TypeError('Failed to fetch')),
    });

    render(<TryPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('Cannot reach the Draughtsman API');
    });
  });
});
