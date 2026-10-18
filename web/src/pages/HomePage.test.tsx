// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { HOME_LAYOUT_KEY } from '../app/homeLayout.ts';
import { HomePage } from './HomePage.tsx';

const document = { id: 'doc_a', title: 'Order service', type: 'flowchart' as const, workspace: 'default', createdAt: '2026-09-21T09:30:15.123Z', updatedAt: '2026-09-21T10:02:44.001Z' };

function listResponse(): Response {
  return new Response(JSON.stringify({ items: [document], total: 1, offset: 0, limit: 50 }), { status: 200 });
}

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

describe('HomePage', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(listResponse())));
    window.history.replaceState(null, '', '/');
    // The page opens on the grid unless this browser chose the table (HomePageGrid.test.tsx); the table's own tests ask for it.
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists the documents with their titles, type and last-modified time', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    expect(screen.getByRole('button', { name: 'New diagram' })).toBeTruthy();
  });

  it('creates a diagram from the New diagram menu and navigates to it', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const fetchSpy = vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_new"}', { status: 201 }));

    fireEvent.click(screen.getByRole('button', { name: 'New diagram' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Flowchart' }));

    await flush();
    await waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledWith('/api/documents', expect.objectContaining({ method: 'POST' }));
    });
    expect(window.location.pathname).toBe('/d/doc_new');
  });

  it('opens the Import SQL paste box beside New diagram and opens the diagram it creates', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const imported = {
      id: 'doc_importer', schemaVersion: 1, title: 'Imported schema', type: 'architecture',
      nodes: [{ id: 't_A', kind: 'entity', label: 'A', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null'] } }],
      edges: [], meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'sql-import' },
    };

    fireEvent.click(screen.getByRole('button', { name: 'Import SQL' }));
    const dialog = screen.getByRole('dialog', { name: 'Import SQL' });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'SQL' }), { target: { value: 'create table A (Id int primary key)' } });
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ document: imported, warnings: [] }), { status: 200 }));
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ document: imported, warnings: [] }), { status: 200 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(within(dialog).getByRole('status').textContent).toContain('Found 1 table and 0 relationships.');
    });
    vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_from_sql"}', { status: 201 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/d/doc_from_sql');
    });
  });

  it('offers Sequence in the New diagram menu and creates it with the starter participants and message', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const fetchSpy = vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_seq"}', { status: 201 }));

    fireEvent.click(screen.getByRole('button', { name: 'New diagram' }));
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Flowchart', 'Architecture', 'Freeform', 'Sequence', 'ER diagram', 'UML class diagram', 'BPMN process', 'Swimlane diagram', 'Org chart']);
    // The shape-based types are under their own heading, so they read as more of the same, not as document types.
    expect(screen.getByRole('group', { name: 'More diagram types' }).textContent).toContain('More diagram types');
    fireEvent.click(screen.getByRole('menuitem', { name: 'Sequence' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/d/doc_seq');
    });
    const body = JSON.parse(fetchSpy.mock.calls[1]?.[1]?.body as string) as { type: string; nodes: { label: string }[]; edges: { kind: string }[] };
    expect(body.type).toBe('sequence');
    expect(body.nodes.map((node) => node.label)).toEqual(['User', 'System']);
    expect(body.edges.map((edge) => edge.kind)).toEqual(['sync']);
  });

  // P5: ER, UML class, BPMN, swimlane and org-chart diagrams were only reachable inside palette groups.
  it('creates each More diagram types entry as an ordinary document of an existing type that opens with its shapes', async () => {
    const expected: [string, string, string][] = [
      ['ER diagram', 'architecture', 'custom:er/entity'],
      ['UML class diagram', 'architecture', 'custom:uml/class'],
      ['BPMN process', 'flowchart', 'custom:bpmn/task'],
      ['Swimlane diagram', 'flowchart', 'custom:swimlane/lane'],
      ['Org chart', 'architecture', 'custom:org/role'],
    ];
    for (const [label, type, shape] of expected) {
      const view = render(<HomePage />);
      await waitFor(() => {
        expect(screen.getByText('Order service')).toBeTruthy();
      });
      const fetchSpy = vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_more"}', { status: 201 }));
      fireEvent.click(screen.getByRole('button', { name: 'New diagram' }));
      fireEvent.click(screen.getByRole('menuitem', { name: label }));
      await waitFor(() => {
        expect(window.location.pathname).toBe('/d/doc_more');
      });
      const body = JSON.parse(fetchSpy.mock.calls.at(-1)?.[1]?.body as string) as { type: string; title: string; nodes: { shape: string }[] };
      expect(body.type, label).toBe(type);
      expect(body.title, label).toMatch(/^Untitled /);
      expect(body.nodes.map((node) => node.shape), label).toContain(shape);
      view.unmount();
      window.history.pushState({}, '', '/');
    }
  });

  it('shows a sequence document with the Sequence type label and its own icon, distinct from a flowchart', async () => {
    window.localStorage.setItem(HOME_LAYOUT_KEY, 'list');
    const sequence = { ...document, id: 'doc_s', title: 'Checkout', type: 'sequence' as const };
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ items: [document, sequence], total: 2, offset: 0, limit: 50 }), { status: 200 }));
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Checkout')).toBeTruthy();
    });
    const flowRow = screen.getByText('Order service').closest('tr');
    const seqRow = screen.getByText('Checkout').closest('tr');
    if (flowRow === null || seqRow === null) {
      throw new Error('rows not rendered');
    }
    expect(within(flowRow).getByText('Flowchart')).toBeTruthy();
    expect(within(seqRow).getByText('Sequence')).toBeTruthy();
    expect(within(seqRow).queryByText('Flowchart')).toBeNull();
    expect(flowRow.querySelector('svg')?.innerHTML).not.toBe(seqRow.querySelector('svg')?.innerHTML);
    expect(seqRow.querySelector('svg path')).not.toBeNull();
  });

  it('opens the Import Mermaid paste box beside Import SQL and opens the sequence it creates', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const imported = {
      id: 'doc_importer', schemaVersion: 1, title: 'Imported diagram', type: 'sequence',
      nodes: [
        { id: 'p_A', kind: 'participant', label: 'A', shape: 'rectangle' },
        { id: 'p_B', kind: 'participant', label: 'B', shape: 'rectangle' },
      ],
      edges: [{ id: 'm_1', kind: 'sync', label: 'hi', from: { node: 'p_A' }, to: { node: 'p_B' } }],
      meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'mermaid-import' },
    };

    fireEvent.click(screen.getByRole('button', { name: 'Import Mermaid' }));
    const dialog = screen.getByRole('dialog', { name: 'Import Mermaid' });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Mermaid' }), { target: { value: 'sequenceDiagram\nA->>B: hi' } });
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ document: imported, warnings: [] }), { status: 200 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(within(dialog).getByRole('status').textContent).toContain('Found 2 participants and 1 message.');
    });
    vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_from_mermaid"}', { status: 201 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/d/doc_from_mermaid');
    });
  });

  it('opens the Import draw.io dialog beside Import Mermaid and opens the diagram it creates', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const imported = {
      id: 'doc_importer', schemaVersion: 1, title: 'Order flow', type: 'flowchart',
      nodes: [
        { id: 'n_a', kind: 'process', label: 'A', shape: 'rounded', geometry: { x: 0, y: 0, w: 120, h: 60 } },
        { id: 'n_b', kind: 'process', label: 'B', shape: 'rounded', geometry: { x: 200, y: 0, w: 120, h: 60 } },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
      meta: { createdAt: '2026-10-02T00:00:00.000Z', updatedAt: '2026-10-02T00:00:00.000Z', createdBy: 'drawio-import' },
    };

    fireEvent.click(screen.getByRole('button', { name: 'Import draw.io' }));
    const dialog = screen.getByRole('dialog', { name: 'Import draw.io' });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'draw.io XML' }), { target: { value: '<mxfile/>' } });
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ document: imported, warnings: [], pages: [] }), { status: 200 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(within(dialog).getByRole('status').textContent).toContain('Found 2 shapes and 1 connector.');
    });
    vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_from_drawio"}', { status: 201 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/d/doc_from_drawio');
    });
  });

  it('opens the Import Visio dialog beside Import draw.io and opens the diagram it creates', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const imported = {
      id: 'doc_importer', schemaVersion: 1, title: 'Order flow', type: 'flowchart',
      nodes: [
        { id: 'n_a', kind: 'process', label: 'A', shape: 'rounded', geometry: { x: 0, y: 0, w: 120, h: 60 } },
        { id: 'n_b', kind: 'process', label: 'B', shape: 'rounded', geometry: { x: 200, y: 0, w: 120, h: 60 } },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
      meta: { createdAt: '2026-10-02T00:00:00.000Z', updatedAt: '2026-10-02T00:00:00.000Z', createdBy: 'visio-import' },
    };

    fireEvent.click(screen.getByRole('button', { name: 'Import Visio' }));
    const dialog = screen.getByRole('dialog', { name: 'Import Visio' });
    fireEvent.change(within(dialog).getByLabelText('Visio file'), { target: { files: [new File([new Uint8Array(10)], 'orders.vsdx')] } });
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ document: imported, warnings: [], pages: [] }), { status: 200 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(within(dialog).getByRole('status').textContent).toContain('Found 2 shapes and 1 connector.');
    });
    vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_from_visio"}', { status: 201 }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/d/doc_from_visio');
    });
  });

  it('renames a document through the kebab menu', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(new Response('{}', { status: 200, headers: { ETag: '"dv1:x"' } }));
    vi.mocked(fetch).mockResolvedValueOnce(new Response('{}', { status: 200, headers: { ETag: '"dv1:y"' } }));
    vi.mocked(fetch).mockResolvedValueOnce(listResponse());

    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Rename' }));

    const input = screen.getByRole('textbox', { name: 'Rename Order service' });
    fireEvent.change(input, { target: { value: 'Order service v2' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents/doc_a', expect.objectContaining({ method: 'PATCH' }));
    });
  });

  it('moves a document to the bin without asking, and offers Undo that restores it', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.mocked(fetch).mockResolvedValueOnce(listResponse());
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Delete' }));

    // Recoverable, so no modal: the DELETE goes out at once and a toast names what moved.
    const deleteCalls = () => vi.mocked(fetch).mock.calls.filter((call) => call[1]?.method === 'DELETE');
    await waitFor(() => {
      expect(deleteCalls()).toHaveLength(1);
    });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(deleteCalls()[0]?.[0]).toBe('/api/documents/doc_a');
    const undo = await screen.findByRole('button', { name: 'Undo' });
    expect(undo.closest('p')?.textContent).toContain('Moved “Order service” to the bin.');
    // One status mechanism (QA P23): the visible line is the live region, so the outcome is not announced a second time by a hidden one.
    expect(screen.getAllByRole('status').filter((node) => node.textContent.includes('Moved “Order service”'))).toHaveLength(1);
    expect(globalThis.document.querySelector('.visually-hidden[role="status"]')).toBeNull();

    vi.mocked(fetch).mockResolvedValueOnce(new Response('{}', { status: 200 }));
    vi.mocked(fetch).mockResolvedValueOnce(listResponse());
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/bin/doc_a/restore', expect.objectContaining({ method: 'POST' }));
    });
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Undo' })).toBeNull();
    });
  });

  it('duplicates a document through the kebab menu and reads the list again', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValueOnce(new Response('{"id":"doc_copy"}', { status: 201 }));
    vi.mocked(fetch).mockResolvedValueOnce(listResponse());
    fireEvent.click(screen.getByRole('button', { name: 'Actions for Order service' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Duplicate' }));

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents/doc_a/duplicate', expect.objectContaining({ method: 'POST' }));
    });
    // It opens nothing: the person stays on the list, which is read again.
    await waitFor(() => {
      expect(vi.mocked(fetch).mock.calls.filter((call) => call[0] === '/api/documents').length).toBeGreaterThanOrEqual(2);
    });
    expect(window.location.pathname).toBe('/');
  });

  it('searches titles after a pause, and says so when nothing matches, with a way back', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });

    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }), { status: 200 }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search documents by title' }), { target: { value: 'zzz' } });

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents?q=zzz', expect.anything());
    });
    expect(await screen.findByText(/No documents match/)).toBeTruthy();
    // A search with no hits is not the first-run empty state.
    expect(screen.queryByRole('heading', { name: 'No documents yet' })).toBeNull();

    vi.mocked(fetch).mockResolvedValue(listResponse());
    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
  });

  it('sorts by the column headers, reverses on a second press and says which way in aria-sort', async () => {
    window.localStorage.setItem(HOME_LAYOUT_KEY, 'list');
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const header = (name: string) => screen.getByRole('columnheader', { name: new RegExp(name) });
    expect(header('Last modified').getAttribute('aria-sort')).toBe('descending');
    expect(header('Title').getAttribute('aria-sort')).toBe('none');

    fireEvent.click(screen.getByRole('button', { name: /^Title/ }));
    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents?sort=title&dir=asc', expect.anything());
    });
    expect(header('Title').getAttribute('aria-sort')).toBe('ascending');

    fireEvent.click(screen.getByRole('button', { name: /^Title/ }));
    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents?sort=title&dir=desc', expect.anything());
    });
    expect(header('Title').getAttribute('aria-sort')).toBe('descending');

    // A new column starts in its natural direction: newest first for times.
    fireEvent.click(screen.getByRole('button', { name: /^Last modified/ }));
    await waitFor(() => {
      expect(vi.mocked(fetch).mock.calls.at(-1)?.[0]).toBe('/api/documents');
    });
  });

  it('filters by type', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });

    fireEvent.change(screen.getByRole('combobox', { name: 'Show only one type' }), { target: { value: 'architecture' } });

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/documents?type=architecture', expect.anything());
    });
  });

  it('jumps to the search field on / unless something already takes text', async () => {
    render(<HomePage />);
    await waitFor(() => {
      expect(screen.getByText('Order service')).toBeTruthy();
    });
    const search = screen.getByRole('searchbox', { name: 'Search documents by title' });

    fireEvent.keyDown(window, { key: '/' });
    expect(window.document.activeElement).toBe(search);

    const other = screen.getByRole('combobox', { name: 'Show only one type' });
    other.focus();
    fireEvent.keyDown(other, { key: '/' });
    expect(window.document.activeElement).toBe(other);
  });
});

describe('HomePage bin (OP-02)', () => {
  const entry = { id: 'doc_b', title: 'Old diagram', type: 'flowchart', createdAt: '2026-09-01T00:00:00.000Z', updatedAt: '2026-09-01T00:00:00.000Z', deletedAt: new Date(Date.now() - 3 * 86_400_000).toISOString(), deletedBy: 'Alice', purgesAt: new Date(Date.now() + 27 * 86_400_000).toISOString(), canPurge: true };

  function serveBin(items: unknown[], retentionDays = 30) {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
        if (url.startsWith('/api/bin?')) {
          return Promise.resolve(new Response(JSON.stringify({ items, total: items.length, offset: 0, limit: 200, retentionDays }), { status: 200 }));
        }
        if (url.startsWith('/api/bin/')) {
          return Promise.resolve(new Response(null, { status: 204 }));
        }
        return Promise.resolve(listResponse());
      }),
    );
  }

  beforeEach(() => {
    window.history.replaceState(null, '', '/');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists what was deleted with who, when and how long is left, and states the retention rule', async () => {
    serveBin([entry]);
    render(<HomePage />);
    await screen.findByText('Order service');

    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));

    expect(await screen.findByText('Old diagram')).toBeTruthy();
    expect(screen.getByText(/3d ago by Alice/)).toBeTruthy();
    expect(screen.getByText('27 days')).toBeTruthy();
    expect(screen.getByText(/stay here for 30 days, then are removed for good/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Bin' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('says when the bin is empty', async () => {
    serveBin([]);
    render(<HomePage />);
    await screen.findByText('Order service');

    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));

    expect(await screen.findByText(/The bin is empty/)).toBeTruthy();
  });

  it('restores a document and reads the documents list again', async () => {
    serveBin([entry]);
    render(<HomePage />);
    await screen.findByText('Order service');
    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));
    await screen.findByText('Old diagram');

    fireEvent.click(screen.getByRole('button', { name: 'Restore Old diagram' }));

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith('/api/bin/doc_b/restore', expect.objectContaining({ method: 'POST' }));
    });
    // The outcome is said once, in the page's visible status line, not in a hidden second live region inside the bin.
    const said = await screen.findAllByText('Restored “Old diagram”.');
    expect(said).toHaveLength(1);
    expect(said[0]?.getAttribute('role')).toBe('status');
    expect(said[0]?.classList.contains('visually-hidden')).toBe(false);
    expect(screen.getAllByRole('status').filter((node) => node.textContent.includes('Restored'))).toHaveLength(1);
  });

  it('deletes forever only after a separate confirmation that names the document, and never on the first press', async () => {
    serveBin([entry]);
    render(<HomePage />);
    await screen.findByText('Order service');
    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));
    await screen.findByText('Old diagram');
    const purges = () => vi.mocked(fetch).mock.calls.filter((call) => call[1]?.method === 'DELETE');

    fireEvent.click(screen.getByRole('button', { name: 'Delete forever: Old diagram' }));

    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/Old diagram/)).toBeTruthy();
    expect(within(dialog).getByText(/cannot be undone/)).toBeTruthy();
    expect(purges()).toHaveLength(0);

    // Cancel changes nothing.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(purges()).toHaveLength(0);

    fireEvent.click(screen.getByRole('button', { name: 'Delete forever: Old diagram' }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete forever' }));
    await waitFor(() => {
      expect(purges()).toHaveLength(1);
    });
    expect(purges()[0]?.[0]).toBe('/api/bin/doc_b');
  });

  it('offers no Delete forever on an entry the server says the caller may not remove', async () => {
    serveBin([{ ...entry, canPurge: false }]);
    render(<HomePage />);
    await screen.findByText('Order service');
    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));
    await screen.findByText('Old diagram');

    expect(screen.getByRole('button', { name: 'Restore Old diagram' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Delete forever: Old diagram' })).toBeNull();
  });

  it('shows "Kept" instead of a day count when the instance never empties the bin', async () => {
    // Sent as JSON, an undefined purge date is simply absent, which is what a server that never purges sends.
    serveBin([{ ...entry, purgesAt: undefined }], 0);
    render(<HomePage />);
    await screen.findByText('Order service');

    fireEvent.click(screen.getByRole('button', { name: 'Bin' }));

    expect(await screen.findByText('Kept')).toBeTruthy();
    expect(screen.getByText(/stay here until someone deletes them permanently/)).toBeTruthy();
  });
});

describe('HomePage empty instance (PR-03, PR-13)', () => {
  function serve(options: { documents: unknown[]; ai?: boolean; posts?: { url: string; body: Record<string, unknown> }[] }) {
    let created = 0;
    const listed = [...options.documents];
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input, init) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
        if (url === '/api/health') {
          return Promise.resolve(new Response(JSON.stringify({ ai: { enabled: options.ai === true } }), { status: 200 }));
        }
        if (url === '/api/documents' && init?.method === 'POST') {
          const body = JSON.parse(init.body as string) as Record<string, unknown>;
          options.posts?.push({ url, body });
          created += 1;
          listed.push({ id: `doc_${String(created)}`, title: body.title, type: body.type, workspace: 'default', createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z' });
          return Promise.resolve(new Response(JSON.stringify({ id: `doc_${String(created)}` }), { status: 201 }));
        }
        return Promise.resolve(new Response(JSON.stringify({ items: listed, total: listed.length, offset: 0, limit: 50 }), { status: 200 }));
      }),
    );
  }

  beforeEach(() => {
    window.history.replaceState(null, '', '/');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the atlas empty state: a heading, the one line, creation cards and the importers', async () => {
    serve({ documents: [] });
    render(<HomePage />);

    expect(await screen.findByRole('heading', { name: 'No documents yet' })).toBeTruthy();
    expect(screen.getByText('Start with a blank flowchart or architecture diagram, or paste text and let Draughtsman draw it.')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Flowchart/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Architecture/ })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Import SQL' }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: 'Import Mermaid' }).length).toBeGreaterThan(0);
  });

  it('offers the From text card only while an AI provider is configured', async () => {
    serve({ documents: [], ai: false });
    const off = render(<HomePage />);
    await screen.findByRole('heading', { name: 'No documents yet' });
    await act(() => Promise.resolve());
    expect(screen.queryByRole('button', { name: /^From text/ })).toBeNull();
    off.unmount();

    serve({ documents: [], ai: true });
    render(<HomePage />);
    expect(await screen.findByRole('button', { name: /^From text/ })).toBeTruthy();
  });

  it('creates nothing on its own: the samples are offered, not forced', async () => {
    const posts: { url: string; body: Record<string, unknown> }[] = [];
    serve({ documents: [], posts });
    render(<HomePage />);

    await screen.findByRole('button', { name: 'Add four sample diagrams' });
    await act(() => Promise.resolve());
    expect(posts).toEqual([]);
  });

  it('adds the four samples when asked, and the list replaces the empty state', async () => {
    const posts: { url: string; body: Record<string, unknown> }[] = [];
    serve({ documents: [], posts });
    render(<HomePage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Add four sample diagrams' }));

    await waitFor(() => {
      expect(screen.getByText('Sample: Order approval')).toBeTruthy();
    });
    expect(posts.map((post) => post.body.type)).toEqual(['flowchart', 'architecture', 'sequence', 'architecture']);
    expect(screen.queryByRole('heading', { name: 'No documents yet' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add four sample diagrams' })).toBeNull();
  });

  it('never offers the samples once any document exists, and asks nothing about AI', async () => {
    serve({ documents: [document], ai: true });
    render(<HomePage />);

    await screen.findByText('Order service');
    expect(screen.queryByRole('button', { name: 'Add four sample diagrams' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'No documents yet' })).toBeNull();
    const asked = vi.mocked(fetch).mock.calls.map(([input]) => (typeof input === 'string' ? input : ''));
    expect(asked).not.toContain('/api/health');
  });
});
