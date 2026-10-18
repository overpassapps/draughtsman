// @vitest-environment jsdom
import { consumePendingTitleEdit, createAndOpen, DIAGRAM_TYPES } from './newDocument.ts';
import { indexDocument } from '../canvas/documentIndex.ts';
import { parseDocument } from '../model/parse.ts';

function flush(): Promise<void> {
  return Promise.resolve();
}

describe('createAndOpen / consumePendingTitleEdit', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('{"id":"doc_new"}', { status: 201 }))));
    window.history.replaceState(null, '', '/');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('marks a document created by createAndOpen pending exactly once, for the editor to consume', async () => {
    await createAndOpen('flowchart');
    expect(window.location.pathname).toBe('/d/doc_new');

    expect(consumePendingTitleEdit('doc_new')).toBe(true);
    // Consumed: a second read (a later remount of the same route) finds nothing pending.
    expect(consumePendingTitleEdit('doc_new')).toBe(false);
  });

  it('never reports a document it did not just create as pending', async () => {
    await createAndOpen('flowchart');
    await flush();
    expect(consumePendingTitleEdit('doc_untouched')).toBe(false);
  });

  it('creates a flowchart blank, with no starter nodes or edges', async () => {
    await createAndOpen('flowchart');
    const init = vi.mocked(fetch).mock.calls[0]?.[1];
    expect(JSON.parse(init?.body as string)).toEqual({ title: 'Untitled flowchart', type: 'flowchart', nodes: [], edges: [] });
  });

  it('offers Sequence and creates a sequence document that opens: User, System and one sync message', async () => {
    expect(DIAGRAM_TYPES.map((option) => option.label)).toEqual(['Flowchart', 'Architecture', 'Freeform', 'Sequence']);

    await createAndOpen('sequence');

    expect(window.location.pathname).toBe('/d/doc_new');
    const init = vi.mocked(fetch).mock.calls[0]?.[1];
    const body = JSON.parse(init?.body as string) as Record<string, unknown>;
    expect(body.type).toBe('sequence');
    expect(body.title).toBe('Untitled sequence');
    // What the server stores comes back through the editor's own parser and indexer: it must open, with a drawn message.
    const document = parseDocument({
      ...body,
      id: 'doc_new',
      schemaVersion: 1,
      meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'test' },
    });
    expect(document.nodes.map((node) => [node.id, node.kind, node.label])).toEqual([
      ['p_user', 'actor', 'User'],
      ['p_system', 'participant', 'System'],
    ]);
    expect(document.edges.map((edge) => [edge.kind, edge.from.node, edge.to.node])).toEqual([['sync', 'p_user', 'p_system']]);
    const index = indexDocument(document);
    expect(index.edgePaths.size).toBe(1);
    expect(index.nodeBounds.size).toBe(2);
  });
});
