// @vitest-environment jsdom
import { act, fireEvent, renderHook, waitFor } from '@testing-library/react';
import { StrictMode } from 'react';
import { copy } from '../model/clipboard.ts';
import type { Document, Geometry, Id } from '../model/types.ts';
import { clearSessionEndedNotice, currentSessionEndedNotice, noteSessionEnded } from '../model/sessionEnded.ts';
import { checkoutSequence, must } from '../model/sequenceFixture.ts';
import { readLocalCopies } from './localCopy.ts';
import { CONFLICT_MESSAGE, useDocumentEditor } from './useDocumentEditor.ts';

const initial: Document = {
  id: 'doc_edit',
  schemaVersion: 1,
  title: 'Edit',
  type: 'flowchart',
  nodes: [{ id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } }],
  edges: [],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};

const moved = { x: 70, y: 30, w: 100, h: 50 };
const geometryOf = (document: Document): Geometry | undefined => document.nodes[0]?.geometry;

function stubFetch(...responses: Response[]) {
  const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(responses.shift() ?? new Response('', { status: 500 })));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}
const ifMatchOf = (call: Parameters<typeof fetch> | undefined): string | null => new Headers(call?.[1]?.headers).get('If-Match');

function editorWithMove() {
  // StrictMode as main.tsx renders it: it re-runs effects on a live component, which must not break undo.
  const hook = renderHook(({ etag }: { etag: string }) => useDocumentEditor('doc_edit', initial, etag), { initialProps: { etag: '"dv1:loaded"' }, wrapper: StrictMode });
  act(() => {
    hook.result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
    hook.result.current.moveEnd();
  });
  return hook;
}

describe('useDocumentEditor', () => {
  it.each(['document', 'predefined-process', 'manual-input', 'off-page-connector'])('adds %s as exactly one undo step', (shape) => {
    const { result } = editorWithMove();
    act(() => { result.current.addNode({ id: 'n_shape', kind: 'process', label: 'Shape', shape }); });
    expect(result.current.document.nodes.find((node) => node.id === 'n_shape')?.shape).toBe(shape);
    act(() => { result.current.undo(); });
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a']);
    expect(geometryOf(result.current.document)).toEqual(moved);
    act(() => { result.current.undo(); });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    act(() => { result.current.redo(); result.current.redo(); });
    expect(result.current.document.nodes.find((node) => node.id === 'n_shape')?.shape).toBe(shape);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('makes a connection its own undo step, apart from the edits either side of it', () => {
    const pair: Document = { ...initial, nodes: [...initial.nodes, { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } }] };
    const { result } = renderHook(() => useDocumentEditor('doc_edit', pair, '"dv1:loaded"'));
    const connection = { from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } }, to: { node: 'n_b', port: { id: 'left', x: 0, y: 0.5 } } };
    act(() => {
      // A frame written before the connection, with no gesture end reported, must not share its step...
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.connect(connection);
      // ...and nor may the drag after it.
      result.current.moveNodes(new Map<Id, Geometry>([['n_b', { x: 320, y: 40, w: 100, h: 50 }]]));
      result.current.moveEnd();
    });
    expect(result.current.document.edges).toHaveLength(1);

    act(() => {
      result.current.undo();
    });
    expect(result.current.document.edges).toHaveLength(1);
    expect(result.current.document.nodes[1]?.geometry).toEqual({ x: 300, y: 0, w: 100, h: 50 });

    act(() => {
      result.current.undo();
    });
    expect(result.current.document.edges).toHaveLength(0);
    expect(geometryOf(result.current.document)).toEqual(moved);
    expect(result.current.document.nodes[0]?.ports).toBeUndefined();
  });

  it('makes a re-attach its own undo step, apart from the edits either side of it', () => {
    const trio: Document = {
      ...initial,
      nodes: [
        ...initial.nodes,
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
        { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 300, y: 200, w: 100, h: 50 } },
      ],
    };
    const { result } = renderHook(() => useDocumentEditor('doc_edit', trio, '"dv1:loaded"'));
    const top = { id: 'top', x: 0.5, y: 0 };
    let edgeId: Id = '';
    act(() => {
      edgeId = result.current.connect({ from: { node: 'n_a', port: { id: 'right', x: 1, y: 0.5 } }, to: { node: 'n_b', port: { id: 'left', x: 0, y: 0.5 } } });
      // A drag frame left open before the re-attach must not share its step...
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.reconnect({ edge: edgeId, end: 'to', to: { node: 'n_c', port: top } });
      // ...and nor may the drag after it.
      result.current.moveNodes(new Map<Id, Geometry>([['n_c', { x: 320, y: 240, w: 100, h: 50 }]]));
      result.current.moveEnd();
    });
    expect(result.current.document.edges[0]?.to).toEqual({ node: 'n_c', port: 'top' });

    act(() => {
      result.current.undo();
    });
    expect(result.current.document.edges[0]?.to).toEqual({ node: 'n_c', port: 'top' });
    expect(result.current.document.nodes[2]?.geometry).toEqual({ x: 300, y: 200, w: 100, h: 50 });

    act(() => {
      result.current.undo();
    });
    expect(result.current.document.edges[0]?.to).toEqual({ node: 'n_b', port: 'left' });
    expect(result.current.document.nodes[2]?.ports).toBeUndefined();
    expect(geometryOf(result.current.document)).toEqual(moved);

    act(() => {
      result.current.redo();
    });
    expect(result.current.document.edges[0]?.to).toEqual({ node: 'n_c', port: 'top' });
  });

  it('ignores overlapping saves and releases the guard after completion', async () => {
    let finish: (response: Response) => void = () => undefined;
    const fetchMock = vi.fn(() => new Promise<Response>((resolve) => { finish = resolve; }));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = editorWithMove();
    let first: Promise<boolean> = Promise.resolve(false);
    act(() => {
      first = result.current.save();
      void result.current.save();
    });
    await act(() => result.current.save());
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(result.current.saveStatus).toBe('saving');
    await act(async () => {
      finish(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } }));
      await first;
    });
    expect(result.current.saveStatus).toBe('saved');
    act(() => { first = result.current.save(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => {
      finish(new Response('{}', { status: 200 }));
      await first;
    });
  });

  it('is saved again once undo returns the document to what was loaded', () => {
    const { result } = editorWithMove();
    expect(result.current.saveStatus).toBe('unsaved');
    act(() => { result.current.undo(); });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    expect(result.current.saveStatus).toBe('saved');
    // ...and redo, which is a change from the saved base again, marks it unsaved.
    act(() => { result.current.redo(); });
    expect(result.current.saveStatus).toBe('unsaved');
  });

  it('is saved again when a drag ends where it began, and stays unsaved partway through one', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"'), { wrapper: StrictMode });
    act(() => { result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]])); });
    expect(result.current.saveStatus).toBe('unsaved');
    act(() => { result.current.moveNodes(new Map<Id, Geometry>([['n_a', { x: 0, y: 0, w: 100, h: 50 }]])); });
    expect(result.current.saveStatus).toBe('saved');
  });

  it('compares against the last save, not the load: undo past a save is unsaved, undo back to it is not', async () => {
    stubFetch(new Response('{}', { status: 200, headers: { ETag: '"dv1:saved"' } }));
    const { result } = editorWithMove();
    await act(() => result.current.save());
    expect(result.current.saveStatus).toBe('saved');
    act(() => {
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', { ...moved, x: 200 }]]));
      result.current.moveEnd();
    });
    expect(result.current.saveStatus).toBe('unsaved');
    act(() => { result.current.undo(); });
    expect(geometryOf(result.current.document)).toEqual(moved);
    expect(result.current.saveStatus).toBe('saved');
    act(() => { result.current.undo(); });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    expect(result.current.saveStatus).toBe('unsaved');
  });

  it('resolves a save true only when the whole document is stored', async () => {
    stubFetch(new Response('{}', { status: 200, headers: { ETag: '"dv1:saved"' } }), new Response('{"title":"Document has changed"}', { status: 412 }));
    const { result } = editorWithMove();
    let stored: boolean | undefined;
    await act(async () => { stored = await result.current.save(); });
    expect(stored).toBe(true);
    act(() => { result.current.moveNodes(new Map<Id, Geometry>([['n_a', { ...moved, x: 300 }]])); });
    await act(async () => { stored = await result.current.save(); });
    expect(stored).toBe(false);
    expect(result.current.saveStatus).toBe('conflict');
  });

  it('asks about a refused save, keeps the local edits and their undo, and keeps the edits as a local copy', async () => {
    localStorage.clear();
    stubFetch(new Response('{"title":"Document has changed"}', { status: 412 }));
    const { result } = editorWithMove();
    expect(result.current.saveStatus).toBe('unsaved');
    expect(result.current.localCopies).toEqual([]);

    await act(() => result.current.save());

    expect(result.current.saveStatus).toBe('conflict');
    expect(result.current.stale).toEqual({ busy: false, error: undefined });
    expect(result.current.saveError).toBe(CONFLICT_MESSAGE);
    expect(result.current.localCopies).toHaveLength(1);
    expect(geometryOf(result.current.localCopies[0]?.document ?? initial)).toEqual(moved);
    expect(geometryOf(result.current.document)).toEqual(moved);
    act(() => {
      result.current.undo();
    });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
  });

  it('saves the edited document from the loaded version, then from the version each save returns', async () => {
    const fetchMock = stubFetch(new Response('{}', { status: 200, headers: { ETag: '"dv1:first"' } }), new Response('{}', { status: 200, headers: { ETag: '"dv1:second"' } }));
    const { result, rerender } = editorWithMove();

    await act(() => result.current.save());
    expect(result.current.saveStatus).toBe('saved');
    expect(ifMatchOf(fetchMock.mock.calls[0])).toBe('"dv1:loaded"');
    const body = JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string) as Document;
    expect(geometryOf(body)).toEqual(moved);

    // A re-render with the same loaded version must not wind the base version back to it.
    rerender({ etag: '"dv1:loaded"' });
    await act(() => result.current.save());
    expect(ifMatchOf(fetchMock.mock.calls[1])).toBe('"dv1:first"');
  });

  it('stays unsaved when an edit lands while a save is in flight', async () => {
    let finish: (response: Response) => void = () => undefined;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => (finish = resolve))));
    const { result } = editorWithMove();

    let saving: Promise<boolean> = Promise.resolve(false);
    act(() => {
      saving = result.current.save();
    });
    act(() => {
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', { x: 1, y: 1, w: 100, h: 50 }]]));
      result.current.moveEnd();
    });
    await act(async () => {
      finish(new Response('{}', { status: 200, headers: { ETag: '"dv1:first"' } }));
      await saving;
    });
    expect(result.current.saveStatus).toBe('unsaved');
  });

  it('waits for an in-flight title rename before saving, so the PUT never races the PATCH', async () => {
    let resolvePatch: (response: Response) => void = () => undefined;
    const fetchMock = vi.fn((_url: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        return new Promise<Response>((resolve) => {
          resolvePatch = resolve;
        });
      }
      return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:afterput"' } }));
    });
    vi.stubGlobal('fetch', fetchMock);
    const { result } = editorWithMove();

    let renaming: Promise<void> = Promise.resolve();
    act(() => {
      renaming = result.current.renameTitle('New title');
    });
    let saving: Promise<boolean> = Promise.resolve(false);
    act(() => {
      saving = result.current.save();
    });
    // The save must not have sent its PUT yet: only the rename's PATCH has gone out.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe('PATCH');

    await act(async () => {
      resolvePatch(new Response('{}', { status: 200, headers: { ETag: '"dv1:afterpatch"' } }));
      await renaming;
      await saving;
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const putCall = fetchMock.mock.calls[1];
    expect(putCall?.[1]?.method).toBe('PUT');
    // The PUT quotes the version the PATCH just produced, not the stale pre-rename one.
    expect(ifMatchOf(putCall)).toBe('"dv1:afterpatch"');
    const body = JSON.parse(putCall?.[1]?.body as string) as Document;
    expect(body.title).toBe('New title');
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.saveError).toBeUndefined();
  });

  it('undoes on Cmd/Ctrl+Z and redoes on Cmd/Ctrl+Shift+Z', () => {
    const { result } = editorWithMove();
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    fireEvent.keyDown(window, { key: 'Z', ctrlKey: true, shiftKey: true });
    expect(geometryOf(result.current.document)).toEqual(moved);
  });

  it('makes an add its own undo step even straight after an unfinished gesture', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"'));
    act(() => {
      // A write whose gesture has not ended yet: its step is still open when the add arrives.
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.addNode({ id: 'n_new', kind: 'service', label: 'New', shape: 'rounded', geometry: { x: 0, y: 200, w: 160, h: 80 } });
    });
    expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_a', 'n_new']);
    act(() => {
      result.current.undo();
    });
    // Only the add went: the move before it is a separate step.
    expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_a']);
    expect(geometryOf(result.current.document)).toEqual(moved);
    act(() => {
      result.current.undo();
    });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
  });

  it('commits a label as its own undo step, separate from the drag before it', () => {
    const { result } = editorWithMove();
    act(() => {
      result.current.setLabel('n_a', 'Orders');
    });
    expect(result.current.document.nodes[0]?.label).toBe('Orders');

    // One undo takes back the label and only the label; the next takes back the drag.
    act(() => {
      result.current.undo();
    });
    expect(result.current.document.nodes[0]?.label).toBe('A');
    expect(geometryOf(result.current.document)).toEqual(moved);
    act(() => {
      result.current.undo();
    });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
  });

  it('makes a delete its own undo step, and the next edit after it another', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"'));
    act(() => {
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.deleteElements(new Set(['n_a']));
      // Added after the delete without an explicit step end: the delete must already have closed its own step.
      result.current.addNode({ id: 'n_new', kind: 'service', label: 'New', shape: 'rounded' });
    });
    expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_new']);
    act(() => {
      result.current.undo();
    });
    expect(result.current.document.nodes.map((n) => n.id)).toEqual([]);
    act(() => {
      result.current.undo();
    });
    expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_a']);
    expect(geometryOf(result.current.document)).toEqual(moved);
  });

  it('adds no undo step and no unsaved change for an unchanged label', () => {
    const hook = renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"'), { wrapper: StrictMode });
    act(() => {
      hook.result.current.setLabel('n_a', 'A');
    });
    expect(hook.result.current.saveStatus).toBe('saved');

    // After a real drag, an unchanged label commit must not sit on the stack above it: one undo reverts the drag.
    act(() => {
      hook.result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      hook.result.current.moveEnd();
      hook.result.current.setLabel('n_a', 'A');
    });
    act(() => {
      hook.result.current.undo();
    });
    expect(geometryOf(hook.result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
  });

  describe('paste and duplicate', () => {
    // Two linked nodes and a third the edge does not reach.
    const linked: Document = {
      ...initial,
      nodes: [
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 40, w: 100, h: 50 } },
        { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 400, y: 0, w: 100, h: 50 } },
      ],
      edges: [
        { id: 'e_ab', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } },
        { id: 'e_bc', kind: 'flow', from: { node: 'n_b' }, to: { node: 'n_c' } },
      ],
    };
    const editor = () => renderHook(() => useDocumentEditor('doc_edit', linked, '"dv1:loaded"'), { wrapper: StrictMode });

    it('pastes with fresh ids, the edge between the copied nodes and the offset, as one undo step after an open gesture', () => {
      const { result } = editor();
      let pasted: ReadonlySet<Id> = new Set();
      act(() => {
        // A gesture whose step is still open when the paste lands.
        result.current.moveNodes(new Map<Id, Geometry>([['n_c', { x: 400, y: 100, w: 100, h: 50 }]]));
        pasted = result.current.pasteElements(copy(result.current.document, ['n_a', 'n_b']), { x: 20, y: 20 });
      });
      const added = result.current.document.nodes.slice(3);
      const [edge] = result.current.document.edges.slice(2);
      expect(added.map((n) => n.label)).toEqual(['A', 'B']);
      expect(added.some((n) => ['n_a', 'n_b', 'n_c'].includes(n.id))).toBe(false);
      expect(added.map((n) => n.geometry)).toEqual([{ x: 20, y: 20, w: 100, h: 50 }, { x: 220, y: 60, w: 100, h: 50 }]);
      expect(result.current.document.edges).toHaveLength(3);
      expect(edge?.from.node).toBe(added[0]?.id);
      expect(edge?.to.node).toBe(added[1]?.id);
      expect(pasted).toEqual(new Set([added[0]?.id, added[1]?.id, edge?.id]));

      act(() => {
        result.current.undo();
      });
      // One undo takes the whole paste and nothing else; the gesture before it is its own step.
      expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_a', 'n_b', 'n_c']);
      expect(result.current.document.edges.map((e) => e.id)).toEqual(['e_ab', 'e_bc']);
      expect(result.current.document.nodes[2]?.geometry?.y).toBe(100);
      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(linked);
    });

    it('pastes the same payload twice with two disjoint id sets, each its own undo step', () => {
      const { result } = editor();
      const payload = copy(linked, ['n_a']);
      let first: ReadonlySet<Id> = new Set();
      let second: ReadonlySet<Id> = new Set();
      act(() => {
        first = result.current.pasteElements(payload, { x: 20, y: 20 });
        second = result.current.pasteElements(payload, { x: 20, y: 20 });
      });
      expect(first.size).toBe(1);
      expect(second.size).toBe(1);
      expect([...first].some((id) => second.has(id))).toBe(false);
      expect(result.current.document.nodes).toHaveLength(5);
      act(() => {
        result.current.undo();
      });
      expect(result.current.document.nodes.map((n) => n.id)).toEqual(['n_a', 'n_b', 'n_c', ...first]);
    });

    it('writes nothing and records no step for an empty payload', () => {
      const { result } = editor();
      act(() => {
        result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
        result.current.moveEnd();
        // Copying a lone edge whose endpoints stay behind copies nothing.
        expect(result.current.pasteElements(copy(result.current.document, ['e_ab']), { x: 20, y: 20 }).size).toBe(0);
      });
      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(linked);
    });

    it('duplicates the selection in place, offset, as one undo step', () => {
      const { result } = editor();
      let duplicated: ReadonlySet<Id> = new Set();
      act(() => {
        duplicated = result.current.duplicateElements(new Set(['n_b', 'n_c']), { x: 20, y: 20 });
      });
      const added = result.current.document.nodes.slice(3);
      expect(added.map((n) => [n.label, n.geometry?.x, n.geometry?.y])).toEqual([['B', 220, 60], ['C', 420, 20]]);
      expect(result.current.document.edges.slice(2).map((e) => [e.from.node, e.to.node])).toEqual([[added[0]?.id, added[1]?.id]]);
      expect(duplicated.size).toBe(3);
      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(linked);
    });

    it('keeps a duplicated child in its container, as one undo step', () => {
      const nested: Document = { ...linked, nodes: [
        { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 300, h: 200 } },
        { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', parent: 'c_box', geometry: { x: 20, y: 20, w: 100, h: 50 } },
      ], edges: [] };
      const { result } = renderHook(() => useDocumentEditor('doc_edit', nested, '"dv1:loaded"'), { wrapper: StrictMode });
      act(() => {
        result.current.duplicateElements(new Set(['n_in']), { x: 20, y: 20 });
      });
      expect(result.current.document.nodes[2]?.parent).toBe('c_box');
      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(nested);
    });
  });
  describe('edges', () => {
    const withEdge: Document = {
      ...initial,
      nodes: [...initial.nodes, { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } }],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
    };
    const edgeOf = (document: Document) => document.edges[0];

    it('commits an edge label as its own undo step, between the drags either side of it', () => {
      const { result } = renderHook(() => useDocumentEditor('doc_edit', withEdge, '"dv1:loaded"'), { wrapper: StrictMode });
      act(() => {
        // Neither drag reports its end before the label: the label must still be a step of its own.
        result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
        result.current.setLabel('e_1', 'HTTPS');
        result.current.moveNodes(new Map<Id, Geometry>([['n_b', { x: 320, y: 40, w: 100, h: 50 }]]));
        result.current.moveEnd();
      });
      expect(edgeOf(result.current.document)?.label).toBe('HTTPS');
      expect(result.current.document.nodes.map((n) => n.label)).toEqual(['A', 'B']);

      act(() => {
        result.current.undo();
      });
      expect(edgeOf(result.current.document)?.label).toBe('HTTPS');
      expect(result.current.document.nodes[1]?.geometry).toEqual({ x: 300, y: 0, w: 100, h: 50 });
      act(() => {
        result.current.undo();
      });
      expect(edgeOf(result.current.document)?.label).toBeUndefined();
      expect(geometryOf(result.current.document)).toEqual(moved);
    });

    it('switches an edge kind as one undo step without writing a style, and an unchanged kind records nothing', () => {
      const { result } = renderHook(() => useDocumentEditor('doc_edit', withEdge, '"dv1:loaded"'), { wrapper: StrictMode });
      act(() => {
        result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
        result.current.setEdgeKind('e_1', 'data');
        result.current.setEdgeKind('e_1', 'data');
      });
      expect(edgeOf(result.current.document)).toEqual({ id: 'e_1', kind: 'data', from: { node: 'n_a' }, to: { node: 'n_b' } });

      // One undo takes back the kind and only the kind; the repeated switch added no step above it.
      act(() => {
        result.current.undo();
      });
      expect(edgeOf(result.current.document)?.kind).toBe('flow');
      expect(geometryOf(result.current.document)).toEqual(moved);
      act(() => {
        result.current.undo();
      });
      expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    });
  });
});

describe('useDocumentEditor align, distribute and arrange', () => {
  const three: Document = {
    ...initial,
    nodes: [
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 150, y: 30, w: 40, h: 50 } },
      { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 400, y: 60, w: 60, h: 50 } },
    ],
  };
  const all = new Set(['n_a', 'n_b', 'n_c']);
  const xs = (document: Document) => document.nodes.map((n) => n.geometry?.x);
  const ys = (document: Document) => document.nodes.map((n) => n.geometry?.y);
  const order = (document: Document) => document.nodes.map((n) => n.id);

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('makes each align and distribute one undo step, separate from an unfinished gesture before it', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', three, '"dv1:loaded"'), { wrapper: StrictMode });
    act(() => {
      // A drag frame whose gesture has not ended: the align must not merge into it.
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', { x: 0, y: 10, w: 100, h: 50 }]]));
      result.current.align(all, 'top');
      result.current.distribute(all, 'horizontal');
    });
    expect(ys(result.current.document)).toEqual([10, 10, 10]);
    // Span 460, sizes 200: gaps of 130, so n_b's left is 100 + 130.
    expect(xs(result.current.document)).toEqual([0, 230, 400]);

    act(() => {
      result.current.undo();
    });
    expect(xs(result.current.document)).toEqual([0, 150, 400]);
    expect(ys(result.current.document)).toEqual([10, 10, 10]);
    act(() => {
      result.current.undo();
    });
    expect(ys(result.current.document)).toEqual([10, 30, 60]);
    act(() => {
      result.current.undo();
    });
    expect(ys(result.current.document)).toEqual([0, 30, 60]);
  });

  it('records no step and no unsaved change when the selection is already aligned', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', three, '"dv1:loaded"'));
    act(() => {
      result.current.align(new Set(['n_a']), 'left');
      result.current.align(new Set(['n_a', 'n_b']), 'top');
    });
    expect(ys(result.current.document)).toEqual([0, 0, 60]);
    act(() => {
      // n_a and n_b already share a top: a second align is a no-op and must not sit on the stack.
      result.current.align(new Set(['n_a', 'n_b']), 'top');
      result.current.undo();
    });
    expect(ys(result.current.document)).toEqual([0, 30, 60]);
  });

  it('reorders the selection as one undo step, keeps the order through a save and reload, and skips a no-op', async () => {
    const fetchMock = stubFetch(new Response('{}', { status: 200, headers: { ETag: '"dv1:first"' } }));
    const { result } = renderHook(() => useDocumentEditor('doc_edit', three, '"dv1:loaded"'), { wrapper: StrictMode });
    act(() => {
      result.current.arrange(new Set(['n_a']), 'bringToFront');
    });
    expect(order(result.current.document)).toEqual(['n_b', 'n_c', 'n_a']);

    await act(() => result.current.save());
    const saved = JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string) as Document;
    expect(order(saved)).toEqual(['n_b', 'n_c', 'n_a']);
    // Reload: a fresh editor over what the server was sent.
    const reloaded = renderHook(() => useDocumentEditor('doc_edit', saved, '"dv1:first"'));
    expect(order(reloaded.result.current.document)).toEqual(['n_b', 'n_c', 'n_a']);

    act(() => {
      // Already at the front: nothing to write, so the one undo after it takes back the bring-to-front.
      result.current.arrange(new Set(['n_a']), 'bringToFront');
      result.current.undo();
    });
    expect(order(result.current.document)).toEqual(['n_a', 'n_b', 'n_c']);
    act(() => {
      result.current.redo();
      result.current.arrange(new Set(['n_a']), 'sendBackward');
    });
    expect(order(result.current.document)).toEqual(['n_b', 'n_a', 'n_c']);
  });

  describe('containers', () => {
    // c_box (0,0 300x200) holds n_in (20,20 40x40) and n_in2 (200,120 40x40), joined by e_in with one waypoint;
    // n_out (400,0 40x40) is top level.
    const boxed: Document = {
      ...initial,
      nodes: [
        { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 300, h: 200 } },
        { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', parent: 'c_box', geometry: { x: 20, y: 20, w: 40, h: 40 } },
        { id: 'n_in2', kind: 'service', label: 'In 2', shape: 'rectangle', parent: 'c_box', geometry: { x: 200, y: 120, w: 40, h: 40 } },
        { id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 400, y: 0, w: 40, h: 40 } },
      ],
      edges: [{ id: 'e_in', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_in2' }, routing: { mode: 'straight', waypoints: [{ x: 120, y: 40 }] } }],
    };
    const nodeOf = (document: Document, id: Id) => document.nodes.find((node) => node.id === id);
    const editor = () => renderHook(() => useDocumentEditor('doc_edit', boxed, '"dv1:loaded"'), { wrapper: StrictMode });

    it('moves waypoints with the drag, and makes a drag that ends in a drop one undo step with it', () => {
      const { result } = editor();
      act(() => {
        // A step before the gesture, so undoing the gesture shows exactly where its step began.
        result.current.setLabel('n_out', 'Renamed');
        result.current.moveNodes(new Map([['n_out', { x: 300, y: 100, w: 40, h: 40 }]]));
        result.current.moveNodes(new Map([['n_out', { x: 100, y: 100, w: 40, h: 40 }]]), new Map([['e_in', [{ x: 5, y: 6 }]]]));
        result.current.reparentNodes(new Map([['n_out', 'c_box']]));
        result.current.moveEnd();
      });
      expect(nodeOf(result.current.document, 'n_out')).toMatchObject({ parent: 'c_box', geometry: { x: 100, y: 100, w: 40, h: 40 } });
      expect(result.current.document.edges[0]?.routing).toEqual({ mode: 'straight', waypoints: [{ x: 5, y: 6 }] });

      act(() => {
        result.current.undo();
      });
      const undone = nodeOf(result.current.document, 'n_out');
      expect(undone?.parent).toBeUndefined();
      expect(undone?.geometry).toEqual({ x: 400, y: 0, w: 40, h: 40 });
      expect(undone?.label).toBe('Renamed');
      expect(result.current.document.edges[0]?.routing).toEqual({ mode: 'straight', waypoints: [{ x: 120, y: 40 }] });

      act(() => {
        result.current.redo();
        // A drop out of the container clears parent.
        result.current.reparentNodes(new Map([['n_out', null]]));
        result.current.moveEnd();
      });
      expect(nodeOf(result.current.document, 'n_out')?.parent).toBeUndefined();
      expect(nodeOf(result.current.document, 'n_out')?.geometry).toEqual({ x: 100, y: 100, w: 40, h: 40 });
    });

    it('groups as one undo step, keeping every child where it was, and ungroups as another', () => {
      const { result } = editor();
      let containerId: Id | undefined;
      act(() => {
        // An unfinished gesture before the group must not share its step.
        result.current.moveNodes(new Map([['n_out', { x: 410, y: 0, w: 40, h: 40 }]]));
        containerId = result.current.group(new Set(['n_in', 'n_in2']), 40);
      });
      expect(containerId).toMatch(/^c_/);
      const grouped = result.current.document;
      expect(nodeOf(grouped, containerId ?? '')).toMatchObject({ kind: 'container', parent: 'c_box', geometry: { x: -20, y: -20, w: 300, h: 220 } });
      expect(nodeOf(grouped, 'n_in')).toMatchObject({ parent: containerId, geometry: { x: 20, y: 20, w: 40, h: 40 } });
      expect(nodeOf(grouped, 'n_in2')).toMatchObject({ parent: containerId, geometry: { x: 200, y: 120, w: 40, h: 40 } });

      let released: ReturnType<typeof result.current.ungroup>;
      act(() => {
        released = result.current.ungroup(new Set([containerId ?? '']));
      });
      expect(released).toEqual({ released: new Set(['n_in', 'n_in2']), refused: [] });
      expect(nodeOf(result.current.document, containerId ?? '')).toBeUndefined();
      expect(nodeOf(result.current.document, 'n_in')).toMatchObject({ parent: 'c_box', geometry: { x: 20, y: 20, w: 40, h: 40 } });

      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(grouped);
      act(() => {
        result.current.undo();
      });
      expect(nodeOf(result.current.document, containerId ?? '')).toBeUndefined();
      expect(nodeOf(result.current.document, 'n_in')?.parent).toBe('c_box');
      expect(nodeOf(result.current.document, 'n_out')?.geometry).toEqual({ x: 410, y: 0, w: 40, h: 40 });
    });

    it('refuses to ungroup a container with a connection of its own, writing nothing and recording no step', () => {
      const connected: Document = { ...boxed, edges: [...boxed.edges, { id: 'e_box', kind: 'flow', from: { node: 'n_out' }, to: { node: 'c_box' } }] };
      const { result } = renderHook(() => useDocumentEditor('doc_edit', connected, '"dv1:loaded"'), { wrapper: StrictMode });
      act(() => {
        result.current.setLabel('n_out', 'Renamed');
      });
      const before = result.current.document;
      let refused: ReturnType<typeof result.current.ungroup>;
      act(() => {
        refused = result.current.ungroup(new Set(['c_box']));
      });
      expect(refused).toEqual({ released: new Set(), refused: ['c_box'] });
      expect(result.current.document).toEqual(before);

      // The undo takes back the rename before the refusal: the refusal recorded no step of its own.
      act(() => {
        result.current.undo();
      });
      expect(nodeOf(result.current.document, 'n_out')?.label).toBe('Out');
      expect(result.current.document).toEqual(connected);
    });

    it('ungroups the unconnected containers of a mixed selection as one undo step and leaves the connected one', () => {
      const mixed: Document = {
        ...boxed,
        nodes: [...boxed.nodes.map((node) => (node.id === 'n_out' ? { ...node, parent: 'c_side' } : node)), { id: 'c_side', kind: 'container', label: 'Side', shape: 'rectangle', geometry: { x: 380, y: -20, w: 80, h: 80 } }],
        edges: [...boxed.edges, { id: 'e_box', kind: 'flow', from: { node: 'n_in' }, to: { node: 'c_box' } }],
      };
      const { result } = renderHook(() => useDocumentEditor('doc_edit', mixed, '"dv1:loaded"'), { wrapper: StrictMode });
      let outcome: ReturnType<typeof result.current.ungroup>;
      act(() => {
        outcome = result.current.ungroup(new Set(['c_box', 'c_side']));
      });
      expect(outcome).toEqual({ released: new Set(['n_out']), refused: ['c_box'] });
      expect(nodeOf(result.current.document, 'c_side')).toBeUndefined();
      expect(nodeOf(result.current.document, 'n_out')).toEqual({ id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 400, y: 0, w: 40, h: 40 } });
      expect(nodeOf(result.current.document, 'c_box')).toEqual(boxed.nodes[0]);
      expect(nodeOf(result.current.document, 'n_in')?.parent).toBe('c_box');
      expect(result.current.document.edges).toEqual(mixed.edges);

      act(() => {
        result.current.undo();
      });
      expect(result.current.document).toEqual(mixed);
    });

    it('fits a container as one undo step and records nothing when there is nothing to group, ungroup or fit', () => {
      const { result } = editor();
      act(() => {
        result.current.fitToContents(new Set(['c_box']), 40);
      });
      expect(nodeOf(result.current.document, 'c_box')?.geometry).toEqual({ x: -20, y: -20, w: 300, h: 220 });

      act(() => {
        expect(result.current.group(new Set(['e_in']), 40)).toBeUndefined();
        expect(result.current.ungroup(new Set(['n_out']))).toBeUndefined();
        result.current.fitToContents(new Set(['c_box']), 40);
        result.current.undo();
      });
      expect(result.current.document).toEqual(boxed);
      // Back at the loaded document, so nothing is unsaved (it used to stay "unsaved" for good).
      expect(result.current.saveStatus).toBe('saved');
    });
  });
});

it('bounds a text application on both sides and makes a no-op consume no history step', () => {
  const { result } = renderHook(() => useDocumentEditor(initial.id, initial, '"v"'));
  act(() => { result.current.moveNodes(new Map([['n_a', moved]])); });
  const base = result.current.document;
  const edited = { ...base, title: 'From text', nodes: [...base.nodes, { id: 'n_b', kind: 'process', shape: 'rectangle', label: 'Added' }] };
  act(() => { result.current.applyTextDocument(edited, base); });
  expect(result.current.document.title).toBe('From text');
  expect(result.current.document.nodes).toHaveLength(2);
  act(() => { result.current.applyTextDocument(edited, edited); });
  act(() => { result.current.setLabel('n_a', 'After'); });
  act(() => { result.current.undo(); });
  expect(result.current.document).toEqual(edited);
  act(() => { result.current.undo(); });
  expect(result.current.document).toEqual(base);
  act(() => { result.current.undo(); });
  expect(result.current.document).toEqual(initial);
  expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  act(() => { result.current.redo(); result.current.redo(); result.current.redo(); });
  expect(result.current.document.title).toBe('From text');
  expect(result.current.document.nodes[0]?.label).toBe('After');
});

describe('text applied to a sequence diagram', () => {
  it('lands a message typed between two others where it was put, as one undo step, though the canvas will not move a sequence by hand', () => {
    const sequence = checkoutSequence();
    const { result } = renderHook(() => useDocumentEditor(sequence.id, sequence, '"v"'));
    const typed: Document = {
      ...sequence,
      edges: [...sequence.edges.slice(0, 2), { ...must(sequence.edges[0]), id: 'm_typed', label: 'Typed between two messages' }, ...sequence.edges.slice(2)],
    };
    act(() => { result.current.applyTextDocument(typed, sequence); });
    expect(result.current.document.edges.map((edge) => edge.id)).toEqual(typed.edges.map((edge) => edge.id));
    // A move on a derived-geometry diagram is refused; text is not a geometry gesture, so it is not.
    act(() => { result.current.moveNodes(new Map([['p_user', { x: 5, y: 5, w: 10, h: 10 }]])); });
    expect(result.current.document.nodes).toEqual(typed.nodes);
    act(() => { result.current.undo(); });
    expect(result.current.document).toEqual(sequence);
    act(() => { result.current.undo(); });
    expect(result.current.document).toEqual(sequence);
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });
});

describe('text applied onto a diagram that moved on', () => {
  const base = { ...initial, nodes: [{ id: 'n_a', kind: 'process', shape: 'rectangle', label: 'A' }, { id: 'n_b', kind: 'process', shape: 'rectangle', label: 'B' }] } as Document;
  const open = () => renderHook(() => useDocumentEditor(base.id, base, '"v"'));

  it('folds the text into the diagram as one undo step, keeping the change made meanwhile and not undoing it', () => {
    const { result } = open();
    act(() => { result.current.setLabel('n_b', 'B from the canvas'); });
    const meanwhile = result.current.document;
    // The text was made from `base`: it changes A and adds a node, and says nothing new about B.
    const typed: Document = { ...base, title: 'Typed', nodes: [{ ...must(base.nodes[0]), label: 'A typed' }, must(base.nodes[1]), { id: 'n_c', kind: 'process', shape: 'rectangle', label: 'New' }] };
    let conflicts: unknown;
    act(() => { conflicts = result.current.mergeTextDocument(typed, base).conflicts; });
    expect(conflicts).toEqual([]);
    expect(result.current.document.title).toBe('Typed');
    expect(result.current.document.nodes.map((node) => node.label)).toEqual(['A typed', 'B from the canvas', 'New']);
    act(() => { result.current.undo(); });
    expect(result.current.document).toEqual(meanwhile);
    act(() => { result.current.undo(); });
    expect(result.current.document).toEqual(base);
    expect(result.current.saveStatus).toBe('saved');
  });

  it('refuses text that is not valid, writes nothing and leaves no undo step', () => {
    const { result } = open();
    const before = result.current.document;
    const bad = { ...base, nodes: [...base.nodes, { id: 'n_a', kind: 'process', shape: 'rectangle', label: 'again' }] } as Document;
    expect(() => { act(() => { result.current.mergeTextDocument(bad, base); }); }).toThrow(/unique id/);
    expect(result.current.document).toEqual(before);
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });

  it('keeps the diagram\'s side of a clash, and takes the text\'s as its own undo step on request', () => {
    const { result } = open();
    act(() => { result.current.setLabel('n_a', 'A from the canvas'); });
    const typed: Document = { ...base, nodes: [{ ...must(base.nodes[0]), label: 'A typed' }, must(base.nodes[1])] };
    let conflicts: ReturnType<typeof result.current.mergeTextDocument>['conflicts'] = [];
    act(() => { conflicts = result.current.mergeTextDocument(typed, base).conflicts; });
    expect(conflicts).toHaveLength(1);
    expect(result.current.document.nodes[0]?.label).toBe('A from the canvas');
    act(() => { result.current.takeTextConflicts(typed, conflicts); });
    expect(result.current.document.nodes[0]?.label).toBe('A typed');
    act(() => { result.current.undo(); });
    expect(result.current.document.nodes[0]?.label).toBe('A from the canvas');
  });
});

/**
 * A stand-in for the document endpoints as docs/api.md defines them: a conditional GET that answers 304 while the
 * caller's If-None-Match is the stored version, and a PUT guarded by If-Match. `agentWrite` changes the stored
 * document the way patch_document does, moving the version on.
 */
function fakeServer(start: Document, version = '"dv1:loaded"') {
  let stored = structuredClone(start);
  let current = version;
  let writes = 0;
  const fetchMock = vi.fn<typeof fetch>((_input, init) => {
    const headers = new Headers(init?.headers);
    if (init?.method === 'PUT') {
      if (headers.get('If-Match') !== current) {
        return Promise.resolve(new Response('{"title":"Document has changed"}', { status: 412 }));
      }
      stored = JSON.parse(init.body as string) as Document;
      writes += 1;
      current = `"dv1:save${String(writes)}"`;
      return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: current } }));
    }
    if (headers.get('If-None-Match') === current) {
      return Promise.resolve(new Response(null, { status: 304, headers: { ETag: current } }));
    }
    return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: current } }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    fetchMock,
    agentWrite(change: (document: Document) => void): string {
      const next = structuredClone(stored);
      change(next);
      next.meta = { ...next.meta, updatedAt: '2026-09-28T10:00:00.000Z', aiProvenance: [{ agent: 'agent:claude', at: '2026-09-28T10:00:00.000Z', action: 'updated', elementIds: [] }] };
      stored = next;
      writes += 1;
      current = `"dv1:agent${String(writes)}"`;
      return current;
    },
    get stored() {
      return stored;
    },
    get version() {
      return current;
    },
    puts: () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT'),
  };
}

function relabel(document: Document, id: Id, label: string): void {
  document.nodes = document.nodes.map((node) => (node.id === id ? { ...node, label } : node));
}

describe('useDocumentEditor live sync', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const openEditor = (liveSyncIntervalMs = 10) =>
    renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"', { liveSyncIntervalMs }), { wrapper: StrictMode });
  const labelOf = (document: Document, id: Id): string | undefined => document.nodes.find((node) => node.id === id)?.label;
  const agentNode = { id: 'n_agent', kind: 'service', label: 'From the agent', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } };

  it('merges an agent change into the open document, keeps the unsaved local edit, and Cmd/Ctrl+Z undoes only the local one', async () => {
    const server = fakeServer(initial);
    const { result } = openEditor();
    act(() => {
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.moveEnd();
    });

    server.agentWrite((document) => {
      relabel(document, 'n_a', 'A (agent)');
      document.nodes.push(agentNode);
    });
    await waitFor(() => { expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)'); });

    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a', 'n_agent']);
    expect(geometryOf(result.current.document)).toEqual(moved);
    expect(result.current.saveStatus).toBe('unsaved');
    expect(result.current.remote).toEqual({ by: 'agent:claude', conflicts: [] });

    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(geometryOf(result.current.document)).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)');
    fireEvent.keyDown(window, { key: 'z', metaKey: true });
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a', 'n_agent']);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)');
  });

  it('leaves the status saved when an agent change lands on a document with no local edits', async () => {
    const server = fakeServer(initial);
    const { result } = openEditor();
    server.agentWrite((document) => { document.nodes.push(agentNode); });
    await waitFor(() => { expect(result.current.document.nodes).toHaveLength(2); });
    expect(result.current.saveStatus).toBe('saved');
  });

  it('saves after an agent change from the version the poll handed out, with both sides in the body', async () => {
    const server = fakeServer(initial);
    const { result } = openEditor();
    act(() => {
      result.current.moveNodes(new Map<Id, Geometry>([['n_a', moved]]));
      result.current.moveEnd();
    });
    const agentVersion = server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent)'); });
    await waitFor(() => { expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)'); });

    await act(() => result.current.save());

    expect(ifMatchOf(server.puts()[0])).toBe(agentVersion);
    expect(result.current.saveStatus).toBe('saved');
    expect(server.stored.nodes[0]).toMatchObject({ label: 'A (agent)', geometry: moved });
  });

  it('keeps a clashing local edit on screen and reports it; taking theirs is one undo step and keeping mine writes nothing', async () => {
    const server = fakeServer(initial);
    const { result } = openEditor();
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent)'); });
    await waitFor(() => { expect(result.current.remote?.conflicts).toHaveLength(1); });

    expect(result.current.remote?.conflicts).toEqual([{ kind: 'node', id: 'n_a', reason: 'both-changed', fields: ['label'] }]);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');

    act(() => { result.current.takeTheirs(); });
    expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)');
    expect(result.current.remote).toBeUndefined();
    act(() => { result.current.undo(); });
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');

    server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent, again)'); });
    await waitFor(() => { expect(result.current.remote?.conflicts).toHaveLength(1); });
    act(() => { result.current.keepLocal(); });
    expect(result.current.remote).toBeUndefined();
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');
  });

  it('asks instead of merging when a save is refused because an agent saved first, and live sync waits for the answer', async () => {
    localStorage.clear();
    const server = fakeServer(initial);
    // A fast poll: if it were allowed to merge during the question, the agent's node would appear within this test.
    const { result } = openEditor(10);
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    // The agent relabels the same node and adds another: a real clash on n_a, so the save asks (a change the user did not touch would just be taken in).
    server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent)'); document.nodes.push(agentNode); });

    await act(() => result.current.save());
    expect(result.current.saveStatus).toBe('conflict');
    await act(() => new Promise((resolve) => setTimeout(resolve, 80)));

    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a']);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');
    expect(result.current.saveStatus).toBe('conflict');
    // Answering it (here by taking theirs) lets the editor show the agent's side again.
    await act(() => result.current.reloadTheirs());
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a', 'n_agent']);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (agent)');
    expect(result.current.saveStatus).toBe('saved');
  });

  // QA S10: whether the autosave PUT or the live-sync poll reached the server's newer version first decided whether a harmless
  // difference raised "Out of date". The poll merges what the user did not touch; now a refused save does the same and saves again.
  it('takes in a change to something the user did not touch when a save is refused as stale, and saves it, with no question asked', async () => {
    localStorage.clear();
    const server = fakeServer(initial);
    const { result } = openEditor(60_000);
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    server.agentWrite((document) => { document.nodes.push(agentNode); });

    let stored = false;
    await act(async () => { stored = await result.current.save(); });

    expect(stored).toBe(true);
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a', 'n_agent']);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');
    // What the server now holds is the user's edit and the agent's node together, written with the agent's version.
    expect(labelOf(server.stored, 'n_a')).toBe('A (mine)');
    expect(server.stored.nodes.map((node) => node.id)).toEqual(['n_a', 'n_agent']);
    expect(readLocalCopies('doc_edit')).toEqual([]);
  });

  it('still asks when the newer version clashes with what the user changed, and leaves their document untouched while it does', async () => {
    localStorage.clear();
    const server = fakeServer(initial);
    const { result } = openEditor(60_000);
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent)'); document.nodes.push(agentNode); });

    await act(async () => { await result.current.save(); });

    expect(result.current.saveStatus).toBe('conflict');
    // The trial merge touched a copy only: nothing of the agent's reached the user's document.
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(['n_a']);
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine)');
    expect(labelOf(server.stored, 'n_a')).toBe('A (agent)');
  });

  // QA S9: a session that ends behind the person's back swaps the page for the sign-in screen, which would take unsaved edits with it.
  it('keeps unsaved edits aside, and says so, when the session ends behind the person’s back', () => {
    localStorage.clear();
    clearSessionEndedNotice();
    fakeServer(initial);
    const { result } = openEditor(60_000);
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    expect(result.current.saveStatus).toBe('unsaved');
    expect(readLocalCopies('doc_edit')).toEqual([]);

    act(() => { noteSessionEnded('revoked'); });

    const kept = readLocalCopies('doc_edit');
    expect(kept).toHaveLength(1);
    const keptDocument = kept[0]?.document;
    if (keptDocument === undefined) {
      throw new Error('no copy kept');
    }
    expect(labelOf(keptDocument, 'n_a')).toBe('A (mine)');
    expect(currentSessionEndedNotice()).toEqual({ reason: 'revoked', keptWork: true });
    clearSessionEndedNotice();
  });

  it('keeps nothing aside, and claims nothing, when everything was already saved', () => {
    localStorage.clear();
    clearSessionEndedNotice();
    fakeServer(initial);
    openEditor(60_000);

    act(() => { noteSessionEnded('deactivated'); });

    expect(readLocalCopies('doc_edit')).toEqual([]);
    expect(currentSessionEndedNotice()).toEqual({ reason: 'deactivated', keptWork: false });
    clearSessionEndedNotice();
  });

  // QA S10: the copy set aside by Reload theirs is a net under a choice the person made; it must not nag on every later open.
  it('offers the copy set aside by Reload theirs for the rest of the visit, but not on the next open', async () => {
    localStorage.clear();
    const server = fakeServer(initial);
    const { result } = openEditor(60_000);
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    server.agentWrite((document) => { relabel(document, 'n_a', 'A (agent)'); });
    await act(async () => { await result.current.save(); });
    expect(result.current.saveStatus).toBe('conflict');

    await act(() => result.current.reloadTheirs());

    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.localCopies).toHaveLength(1);
    expect(readLocalCopies('doc_edit').map((copy) => copy.answered)).toEqual([true]);

    // Opening the document again: the server's version is what is shown, and no strip about the answered copy.
    const again = renderHook(() => useDocumentEditor('doc_edit', server.stored, server.version), { wrapper: StrictMode });
    expect(again.result.current.localCopies).toEqual([]);
    // It is still stored for a week, so Reload theirs is never an unrecoverable click within that time.
    expect(readLocalCopies('doc_edit')).toHaveLength(1);
  });

  it('drops a poll that was in flight when a save started, so its own save never comes back as another writer’s clash', async () => {
    const server = fakeServer(initial);
    const serve = server.fetchMock.getMockImplementation();
    if (serve === undefined) {
      throw new Error('fake server has no implementation');
    }
    let answerPoll: (() => void) | undefined;
    let answerSave: (() => void) | undefined;
    server.fetchMock.mockImplementation((input, init) => {
      if (init?.method === 'PUT') {
        // The server commits the save at once; its response is held back.
        const committed = serve(input, init);
        return new Promise<Response>((resolve) => { answerSave = () => { void committed.then(resolve); }; });
      }
      if (answerPoll === undefined) {
        // The first poll is held, then answered with whatever is stored by then: the save below.
        return new Promise<Response>((resolve) => { answerPoll = () => { void serve(input, { ...init, headers: {} }).then(resolve); }; });
      }
      return serve(input, init);
    });
    const { result } = openEditor();
    act(() => { result.current.setLabel('n_a', 'A (mine)'); });
    await waitFor(() => { expect(answerPoll).toBeDefined(); });

    let saving: Promise<boolean> = Promise.resolve(false);
    act(() => { saving = result.current.save(); });
    await waitFor(() => { expect(answerSave).toBeDefined(); });
    act(() => { result.current.setLabel('n_a', 'A (mine, again)'); });
    await act(async () => {
      answerPoll?.();
      await new Promise((resolve) => setTimeout(resolve, 30));
    });
    await act(async () => {
      answerSave?.();
      await saving;
    });

    expect(result.current.remote).toBeUndefined();
    expect(labelOf(result.current.document, 'n_a')).toBe('A (mine, again)');
    expect(result.current.saveStatus).toBe('unsaved');
  });

  it('checks at once when the tab becomes visible again, rather than waiting out a throttled timer', async () => {
    const server = fakeServer(initial);
    const { result } = openEditor(60_000);
    server.agentWrite((document) => { document.nodes.push(agentNode); });
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(result.current.document.nodes).toHaveLength(1);

    globalThis.document.dispatchEvent(new Event('visibilitychange'));

    await waitFor(() => { expect(result.current.document.nodes).toHaveLength(2); }, { timeout: 15_000 });
  });

  it('does not poll without an interval, as previews and the Try page open documents', async () => {
    const server = fakeServer(initial);
    renderHook(() => useDocumentEditor('doc_edit', initial, '"dv1:loaded"'));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(server.fetchMock).not.toHaveBeenCalled();
  });
});

describe('useDocumentEditor column rename', () => {
  const tables: Document = {
    ...initial,
    type: 'architecture',
    nodes: [
      { id: 't_order', kind: 'entity', label: 'Order', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'FK CustomerId int not null', 'Placed datetime2 not null'] } },
      { id: 't_customer', kind: 'entity', label: 'Customer', shape: 'custom:er/entity', geometry: { x: 500, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null'] } },
    ],
    edges: [{ id: 'e_fk', kind: 'association', from: { node: 't_order', port: 'col:CustomerId' }, to: { node: 't_customer', port: 'col:Id' }, routing: { mode: 'orthogonal' } }],
  };
  const endsOf = (document: Document) => [document.edges[0]?.from.port, document.edges[0]?.to.port];

  it('keeps a connector on its column when the column is renamed, in one undo step with the edit', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', tables, '"dv1:loaded"'));
    act(() => { result.current.setClassMembers('t_order', 'columns', ['PK Id int not null', 'FK Customer int not null', 'Placed datetime2 not null']); });
    expect(endsOf(result.current.document)).toEqual(['col:Customer', 'col:Id']);
    act(() => { result.current.undo(); });
    expect(endsOf(result.current.document)).toEqual(['col:CustomerId', 'col:Id']);
    expect(result.current.document.nodes[0]?.props).toEqual(tables.nodes[0]?.props);
    act(() => { result.current.redo(); });
    expect(endsOf(result.current.document)).toEqual(['col:Customer', 'col:Id']);
  });

  it('renames only ends on the edited table: a column of the same name on the other table stays', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', tables, '"dv1:loaded"'));
    act(() => { result.current.setClassMembers('t_order', 'columns', ['PK OrderId int not null', 'FK CustomerId int not null', 'Placed datetime2 not null']); });
    // Order's Id became OrderId; the edge's far end is Customer's own Id, which did not change.
    expect(endsOf(result.current.document)).toEqual(['col:CustomerId', 'col:Id']);
  });

  it('leaves a connector alone when a column is added or removed rather than renamed', () => {
    const { result } = renderHook(() => useDocumentEditor('doc_edit', tables, '"dv1:loaded"'));
    act(() => { result.current.setClassMembers('t_order', 'columns', ['PK Id int not null', 'Region text', 'FK CustomerId int not null', 'Placed datetime2 not null']); });
    expect(endsOf(result.current.document)).toEqual(['col:CustomerId', 'col:Id']);
  });
});
