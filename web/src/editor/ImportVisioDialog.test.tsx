// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ImportVisioDialog } from './ImportVisioDialog.tsx';

const meta = { createdAt: '2026-10-02T00:00:00.000Z', updatedAt: '2026-10-02T00:00:00.000Z', createdBy: 'visio-import' };

const flowDocument = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Order flow',
  type: 'flowchart',
  nodes: [
    { id: 'n_1', kind: 'terminal', label: 'Start', shape: 'pill', geometry: { x: 125, y: 62.5, w: 150, h: 75 } },
    { id: 'n_2', kind: 'process', label: 'Ship', shape: 'rounded', geometry: { x: 125, y: 262.5, w: 150, h: 75 } },
  ],
  edges: [{ id: 'e_3', kind: 'flow', label: 'go', from: { node: 'n_1' }, to: { node: 'n_2' } }],
  meta,
};

interface Request {
  url: string;
  method: string | undefined;
  contentType: string | undefined;
  body: unknown;
}

const vsdx = (name = 'orders.vsdx', size = 4096): File => new File([new Uint8Array(size)], name, { type: 'application/vnd.ms-visio.drawing' });

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('ImportVisioDialog', () => {
  const onClose = vi.fn();
  const onCreated = vi.fn();
  let requests: Request[];
  let imported: { document: unknown; warnings: unknown[]; pages?: unknown[] };

  beforeEach(() => {
    onClose.mockClear();
    onCreated.mockClear();
    requests = [];
    imported = {
      document: flowDocument,
      warnings: [{ code: 'kind-guessed', message: '"Start" is drawn as an ellipse, so it was given the kind \'terminal\'; change it if that is not what it is.', elementId: 'n_1' }],
      pages: [{ index: 1, name: 'Order flow', selected: true }],
    };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((url, init) => {
      const headers = init?.headers as Record<string, string> | undefined;
      requests.push({ url: urlOf(url), method: init?.method, contentType: headers?.['Content-Type'], body: init?.body });
      if (urlOf(url).startsWith('/api/import/visio')) {
        return Promise.resolve(json(200, imported));
      }
      return Promise.resolve(json(201, { id: 'doc_created' }));
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const isDisabled = (name: string): boolean => screen.getByRole('button', { name }).hasAttribute('disabled');
  const choose = (file: File = vsdx()): void => {
    fireEvent.change(screen.getByLabelText('Visio file'), { target: { files: [file] } });
  };
  const read = async (): Promise<void> => {
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(screen.getByRole('status')).toBeTruthy();
    });
  };

  it('is a labelled modal dialog with the file chooser focused and nothing to read yet', () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);

    expect(screen.getByRole('dialog', { name: 'Import Visio' }).getAttribute('aria-modal')).toBe('true');
    expect(globalThis.document.activeElement).toBe(screen.getByLabelText('Visio file'));
    expect(isDisabled('Read diagram')).toBe(true);
    expect(isDisabled('Create diagram')).toBe(true);
    // There is no paste box: a .vsdx is binary.
    expect(screen.queryByRole('textbox', { name: /XML/ })).toBeNull();
  });

  it('says what it reads, including the Lucidchart route and the older .vsd', () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);

    const help = screen.getByRole('dialog').textContent;
    expect(help).toContain('.vsdx');
    expect(help).toContain('Lucidchart');
    expect(help).toContain('older .vsd must be saved as .vsdx');
  });

  it('sends the file itself as the body of the import, lists the guesses, saves nothing until Create, and then keeps the positions', async () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    const file = vsdx('orders.vsdx', 12_345);
    choose(file);

    await read();

    expect(screen.getByText('Loaded orders.vsdx (12 KB).')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('Found 2 shapes and 1 connector.');
    expect(screen.getByRole('status').textContent).toContain('1 shape could mean more than one thing, so its kind was chosen for you and listed below:');
    expect(within(screen.getByRole('list', { name: 'Import warnings' })).getAllByRole('listitem')).toHaveLength(1);
    // The file carries every position: reading imports and nothing else (no /api/layout), and what is posted is the chosen File, as binary.
    expect(requests.map((request) => request.url)).toEqual(['/api/import/visio?type=flowchart']);
    expect(requests[0]?.method).toBe('POST');
    expect(requests[0]?.contentType).toBe('application/octet-stream');
    expect(requests[0]?.body).toBe(file);
    expect(onCreated).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    const create = requests.at(-1);
    expect(create?.url).toBe('/api/documents');
    const created = JSON.parse(create?.body as string) as { type: string; title: string; nodes: { geometry?: { x: number } }[] };
    expect(created.type).toBe('flowchart');
    expect(created.title).toBe('Order flow');
    expect(created.nodes[0]?.geometry).toEqual({ x: 125, y: 62.5, w: 150, h: 75 });
  });

  it('sends the type and the title, and discards a read when the type or the file changes so Create is off again', async () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();
    fireEvent.change(screen.getByRole('combobox', { name: 'Diagram type' }), { target: { value: 'architecture' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });
    await read();
    expect(requests[0]?.url).toBe('/api/import/visio?type=architecture&title=Shop');
    expect(isDisabled('Create diagram')).toBe(false);

    fireEvent.change(screen.getByRole('combobox', { name: 'Diagram type' }), { target: { value: 'freeform' } });
    expect(isDisabled('Create diagram')).toBe(true);
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByText('No kinds are guessed: every shape stays a plain shape.')).toBeTruthy();

    await read();
    choose(vsdx('other.vsdx'));
    expect(isDisabled('Create diagram')).toBe(true);
    expect(screen.getByText('Loaded other.vsdx (4 KB).')).toBeTruthy();
  });

  it('keeps the read when only the title changes, and creates under the new title', async () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();
    await read();

    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });

    expect(screen.getByRole('status')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect((JSON.parse(requests.at(-1)?.body as string) as { title: string }).title).toBe('Shop');
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
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();
    await read();

    const picker = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Page to import' });
    expect(Array.from(picker.options).map((option) => option.textContent)).toEqual(['Overview', 'Details', 'Page 3']);
    expect(picker.value).toBe('1');
    // The page note is listed but is not "skipped": the picker already says it.
    expect(screen.getByRole('status').textContent).not.toContain('skipped');
    expect(within(screen.getByRole('list', { name: 'Import warnings' })).getByText(/other 2 pages were not/)).toBeTruthy();

    imported = { ...imported, pages: [{ index: 1, name: 'Overview', selected: false }, { index: 2, name: 'Details', selected: true }, { index: 3, name: '', selected: false }] };
    fireEvent.change(picker, { target: { value: '2' } });

    await waitFor(() => {
      expect(requests.at(-1)?.url).toBe('/api/import/visio?type=flowchart&page=2');
    });
    await waitFor(() => {
      expect(screen.getByRole<HTMLSelectElement>('combobox', { name: 'Page to import' }).value).toBe('2');
    });
  });

  it('shows no page picker for a one-page file', async () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();

    await read();

    expect(screen.queryByRole('combobox', { name: 'Page to import' })).toBeNull();
  });

  it('counts what it could not draw as skipped, apart from the guesses', async () => {
    imported = {
      document: flowDocument,
      warnings: [
        { code: 'kind-guessed', message: 'a' },
        { code: 'unsupported-syntax', message: '2 shapes are instances of the Visio master \'Server\' (a stencil shape), which has no equivalent here.' },
        { code: 'missing-reference', message: 'A connector has a loose end.' },
      ],
      pages: [],
    };
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();

    await read();

    expect(screen.getByRole('status').textContent).toContain('1 shape could mean more than one thing, so its kind was chosen for you and listed below; 2 items were skipped or only partly understood:');
  });

  it('shows the server message when the importer refuses the file, and keeps Create off', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(422, { detail: 'That is an older Visio binary file (.vsd), or a password-protected Visio file, not a .vsdx drawing, and Draughtsman cannot read it. Open it in Visio, remove any password, and choose File, Save As, Visio Drawing (.vsdx), then import that file.' }));
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose(vsdx('old.vsd'));

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('Save As');
    });
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('refuses a file over the size limit without uploading it, saying what to do', async () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose(vsdx('huge.vsdx', 3_500_000));

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toContain('3.5 MB');
    });
    expect(screen.getByRole('alert').textContent).toContain('Remove embedded pictures');
    expect(requests).toHaveLength(0);
  });

  it('will not create a diagram from a page that held nothing', async () => {
    imported = { document: { ...flowDocument, nodes: [], edges: [] }, warnings: [{ code: 'empty-diagram', message: 'The page has no shapes.' }], pages: [] };
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();

    await read();

    expect(screen.getByRole('status').textContent).toContain('Nothing was found on that page.');
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('closes on Escape and Cancel', () => {
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    fireEvent.keyDown(screen.getByLabelText('Visio file'), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('asks confirmLeave before it creates anything, and a refusal leaves it ready with nothing saved', async () => {
    const held: { go: (() => void) | undefined } = { go: undefined };
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} confirmLeave={(go) => { held.go = go; }} />);
    choose();
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
    render(<ImportVisioDialog onClose={onClose} onCreated={onCreated} />);
    choose();
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
