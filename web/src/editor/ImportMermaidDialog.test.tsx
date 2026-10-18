// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ImportMermaidDialog } from './ImportMermaidDialog.tsx';

const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'mermaid-import' };

const sequenceDocument = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Imported diagram',
  type: 'sequence',
  nodes: [
    { id: 'p_A', kind: 'participant', label: 'A', shape: 'rectangle' },
    { id: 'p_B', kind: 'participant', label: 'B', shape: 'rectangle' },
  ],
  edges: [
    { id: 'm_1', kind: 'sync', label: 'hi', from: { node: 'p_A' }, to: { node: 'p_B' } },
    { id: 'm_2', kind: 'return', label: 'ok', from: { node: 'p_B' }, to: { node: 'p_A' } },
  ],
  meta,
};

const flowDocument = {
  id: 'doc_importer',
  schemaVersion: 1,
  title: 'Imported diagram',
  type: 'flowchart',
  nodes: [{ id: 'n_A', kind: 'process', label: 'A', shape: 'rounded' }],
  edges: [],
  meta,
};

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('ImportMermaidDialog', () => {
  const onClose = vi.fn();
  const onCreated = vi.fn();
  let requests: { url: string; body: string }[];
  let imported: { document: unknown; warnings: unknown[] };

  beforeEach(() => {
    onClose.mockClear();
    onCreated.mockClear();
    requests = [];
    imported = {
      document: sequenceDocument,
      warnings: [{ code: 'unsupported-syntax', message: 'Skipped "autonumber": not supported yet.', line: 2 }],
    };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((url, init) => {
      requests.push({ url: urlOf(url), body: init?.body as string });
      const path = urlOf(url);
      if (path.startsWith('/api/import/mermaid')) {
        return Promise.resolve(json(200, imported));
      }
      if (path.startsWith('/api/layout')) {
        return Promise.resolve(json(200, { document: init?.body === undefined ? flowDocument : (JSON.parse(init.body as string) as unknown), warnings: [] }));
      }
      return Promise.resolve(json(201, { id: 'doc_created' }));
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const isDisabled = (name: string): boolean => screen.getByRole('button', { name }).hasAttribute('disabled');
  const paste = (text: string): void => {
    fireEvent.change(screen.getByRole('textbox', { name: 'Mermaid' }), { target: { value: text } });
  };

  it('is a labelled modal dialog with the paste box focused and nothing to read yet', () => {
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);

    expect(screen.getByRole('dialog', { name: 'Import Mermaid' }).getAttribute('aria-modal')).toBe('true');
    expect(globalThis.document.activeElement).toBe(screen.getByRole('textbox', { name: 'Mermaid' }));
    expect(isDisabled('Read diagram')).toBe(true);
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('reads a sequenceDiagram without saving or laying out, shows the importer warnings, then creates it', async () => {
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('sequenceDiagram\nA->>B: hi\nB-->>A: ok');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Found 2 participants and 2 messages.');
    });
    const warnings = within(screen.getByRole('list', { name: 'Import warnings' })).getAllByRole('listitem');
    expect(warnings).toHaveLength(1);
    expect(warnings[0]?.textContent).toContain('Line 2');
    expect(warnings[0]?.textContent).toContain('autonumber');
    // A sequence diagram derives its geometry: reading imports and nothing else.
    expect(requests.map((request) => request.url)).toEqual(['/api/import/mermaid']);
    expect(requests[0]?.body).toBe('sequenceDiagram\nA->>B: hi\nB-->>A: ok');
    expect(onCreated).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Create diagram' }));

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('doc_created');
    });
    const create = requests.at(-1);
    expect(create?.url).toBe('/api/documents');
    expect((JSON.parse(create?.body ?? '{}') as { type: string }).type).toBe('sequence');
  });

  it('keeps the read when only the title changes, and creates under the new title', async () => {
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('sequenceDiagram\nA->>B: hi');
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
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

  it('counts participants and messages, not fragments, notes or interaction uses, and says "needs" for one warning', async () => {
    imported = {
      document: {
        ...sequenceDocument,
        nodes: [...sequenceDocument.nodes, { id: 'f_1', kind: 'fragment', label: 'alt', shape: 'rectangle' }],
        edges: [
          ...sequenceDocument.edges,
          { id: 'u_1', kind: 'note', label: 'n', from: { node: 'p_A' }, to: { node: 'p_B' } },
          { id: 'u_2', kind: 'interaction-use', label: 'Auth', from: { node: 'p_A' }, to: { node: 'p_B' } },
        ],
      },
      warnings: [{ code: 'x', message: 'one' }],
    };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('sequenceDiagram\nA->>B: hi');
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Found 2 participants and 2 messages.');
    });
    expect(screen.getByRole('status').textContent).toContain('1 item was skipped or only partly understood');
    expect(screen.getByRole('status').textContent).not.toContain('could mean more than one thing');
  });

  it('does not call kind guesses "skipped": four guesses read as a check to make, a skipped line reads as lost (QA-08)', async () => {
    imported = {
      document: flowDocument,
      warnings: [
        ...[1, 2, 3, 4].map((n) => ({ code: 'kind-guessed', message: `Node n${String(n)} was given a kind from its shape.` })),
        { code: 'unsupported-syntax', message: 'Skipped "style A fill:#f9f".', line: 6 },
      ],
    };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('flowchart TD\nA');
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('4 shapes could mean more than one thing, so their kinds were chosen for you and listed below; 1 item was skipped or only partly understood:');
    });
    expect(screen.getByRole('status').textContent).not.toContain('4 things were skipped');
  });

  it('lays a flowchart out before showing it, because its nodes have no geometry', async () => {
    imported = { document: flowDocument, warnings: [] };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('flowchart TD\nA');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Found 1 node and 0 connectors.');
    });
    expect(requests.map((request) => request.url.split('?')[0])).toEqual(['/api/import/mermaid', '/api/layout']);
    expect(screen.queryByRole('list', { name: 'Import warnings' })).toBeNull();
  });

  it('words an erDiagram as tables and relationships', async () => {
    imported = {
      document: {
        ...flowDocument,
        type: 'architecture',
        nodes: [
          { id: 't_A', kind: 'entity', label: 'A', shape: 'custom:er/entity' },
          { id: 't_B', kind: 'entity', label: 'B', shape: 'custom:er/entity' },
        ],
        edges: [{ id: 'e_1', kind: 'association', from: { node: 't_A' }, to: { node: 't_B' } }],
      },
      warnings: [],
    };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('erDiagram\nA ||--o{ B : has');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Found 2 tables and 1 relationship.');
    });
  });

  it('sends the title, and discards a read when the text changes so Create is off again', async () => {
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('sequenceDiagram\nA->>B: hi');
    fireEvent.change(screen.getByRole('textbox', { name: 'Diagram title' }), { target: { value: 'Shop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
    await waitFor(() => {
      expect(isDisabled('Create diagram')).toBe(false);
    });
    expect(requests[0]?.url).toBe('/api/import/mermaid?title=Shop');

    paste('sequenceDiagram\nA->>B: hello');

    expect(isDisabled('Create diagram')).toBe(true);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('shows the server message when the importer refuses the source, and stays creatable-off', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(json(400, { detail: 'Not a Mermaid flowchart or sequenceDiagram.' }));
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('nonsense');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe('Not a Mermaid flowchart or sequenceDiagram.');
    });
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('will not create a diagram from a read that found nothing', async () => {
    imported = { document: { ...sequenceDocument, nodes: [], edges: [] }, warnings: [] };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('sequenceDiagram');

    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toContain('Nothing was found');
    });
    expect(isDisabled('Create diagram')).toBe(true);
  });

  it('closes on Escape and Cancel', () => {
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Mermaid' }), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('asks confirmLeave before it creates anything, and a refusal leaves it ready with nothing saved', async () => {
    const held: { go: (() => void) | undefined } = { go: undefined };
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} confirmLeave={(go) => { held.go = go; }} />);
    paste('flowchart TD\nA');
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
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
    render(<ImportMermaidDialog onClose={onClose} onCreated={onCreated} />);
    paste('flowchart TD\nA');
    fireEvent.click(screen.getByRole('button', { name: 'Read diagram' }));
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
