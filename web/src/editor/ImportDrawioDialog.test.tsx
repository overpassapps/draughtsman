// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ImportDrawioDialog } from './ImportDrawioDialog.tsx';

const meta = { createdAt: '2026-10-02T00:00:00.000Z', updatedAt: '2026-10-02T00:00:00.000Z', createdBy: 'drawio-import' };

const flowDocument = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Order flow',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'terminal', label: 'Start', shape: 'pill', geometry: { x: 10, y: 20, w: 120, h: 60 } },
    { id: 'n_b', kind: 'process', label: 'Ship', shape: 'rounded', geometry: { x: 10, y: 140, w: 120, h: 60 } },
  ],
  edges: [{ id: 'e_1', kind: 'flow', label: 'go', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta,
};

const DRAWIO = '<mxfile><diagram name="Order flow"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram></mxfile>';

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('ImportDrawioDialog', () => {
  const onClose = vi.fn();
  const onCreated = vi.fn();
  let requests: { url: string; body: string }[];
  let imported: { document: unknown; warnings: unknown[]; pages?: unknown[] };

  beforeEach(() => {
    onClose.mockClear();
    onCreated.mockClear();
    requests = [];
    imported = {
      document: flowDocument,
      warnings: [{ code: 'kind-guessed', message: '"Start" is drawn as an ellipse, so it was given the kind \'terminal\'; change it if that is not what it is.', elementId: 'n_a' }],
      pages: [{ index: 1, name: 'Order flow', selected: true }],
    };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((url, init) => {
      requests.push({ url: urlOf(url), body: init?.body as string });
      const path = urlOf(url);
      if (path.startsWith('/api/import/drawio')) {
        return Promise.resolve(json(200, imported));
      }
      return Promise.resolve(json(201, { id: 'doc_created' }));
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const isDisabled = (name: string): boolean => screen.getByRole('button', { name }).hasAttribute('disabled');
  const paste = (text: string): void => {
    fireEvent.change(screen.getByRole('textbox', { name: 'draw.io XML' }), { target: { value: text } });
  };
  const read = async (): Promise<void> => {
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(screen.getByRole('status')).toBeTruthy();
    });
  };

  it('is a labelled modal dialog with the paste box focused and nothing to read yet', () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);

    expect(screen.getByRole('dialog', { name: 'Import draw.io' }).getAttribute('aria-modal')).toBe('true');
    expect(globalThis.document.activeElement).toBe(screen.getByRole('textbox', { name: 'draw.io XML' }));
    expect(isDisabled('Read diagram')).toBe(true);
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('reads the pasted XML without saving or laying anything out, lists the guesses, then creates the diagram with its positions', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);

    await read();

    expect(screen.getByRole('status').textContent).toContain('Found 2 shapes and 1 connector.');
    expect(screen.getByRole('status').textContent).toContain('1 shape could mean more than one thing, so its kind was chosen for you and listed below:');
    const warnings = within(screen.getByRole('list', { name: 'Import warnings' })).getAllByRole('listitem');
    expect(warnings).toHaveLength(1);
    expect(warnings[0]?.textContent).toContain('drawn as an ellipse');
    // The file carries every position: reading imports and nothing else (no /api/layout), and it posts the XML as it was pasted.
    expect(requests.map((request) => request.url)).toEqual(['/api/import/drawio?type=flowchart']);
    expect(requests[0]?.body).toBe(DRAWIO);
    expect(onCreated).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    const create = requests.at(-1);
    expect(create?.url).toBe('/api/documents');
    const created = JSON.parse(create?.body ?? '{}') as { type: string; title: string; nodes: { geometry?: { x: number } }[] };
    expect(created.type).toBe('flowchart');
    expect(created.title).toBe('Order flow');
    expect(created.nodes[0]?.geometry).toEqual({ x: 10, y: 20, w: 120, h: 60 });
  });

  it('reads a chosen file as the source and says which file it loaded', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);

    fireEvent.change(screen.getByLabelText('draw.io file'), { target: { files: [new File([DRAWIO], 'orders.drawio', { type: 'application/xml' })] } });

    await waitFor(() => {
      expect(screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'draw.io XML' }).value).toBe(DRAWIO);
    });
    expect(screen.getByText('Loaded orders.drawio.')).toBeTruthy();
    expect(isDisabled('Read diagram')).toBe(false);
    await read();
    expect(requests[0]?.body).toBe(DRAWIO);

    // Editing the text afterwards is no longer "that file".
    paste(`${DRAWIO} `);
    expect(screen.queryByText('Loaded orders.drawio.')).toBeNull();
  });

  it('sends the type and the title, and discards a read when the type or the text changes so Create is off again', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);
    fireEvent.change(screen.getByRole('combobox', { name: 'Diagram type' }), { target: { value: 'architecture' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });
    await read();
    expect(requests[0]?.url).toBe('/api/import/drawio?type=architecture&title=Shop');
    expect(isDisabled('Create diagram')).toBe(false);

    fireEvent.change(screen.getByRole('combobox', { name: 'Diagram type' }), { target: { value: 'freeform' } });
    expect(isDisabled('Create diagram')).toBe(true);
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByText('No kinds are guessed: every shape stays a plain shape.')).toBeTruthy();

    await read();
    paste(`${DRAWIO} `);
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('keeps the read when only the title changes, and creates under the new title', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);
    await read();

    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });

    expect(screen.getByRole('status')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect((JSON.parse(requests.at(-1)?.body ?? '{}') as { title: string }).title).toBe('Shop');
  });

  it('offers the pages of a multi-page file, says which was read, and reads another page when it is chosen', async () => {
    imported = {
      document: flowDocument,
      warnings: [{ code: 'page-selected', message: 'Page 1 ("Overview") of 3 was imported; the other 2 pages were not. Choose another page to import it instead.' }],
      pages: [
        { index: 1, name: 'Overview', selected: true },
        { index: 2, name: 'Details', selected: false },
        { index: 3, name: '', selected: false },
      ],
    };
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);
    await read();

    const picker = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Page to import' });
    expect(Array.from(picker.options).map((option) => option.textContent)).toEqual(['Overview', 'Details', 'Page 3']);
    expect(picker.value).toBe('1');
    expect(screen.getByRole('status').textContent).toContain('Found 2 shapes and 1 connector.');
    // The page note is listed but is not "skipped": the picker already says it.
    expect(screen.getByRole('status').textContent).not.toContain('skipped');
    expect(within(screen.getByRole('list', { name: 'Import warnings' })).getByText(/other 2 pages were not/)).toBeTruthy();

    imported = { ...imported, pages: [{ index: 1, name: 'Overview', selected: false }, { index: 2, name: 'Details', selected: true }, { index: 3, name: '', selected: false }] };
    fireEvent.change(picker, { target: { value: '2' } });

    await waitFor(() => {
      expect(requests.at(-1)?.url).toBe('/api/import/drawio?type=flowchart&page=2');
    });
    await waitFor(() => {
      expect(screen.getByRole<HTMLSelectElement>('combobox', { name: 'Page to import' }).value).toBe('2');
    });
  });

  it('shows no page picker for a one-page file', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);

    await read();

    expect(screen.queryByRole('combobox', { name: 'Page to import' })).toBeNull();
  });

  it('counts a shape it could not draw as skipped, apart from the guesses', async () => {
    imported = {
      document: flowDocument,
      warnings: [
        { code: 'kind-guessed', message: 'a' },
        { code: 'kind-guessed', message: 'b' },
        { code: 'unsupported-syntax', message: '"Lambda" uses the draw.io shape \'mxgraph.aws4.lambda_function\', which has no equivalent here; it is drawn as a plain box.' },
        { code: 'missing-reference', message: 'A connector has a loose end.' },
      ],
      pages: [],
    };
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);

    await read();

    expect(screen.getByRole('status').textContent).toContain('2 shapes could mean more than one thing, so their kinds were chosen for you and listed below; 2 items were skipped or only partly understood:');
  });

  it('shows the server message when the importer refuses the source, and keeps Create off', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(422, { detail: 'The draw.io file contains a DOCTYPE declaration. draw.io never writes one, and Draughtsman refuses it.' }));
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste('<!DOCTYPE x><mxfile/>');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('DOCTYPE');
    });
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('will not create a diagram from a page that held nothing', async () => {
    imported = { document: { ...flowDocument, nodes: [], edges: [] }, warnings: [{ code: 'empty-diagram', message: 'The page has no shapes.' }], pages: [] };
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);

    await read();

    expect(screen.getByRole('status').textContent).toContain('Nothing was found on that page.');
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('closes on Escape and Cancel', () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'draw.io XML' }), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('asks confirmLeave before it creates anything, and a refusal leaves it ready with nothing saved', async () => {
    const held: { go: (() => void) | undefined } = { go: undefined };
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} confirmLeave={(go) => { held.go = go; }} />);
    paste(DRAWIO);
    await read();
    const before = requests.length;

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    // Asked, not yet created; the dialog is not "Creating..." and Cancel still works whatever the answer.
    expect(held.go).toBeDefined();
    expect(requests).toHaveLength(before);
    expect(isDisabled('Create diagram')).toBe(false);
    expect(isDisabled('Cancel')).toBe(false);

    held.go?.();
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect(requests.at(-1)?.url).toBe('/api/documents');
  });

  it('is usable again after a creation whose navigation went nowhere', async () => {
    render(<ImportDrawioDialog onClose={onClose} onCreated={onCreated} />);
    paste(DRAWIO);
    await read();
    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
      expect(isDisabled('Cancel')).toBe(false);
    });
  });
});
