import * as Y from 'yjs';
import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from './parse.ts';
import { arrange } from './arrange.ts';
import {
  addElements,
  addNodes,
  createUndoManager,
  documentToYDoc,
  endUndoStep,
  removalFor,
  removeElements,
  setEdgeKind,
  setEdgeLabel,
  setElementOrder,
  setNodeGeometries,
  setNodeLabel,
  translateGeometries,
  yDocToDocument,
} from './editor.ts';
import type { Document, Geometry, Id } from './types.ts';

/** Every JSON example in docs/examples, so a new one is covered by the round-trip without being remembered by hand. */
const examples = import.meta.glob<{ default: unknown }>('../../../docs/examples/*.json', { eager: true });

describe('Yjs document bridge', () => {
  it('round-trips every document in docs/examples to identical bytes', () => {
    expect(Object.keys(examples).length).toBeGreaterThan(0);
    for (const [path, module] of Object.entries(examples)) {
      const document = parseDocument(module.default);
      const roundtripped = yDocToDocument(documentToYDoc(document));
      // Against the file as read from disk, not a second copy of what went in: parseDocument adds nothing to compare.
      expect(roundtripped, path).toEqual(module.default);
      // Byte-identical too, because the canonical member order is part of the wire contract the C# side matches.
      expect(JSON.stringify(roundtripped), path).toBe(JSON.stringify(document));
    }
  });

  it('builds the map shape sidecar/src/yjs/shape.ts materialises', () => {
    // Names written out here, not taken from editor.ts's constants, so renaming one on this side alone fails.
    const doc = documentToYDoc(parseDocument(orderService));
    expect([...doc.share.keys()].sort()).toEqual(['edges', 'header', 'meta', 'nodes']);
    const node = doc.getMap<Y.Map<unknown>>('nodes').get('n_web');
    expect(node).toBeInstanceOf(Y.Map);
    expect(node?.get('order')).toBe(0);
    expect(node?.get('geometry')).toEqual({ x: 40, y: 40, w: 320, h: 80 });
    expect(doc.getMap('header').get('schemaVersion')).toBe(1);
    expect(doc.getMap('meta').get('createdBy')).toBeTypeOf('string');
  });

  it('does not lose a node without geometry or an absent theme', () => {
    const document: Document = {
      id: 'doc_no_pos',
      schemaVersion: 1,
      title: 'No position',
      type: 'flowchart',
      nodes: [
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 1, y: 2, w: 3, h: 4 } },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle' },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
      meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
    };
    const roundtripped = yDocToDocument(documentToYDoc(document));
    expect(roundtripped.nodes[1]?.geometry).toBeUndefined();
    expect(roundtripped.theme).toBeUndefined();
    expect(JSON.stringify(roundtripped)).toBe(JSON.stringify(document));
  });
});

describe('undo scoping', () => {
  const base: Document = {
    id: 'doc_1',
    schemaVersion: 1,
    title: 'Undo',
    type: 'architecture',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    ],
    edges: [],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };
  const origin = { agent: 'claude-code' };
  const geometry = (doc: Y.Doc, id: Id): Geometry | undefined => yDocToDocument(doc).nodes.find((n) => n.id === id)?.geometry;

  it('undoes a local edit', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 400, y: 400, w: 100, h: 50 }]]));
    expect(geometry(doc, 'n_a')).toEqual({ x: 400, y: 400, w: 100, h: 50 });
    undo.undo();
    expect(geometry(doc, 'n_a')).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    undo.redo();
    expect(geometry(doc, 'n_a')).toEqual({ x: 400, y: 400, w: 100, h: 50 });
  });

  it('does not undo an agent edit even when it is the most recent change', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    // The agent edit carries a different origin, so the undo manager never records it at all.
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 9, y: 9, w: 100, h: 50 }]]), origin);
    undo.undo();
    expect(geometry(doc, 'n_a')).toEqual({ x: 9, y: 9, w: 100, h: 50 });
  });

  it('undoes only the local edit when an agent edit landed after the local one', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 1, y: 1, w: 100, h: 50 }]]));
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_b', { x: 9, y: 9, w: 100, h: 50 }]]), origin);
    undo.undo();
    // The local move on n_a is reverted; the agent move on n_b is untouched.
    expect(geometry(doc, 'n_a')).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    expect(geometry(doc, 'n_b')).toEqual({ x: 9, y: 9, w: 100, h: 50 });
  });

  it('keeps a whole drag as one undo entry however slowly it moves', async () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    // Frame-writes of one drag with a real pause past Yjs's default 500ms capture window, which must not split it.
    // Real time because Yjs reads the clock through lib0, which captured Date.now before any spy could replace it.
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 10, y: 0, w: 100, h: 50 }]]));
    await new Promise((resolve) => setTimeout(resolve, 600));
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 30, y: 0, w: 100, h: 50 }]]));
    endUndoStep(undo);
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(geometry(doc, 'n_a')).toEqual({ x: 0, y: 0, w: 100, h: 50 });
    expect(undo.canUndo()).toBe(false);
  });

  it('keeps two drags as two undo entries however quickly they follow each other', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 10, y: 0, w: 100, h: 50 }]]));
    endUndoStep(undo);
    setNodeGeometries(doc, new Map<Id, Geometry>([['n_a', { x: 50, y: 0, w: 100, h: 50 }]]));
    endUndoStep(undo);
    expect(undo.undoStack.length).toBe(2);
    undo.undo();
    expect(geometry(doc, 'n_a')).toEqual({ x: 10, y: 0, w: 100, h: 50 });
  });

  it('skips a node another writer removed while it was being dragged', () => {
    const doc = documentToYDoc(base);
    doc.transact(() => {
      doc.getMap('nodes').delete('n_b');
    }, origin);
    setNodeGeometries(
      doc,
      new Map<Id, Geometry>([
        ['n_a', { x: 5, y: 5, w: 100, h: 50 }],
        ['n_b', { x: 7, y: 7, w: 100, h: 50 }],
      ]),
    );
    expect(yDocToDocument(doc).nodes.map((n) => n.id)).toEqual(['n_a']);
    expect(geometry(doc, 'n_a')).toEqual({ x: 5, y: 5, w: 100, h: 50 });
  });
});

describe('setNodeLabel', () => {
  const base: Document = {
    id: 'doc_label',
    schemaVersion: 1,
    title: 'Labels',
    type: 'flowchart',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'Orders', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'Billing', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    ],
    edges: [],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };
  const labels = (doc: Y.Doc): string[] => yDocToDocument(doc).nodes.map((n) => n.label);

  it('writes a changed label as one undoable local step and touches no other node', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    expect(setNodeLabel(doc, 'n_a', 'Order service')).toBe(true);
    endUndoStep(undo);
    expect(labels(doc)).toEqual(['Order service', 'Billing']);
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(labels(doc)).toEqual(['Orders', 'Billing']);
    undo.redo();
    expect(labels(doc)).toEqual(['Order service', 'Billing']);
  });

  it('writes nothing, and records no undo step, when the label is unchanged', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    let updates = 0;
    doc.on('update', () => {
      updates += 1;
    });
    expect(setNodeLabel(doc, 'n_a', 'Orders')).toBe(false);
    endUndoStep(undo);
    expect(updates).toBe(0);
    expect(undo.undoStack.length).toBe(0);
  });

  it('writes nothing for a node another writer removed', () => {
    const doc = documentToYDoc(base);
    doc.transact(() => {
      doc.getMap('nodes').delete('n_b');
    }, { agent: 'claude-code' });
    const undo = createUndoManager(doc);
    expect(setNodeLabel(doc, 'n_b', 'Payments')).toBe(false);
    expect(labels(doc)).toEqual(['Orders']);
    expect(undo.undoStack.length).toBe(0);
  });
});

describe('edge writes', () => {
  const base: Document = {
    id: 'doc_edges',
    schemaVersion: 1,
    title: 'Edges',
    type: 'architecture',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'Orders', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'Billing', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    ],
    edges: [
      { id: 'e_1', kind: 'flow', label: 'HTTPS', from: { node: 'n_a' }, to: { node: 'n_b' } },
      { id: 'e_2', kind: 'data', from: { node: 'n_b' }, to: { node: 'n_a' }, style: { stroke: '#123456' } },
    ],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };
  const edgesOf = (doc: Y.Doc): Document['edges'] => yDocToDocument(doc).edges;
  const counting = (doc: Y.Doc): (() => number) => {
    let updates = 0;
    doc.on('update', () => {
      updates += 1;
    });
    return () => updates;
  };

  it('writes a changed edge label as one undoable local step and touches no other edge', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    expect(setEdgeLabel(doc, 'e_1', 'gRPC')).toBe(true);
    expect(setEdgeLabel(doc, 'e_2', 'read/write')).toBe(true);
    endUndoStep(undo);
    expect(edgesOf(doc).map((e) => e.label)).toEqual(['gRPC', 'read/write']);
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(edgesOf(doc)).toEqual(base.edges);
  });

  it('removes the label field for an empty label, and writes nothing for an empty label on an unlabelled edge', () => {
    const doc = documentToYDoc(base);
    const updates = counting(doc);
    expect(setEdgeLabel(doc, 'e_1', '')).toBe(true);
    expect(edgesOf(doc)[0]).toEqual({ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } });
    expect(Object.hasOwn(edgesOf(doc)[0] ?? {}, 'label')).toBe(false);
    expect(updates()).toBe(1);
    expect(setEdgeLabel(doc, 'e_2', '')).toBe(false);
    expect(updates()).toBe(1);
  });

  it('writes nothing, and records no undo step, for an unchanged label, a node id, or an edge another writer removed', () => {
    const doc = documentToYDoc(base);
    doc.transact(() => {
      doc.getMap('edges').delete('e_2');
    }, { agent: 'claude-code' });
    const undo = createUndoManager(doc);
    const updates = counting(doc);
    expect(setEdgeLabel(doc, 'e_1', 'HTTPS')).toBe(false);
    expect(setEdgeLabel(doc, 'n_a', 'Not an edge')).toBe(false);
    expect(setEdgeLabel(doc, 'e_2', 'Gone')).toBe(false);
    endUndoStep(undo);
    expect(updates()).toBe(0);
    expect(undo.undoStack.length).toBe(0);
    expect(yDocToDocument(doc).nodes.map((n) => n.label)).toEqual(['Orders', 'Billing']);
  });

  it('switches an edge kind as one undoable local step and leaves the edge style exactly as it was', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    expect(setEdgeKind(doc, 'e_1', 'data')).toBe(true);
    expect(setEdgeKind(doc, 'e_2', 'dependency')).toBe(true);
    endUndoStep(undo);
    const [first, second] = edgesOf(doc);
    expect(first).toEqual({ id: 'e_1', kind: 'data', label: 'HTTPS', from: { node: 'n_a' }, to: { node: 'n_b' } });
    // No style appears where there was none, and a style that was there is not rewritten.
    expect(Object.hasOwn(first ?? {}, 'style')).toBe(false);
    expect(second?.style).toEqual({ stroke: '#123456' });
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(edgesOf(doc).map((e) => e.kind)).toEqual(['flow', 'data']);
  });

  it('writes nothing for an unchanged kind or an id that names no edge', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    const updates = counting(doc);
    expect(setEdgeKind(doc, 'e_1', 'flow')).toBe(false);
    expect(setEdgeKind(doc, 'n_a', 'flow')).toBe(false);
    endUndoStep(undo);
    expect(updates()).toBe(0);
    expect(undo.undoStack.length).toBe(0);
    expect(yDocToDocument(doc).nodes.map((n) => n.kind)).toEqual(['service', 'service']);
  });
});

describe('translateGeometries', () => {
  it('shifts every node by the delta, preserving size and rotation', () => {
    const starts = new Map<Id, Geometry>([
      ['n_a', { x: 10, y: 20, w: 100, h: 50, rotation: 15 }],
      ['n_b', { x: 200, y: 40, w: 80, h: 80 }],
    ]);
    expect(translateGeometries(starts, 5, -7)).toEqual(
      new Map<Id, Geometry>([
        ['n_a', { x: 15, y: 13, w: 100, h: 50, rotation: 15 }],
        ['n_b', { x: 205, y: 33, w: 80, h: 80 }],
      ]),
    );
  });

  it('produces nothing for nothing', () => {
    expect(translateGeometries(new Map(), 1, 2)).toEqual(new Map());
  });
});
describe('adding and removing elements', () => {
  // A container holding a node that holds a node, a free node, and edges that touch each of them.
  const base: Document = {
    id: 'doc_rm',
    schemaVersion: 1,
    title: 'Remove',
    type: 'architecture',
    nodes: [
      { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 300 } },
      { id: 'c_inner', kind: 'container', label: 'Inner', shape: 'rectangle', geometry: { x: 10, y: 10, w: 200, h: 200 }, parent: 'c_box' },
      { id: 'n_deep', kind: 'service', label: 'Deep', shape: 'rectangle', geometry: { x: 20, y: 20, w: 50, h: 50 }, parent: 'c_inner' },
      { id: 'n_free', kind: 'service', label: 'Free', shape: 'rectangle', geometry: { x: 500, y: 0, w: 100, h: 50 } },
      { id: 'n_other', kind: 'service', label: 'Other', shape: 'rectangle', geometry: { x: 700, y: 0, w: 100, h: 50 } },
    ],
    edges: [
      { id: 'e_in', kind: 'data', from: { node: 'n_free' }, to: { node: 'n_deep' } },
      { id: 'e_out', kind: 'data', from: { node: 'n_free' }, to: { node: 'n_other' } },
    ],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
  };
  const ids = (document: Document) => ({ nodes: document.nodes.map((n) => n.id), edges: document.edges.map((e) => e.id) });

  it('removes a node, every edge attached to it, and nothing else', () => {
    const removal = removalFor(base.nodes, base.edges, new Set(['n_other']));
    expect([...removal.nodes]).toEqual(['n_other']);
    expect([...removal.edges]).toEqual(['e_out']);
  });

  it('removes a container with everything inside it, transitively, and the edges into its contents', () => {
    const removal = removalFor(base.nodes, base.edges, new Set(['c_box']));
    expect([...removal.nodes].sort()).toEqual(['c_box', 'c_inner', 'n_deep']);
    expect([...removal.edges]).toEqual(['e_in']);
  });

  it('removes a selected edge alone and leaves both its endpoints', () => {
    const removal = removalFor(base.nodes, base.edges, new Set(['e_out']));
    expect([...removal.nodes]).toEqual([]);
    expect([...removal.edges]).toEqual(['e_out']);
  });

  it('ignores ids that name nothing', () => {
    const removal = removalFor(base.nodes, base.edges, new Set(['n_gone', 'e_gone']));
    expect(removal.nodes.size + removal.edges.size).toBe(0);
  });

  it('deletes from the Y.Doc as one local undo step, and undo brings every element back in its place', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    removeElements(doc, new Set(['c_inner', 'e_out']));
    endUndoStep(undo);
    expect(ids(yDocToDocument(doc))).toEqual({ nodes: ['c_box', 'n_free', 'n_other'], edges: [] });
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(base);
  });

  it('deletes a container with its nested contents and their edges, and one undo restores all of it exactly', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    removeElements(doc, new Set(['c_box']));
    endUndoStep(undo);
    expect(ids(yDocToDocument(doc))).toEqual({ nodes: ['n_free', 'n_other'], edges: ['e_out'] });
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(undo.undoStack.length).toBe(0);
    expect(yDocToDocument(doc)).toEqual(base);
  });

  it('deletes nothing and records no undo step for a selection that names nothing', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    removeElements(doc, new Set(['n_gone']));
    expect(undo.undoStack.length).toBe(0);
    expect(yDocToDocument(doc)).toEqual(base);
  });

  it('appends added nodes after every existing one, even when earlier ones were removed', () => {
    const doc = documentToYDoc(base);
    removeElements(doc, new Set(['n_other']));
    const added = { id: 'n_new', kind: 'service', label: 'New', shape: 'rounded', geometry: { x: 1, y: 2, w: 3, h: 4 } };
    addNodes(doc, [added]);
    const after = yDocToDocument(doc);
    expect(after.nodes.map((n) => n.id)).toEqual(['c_box', 'c_inner', 'n_deep', 'n_free', 'n_new']);
    expect(after.nodes[4]).toEqual({ id: 'n_new', kind: 'service', label: 'New', shape: 'rounded', geometry: { x: 1, y: 2, w: 3, h: 4 } });
  });

  it('adds as one local undo step, which undo takes away again', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    addNodes(doc, [{ id: 'n_new', kind: 'service', label: 'New', shape: 'rounded' }]);
    endUndoStep(undo);
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(base);
  });

  it('refuses, writing nothing, to add a node whose id a node or an edge already has', () => {
    const doc = documentToYDoc(base);
    for (const id of ['n_free', 'e_in']) {
      expect(() => {
        addNodes(doc, [
          { id: 'n_fine', kind: 'service', label: 'Fine', shape: 'rounded' },
          { id, kind: 'service', label: 'Clash', shape: 'rounded' },
        ]);
      }).toThrow(id);
      expect(yDocToDocument(doc)).toEqual(base);
    }
  });

  it('appends added nodes and edges after every existing one, as one local undo step', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    addElements(
      doc,
      [
        { id: 'n_p', kind: 'service', label: 'P', shape: 'rounded', geometry: { x: 1, y: 2, w: 3, h: 4 } },
        { id: 'n_q', kind: 'service', label: 'Q', shape: 'rounded', parent: 'n_p' },
      ],
      [
        { id: 'e_pq', kind: 'data', from: { node: 'n_p', port: 'out' }, to: { node: 'n_q' }, routing: { mode: 'orthogonal', waypoints: [{ x: 5, y: 6 }] } },
        // An added edge may also land on a node the document already has.
        { id: 'e_qf', kind: 'data', label: 'to free', from: { node: 'n_q' }, to: { node: 'n_free' } },
      ],
    );
    endUndoStep(undo);
    const after = yDocToDocument(doc);
    expect(ids(after)).toEqual({ nodes: ['c_box', 'c_inner', 'n_deep', 'n_free', 'n_other', 'n_p', 'n_q'], edges: ['e_in', 'e_out', 'e_pq', 'e_qf'] });
    expect(after.nodes[6]).toEqual({ id: 'n_q', kind: 'service', label: 'Q', shape: 'rounded', parent: 'n_p' });
    expect(after.edges[2]).toEqual({ id: 'e_pq', kind: 'data', from: { node: 'n_p', port: 'out' }, to: { node: 'n_q' }, routing: { mode: 'orthogonal', waypoints: [{ x: 5, y: 6 }] } });
    expect(after.edges[3]).toEqual({ id: 'e_qf', kind: 'data', label: 'to free', from: { node: 'n_q' }, to: { node: 'n_free' } });
    expect(undo.undoStack.length).toBe(1);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(base);
  });

  it('refuses, writing nothing, an addition with a taken or repeated id or an edge to a missing node', () => {
    const fine = { id: 'n_fine', kind: 'service', label: 'Fine', shape: 'rounded' };
    const cases: [Parameters<typeof addElements>[1], Parameters<typeof addElements>[2], string][] = [
      [[fine], [{ id: 'e_in', kind: 'data', from: { node: 'n_fine' }, to: { node: 'n_free' } }], 'e_in'],
      [[fine], [{ id: 'n_free', kind: 'data', from: { node: 'n_fine' }, to: { node: 'n_free' } }], 'n_free'],
      [[fine, { ...fine, label: 'Twin' }], [], 'n_fine'],
      [[fine], [{ id: 'e_new', kind: 'data', from: { node: 'n_fine' }, to: { node: 'n_gone' } }], 'n_gone'],
    ];
    for (const [nodes, edges, named] of cases) {
      const doc = documentToYDoc(base);
      expect(() => {
        addElements(doc, nodes, edges);
      }).toThrow(named);
      expect(yDocToDocument(doc)).toEqual(base);
    }
  });
});

describe('setElementOrder', () => {
  const ids = (document: Document) => ({ nodes: document.nodes.map((n) => n.id), edges: document.edges.map((e) => e.id) });

  it('writes an arranged order as one local undo step, which undo takes back', () => {
    const doc = documentToYDoc(parseDocument(orderService));
    const undo = createUndoManager(doc);
    const before = yDocToDocument(doc);

    expect(setElementOrder(doc, arrange(before, ['n_web', 'e_1'], 'bringToFront'))).toBe(true);
    endUndoStep(undo);
    // n_web was the first root node and goes after c_vpc, the only other root; its children keep their slots.
    expect(ids(yDocToDocument(doc))).toEqual({ nodes: ['c_vpc', 'n_web', 'n_api', 'n_db', 'n_cache'], edges: ['e_2', 'e_3', 'e_1'] });
    expect(undo.undoStack).toHaveLength(1);

    undo.undo();
    expect(ids(yDocToDocument(doc))).toEqual(ids(before));
  });

  it('keeps the order through a save and a reload of the saved JSON', () => {
    const doc = documentToYDoc(parseDocument(orderService));
    setElementOrder(doc, arrange(yDocToDocument(doc), ['n_cache'], 'sendToBack'));
    const saved: unknown = JSON.parse(JSON.stringify(yDocToDocument(doc)));
    const reloaded = yDocToDocument(documentToYDoc(parseDocument(saved)));
    expect(ids(reloaded).nodes).toEqual(['n_web', 'c_vpc', 'n_cache', 'n_api', 'n_db']);
  });

  it('writes nothing, and records no undo step, when the order is already in place', () => {
    const doc = documentToYDoc(parseDocument(orderService));
    const undo = createUndoManager(doc);
    const updates = vi.fn();
    doc.on('update', updates);
    expect(setElementOrder(doc, yDocToDocument(doc))).toBe(false);
    expect(updates).not.toHaveBeenCalled();
    expect(undo.undoStack).toHaveLength(0);
  });

  it('orders correctly across the gaps a delete leaves, and a node added afterwards still goes last', () => {
    const doc = documentToYDoc(parseDocument(orderService));
    removeElements(doc, new Set(['n_web']));
    setElementOrder(doc, arrange(yDocToDocument(doc), ['n_db'], 'bringToFront'));
    expect(ids(yDocToDocument(doc)).nodes).toEqual(['c_vpc', 'n_api', 'n_cache', 'n_db']);
    addNodes(doc, [{ id: 'n_new', kind: 'service', label: 'New', shape: 'rectangle' }]);
    expect(ids(yDocToDocument(doc)).nodes).toEqual(['c_vpc', 'n_api', 'n_cache', 'n_db', 'n_new']);
  });
});
