// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ImportSqlDialog } from './ImportSqlDialog.tsx';

const importedDocument = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Imported schema',
  type: 'architecture',
  nodes: [
    { id: 't_A', kind: 'entity', label: 'A', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null'] } },
    { id: 't_B', kind: 'entity', label: 'B', shape: 'custom:er/entity', geometry: { x: 300, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'FK AId int not null'] } },
  ],
  edges: [{ id: 'e_1', kind: 'association', from: { node: 't_B' }, to: { node: 't_A' }, label: 'AId' }],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'sql-import' },
};

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('ImportSqlDialog', () => {
  const onClose = vi.fn();
  const onCreated = vi.fn();
  let requests: { url: string; body: string }[];

  beforeEach(() => {
    onClose.mockClear();
    onCreated.mockClear();
    requests = [];
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((url, init) => {
      requests.push({ url: urlOf(url), body: init?.body as string });
      const path = urlOf(url);
      if (path.startsWith('/api/import/sql')) {
        return Promise.resolve(json(200, {
          document: importedDocument,
          warnings: [
            { code: 'unsupported-syntax', message: 'Skipped "SET ANSI_NULLS ON": only CREATE TABLE and ALTER TABLE ... ADD carry schema.', line: 2 },
            { code: 'missing-reference', message: 'C (X) references Y, which is not in the source; no relationship was drawn.', line: 9 },
          ],
        }));
      }
      if (path.startsWith('/api/layout')) {
        return Promise.resolve(json(200, { document: importedDocument, warnings: [] }));
      }
      return Promise.resolve(json(201, { id: 'doc_created' }));
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const isDisabled = (name: string): boolean => screen.getByRole('button', { name }).hasAttribute('disabled');

  const paste = (text: string): void => {
    fireEvent.change(screen.getByRole('textbox', { name: 'SQL' }), { target: { value: text } });
  };

  it('is a labelled modal dialog with the paste box focused and nothing to read yet', () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);

    expect(screen.getByRole('dialog', { name: 'Import SQL' }).getAttribute('aria-modal')).toBe('true');
    expect(globalThis.document.activeElement).toBe(screen.getByRole('textbox', { name: 'SQL' }));
    expect(isDisabled('Read schema')).toBe(true);
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('reads the schema without saving, shows the summary and every warning with its line, then creates it', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int primary key)');

    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Found 2 tables and 1 relationship.');
    });
    const warnings = within(screen.getByRole('list', { name: 'Import warnings' })).getAllByRole('listitem');
    expect(warnings).toHaveLength(2);
    expect(warnings[0]?.textContent).toContain('Line 2');
    expect(warnings[0]?.textContent).toContain('SET ANSI_NULLS ON');
    expect(warnings[1]?.textContent).toContain('Line 9');
    // Reading imports and lays out, and saves nothing.
    expect(requests.map((request) => request.url.split('?')[0])).toEqual(['/api/import/sql', '/api/layout']);
    expect(onCreated).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect(requests.at(-1)?.url).toBe('/api/documents');
  });

  it('sends the chosen dialect, type and title', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    fireEvent.change(screen.getByRole('combobox', { name: 'Dialect' }), { target: { value: 'postgres' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Diagram type' }), { target: { value: 'freeform' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });

    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(requests[0]?.url).toBe('/api/import/sql?type=freeform&dialect=postgres&title=Shop');
    });
  });

  it('colours by tag by default and sends tags=off when the box is unticked', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    const box = screen.getByRole('checkbox', { name: /Colour tables by their/ });
    expect((box as HTMLInputElement).checked).toBe(true);

    fireEvent.click(box);
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(requests[0]?.url).toBe('/api/import/sql?type=architecture&tags=off');
    });
  });

  it('draws comment relationships by default and sends implied=off when the box is unticked', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    const box = screen.getByRole('checkbox', { name: /relationships noted in comments/ });
    expect((box as HTMLInputElement).checked).toBe(true);

    fireEvent.click(box);
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(requests[0]?.url).toBe('/api/import/sql?type=architecture&implied=off');
    });
  });

  it('discards a read when the text changes, so only what the box says can be created', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(screen.getByRole('status')).toBeTruthy();
    });

    paste('create table A (Id int, Name text)');

    expect(screen.queryByRole('status')).toBeNull();
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('keeps the read when only the title changes, and creates under the new title', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(screen.getByRole('status')).toBeTruthy();
    });

    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });

    expect(screen.getByRole('status')).toBeTruthy();
    expect(isDisabled('Create diagram')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect((JSON.parse(requests.at(-1)?.body ?? '{}') as { title: string }).title).toBe('Shop');
  });

  it('says so when the text has no tables and offers no create', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(200, { document: { ...importedDocument, nodes: [], edges: [] }, warnings: [{ code: 'empty-diagram', message: 'No CREATE TABLE statement was found in the source.' }] }));
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('select 1');

    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('No tables were found in that SQL.');
    });
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('shows the server message when the request is refused and lets the user try again', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(413, { title: 'Source too large', detail: 'Source is limited to 1,000,000 characters.' }));
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('x');

    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe('Source is limited to 1,000,000 characters.');
    });
    expect(isDisabled('Read schema')).toBe(false);
  });

  it('keeps the read and shows the error when creating fails', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int)');
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(screen.getByRole('status')).toBeTruthy();
    });
    vi.mocked(fetch).mockResolvedValueOnce(json(400, { title: 'Document is not valid', detail: 'The document is not valid.' }));

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe('The document is not valid.');
    });
    expect(onCreated).not.toHaveBeenCalled();
    expect(isDisabled('Create diagram')).toBe(false);
  });

  it('closes on Escape, Cancel and a click on the backdrop, and keeps the keys away from the page behind', () => {
    const { container } = render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    const outside = vi.fn();
    globalThis.document.addEventListener('keydown', outside);

    fireEvent.keyDown(screen.getByRole('textbox', { name: 'SQL' }), { key: 'z', metaKey: true, bubbles: true });
    expect(outside).not.toHaveBeenCalled();
    globalThis.document.removeEventListener('keydown', outside);

    fireEvent.keyDown(screen.getByRole('textbox', { name: 'SQL' }), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    const backdrop = container.querySelector('.sql-import-backdrop');
    if (!backdrop) {
      throw new Error('backdrop missing');
    }
    fireEvent.mouseDown(backdrop);

    expect(onClose).toHaveBeenCalledTimes(3);
  });

  it('asks confirmLeave before it creates anything, and a refusal leaves it ready with nothing saved', async () => {
    const held: { go: (() => void) | undefined } = { go: undefined };
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} confirmLeave={(go) => { held.go = go; }} />);
    paste('create table A (Id int primary key)');
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
    });
    const before = requests.length;

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    // Asked, not yet created; the dialog is not "Creating..." and Cancel still works whatever the answer.
    expect(held.go).toBeDefined();
    expect(requests).toHaveLength(before);
    expect(screen.getByRole('button', { name: 'Create diagram' })).toBeTruthy();
    expect(isDisabled('Create diagram')).toBe(false);
    expect(isDisabled('Cancel')).toBe(false);

    // Answered yes: it is created and handed on.
    held.go?.();
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    expect(requests.at(-1)?.url).toBe('/api/documents');
  });

  it('is usable again after a creation whose navigation went nowhere', async () => {
    render(<ImportSqlDialog onClose={onClose} onCreated={onCreated} />);
    paste('create table A (Id int primary key)');
    fireEvent.click(screen.getByRole('button', { name: 'Read schema' }));
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalled();
    });
    // The caller did not unmount the dialog: it must not be left saying "Creating...".
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
      expect(isDisabled('Cancel')).toBe(false);
    });
  });
});
