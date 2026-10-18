import * as Y from 'yjs';
import orderService from '../../../docs/examples/order-service.json';
import {
  addNodes,
  createUndoManager,
  documentToYDoc,
  endUndoStep,
  removeElements,
  setElementOrder,
  setNodeGeometries,
  setNodeLabel,
  yDocToDocument,
} from './editor.ts';
import { parseDocument } from './parse.ts';
import { insertEdgeAt } from './sequenceEdit.ts';
import { must } from './sequenceFixture.ts';
import { jsonEqual, mergeRemote, takeRemote } from './remoteMerge.ts';
import type { Document, Edge, Geometry, Id, Node } from './types.ts';

const base: Document = {
  id: 'doc_merge',
  schemaVersion: 1,
  title: 'Merge',
  type: 'architecture',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 400, y: 0, w: 100, h: 50 } },
  ],
  edges: [{ id: 'e_ab', kind: 'sync-call', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};

/** The document an agent's patch_document stores: base with a transform applied, and the server's new updatedAt. */
function agentWrite(change: (document: Document) => void): Document {
  const document = structuredClone(base);
  change(document);
  document.meta.updatedAt = '2026-09-28T10:00:00.000Z';
  return document;
}

const node = (document: Document, id: Id): Node | undefined => document.nodes.find((n) => n.id === id);
function nodeOf(document: Document, id: Id): Node {
  const found = node(document, id);
  if (found === undefined) {
    throw new Error(`no node ${id}`);
  }
  return found;
}
function relabel(document: Document, id: Id, label: string): void {
  document.nodes = document.nodes.map((n) => (n.id === id ? { ...n, label } : n));
}
const moved: Geometry = { x: 7, y: 7, w: 100, h: 50 };

describe('mergeRemote', () => {
  it('lands the other writer’s document exactly when there are no local edits', () => {
    const example = parseDocument(orderService);
    const doc = documentToYDoc(example);
    const remote = structuredClone(example);
    remote.title = 'Order service, amended';
    relabel(remote, 'n_web', 'Web (agent)');
    remote.nodes.splice(1, 1);
    remote.edges = remote.edges.filter((edge) => edge.from.node !== example.nodes[1]?.id && edge.to.node !== example.nodes[1]?.id);
    remote.nodes.push({ id: 'n_agent', kind: 'service', label: 'Added by agent', shape: 'rectangle', geometry: { x: 900, y: 900, w: 100, h: 50 } });
    remote.meta.updatedAt = '2026-09-28T10:00:00.000Z';

    const result = mergeRemote(doc, example, remote);

    expect(result).toEqual({ changed: true, conflicts: [] });
    // Byte-identical, so the next save sends exactly what the server holds plus nothing.
    expect(JSON.stringify(yDocToDocument(doc))).toBe(JSON.stringify(remote));
  });

  it('writes nothing and reports nothing when the stored version has the same content', () => {
    const doc = documentToYDoc(base);
    const updates: Uint8Array[] = [];
    doc.on('update', (update: Uint8Array) => updates.push(update));
    expect(mergeRemote(doc, base, structuredClone(base))).toEqual({ changed: false, conflicts: [] });
    expect(updates).toEqual([]);
  });

  it('takes a field only the other writer changed and keeps a field only the local user changed, on the same node', () => {
    const doc = documentToYDoc(base);
    setNodeGeometries(doc, new Map([['n_a', moved]]));
    const remote = agentWrite((d) => { relabel(d, 'n_a', 'A (agent)'); });

    const { conflicts } = mergeRemote(doc, base, remote);

    const merged = yDocToDocument(doc);
    expect(node(merged, 'n_a')).toMatchObject({ label: 'A (agent)', geometry: moved });
    expect(merged.meta.updatedAt).toBe('2026-09-28T10:00:00.000Z');
    expect(conflicts).toEqual([]);
  });

  it('keeps the local value of a field both sides changed and reports it, and reports nothing when they agree', () => {
    const doc = documentToYDoc(base);
    setNodeLabel(doc, 'n_a', 'A (mine)');
    setNodeLabel(doc, 'n_b', 'B (same)');
    const remote = agentWrite((d) => {
      relabel(d, 'n_a', 'A (agent)');
      relabel(d, 'n_b', 'B (same)');
    });

    const { conflicts } = mergeRemote(doc, base, remote);

    expect(node(yDocToDocument(doc), 'n_a')?.label).toBe('A (mine)');
    expect(node(yDocToDocument(doc), 'n_b')?.label).toBe('B (same)');
    expect(conflicts).toEqual([{ kind: 'node', id: 'n_a', reason: 'both-changed', fields: ['label'] }]);
  });

  it('adds what the other writer added after every local element, keeping what the local user added', () => {
    const doc = documentToYDoc(base);
    addNodes(doc, [{ id: 'n_mine', kind: 'service', label: 'Mine', shape: 'rectangle' }]);
    const added: Node = { id: 'n_agent', kind: 'service', label: 'Agent', shape: 'rectangle', geometry: { x: 0, y: 200, w: 100, h: 50 } };
    const edge: Edge = { id: 'e_agent', kind: 'sync-call', from: { node: 'n_c' }, to: { node: 'n_agent' } };
    const remote = agentWrite((d) => {
      d.nodes.push(added);
      d.edges.push(edge);
    });

    expect(mergeRemote(doc, base, remote).conflicts).toEqual([]);

    const merged = yDocToDocument(doc);
    expect(merged.nodes.map((n) => n.id)).toEqual(['n_a', 'n_b', 'n_c', 'n_mine', 'n_agent']);
    expect(node(merged, 'n_agent')).toEqual(added);
    expect(merged.edges.map((e) => e.id)).toEqual(['e_ab', 'e_agent']);
  });

  it('removes what the other writer removed only when the local user left it alone', () => {
    const doc = documentToYDoc(base);
    setNodeLabel(doc, 'n_b', 'B (mine)');
    const remote = agentWrite((d) => {
      d.nodes = d.nodes.filter((n) => n.id === 'n_a');
      d.edges = [];
    });

    const { conflicts } = mergeRemote(doc, base, remote);

    const merged = yDocToDocument(doc);
    // n_c was untouched here, so it goes; n_b was edited here, so it stays and the clash is reported.
    expect(merged.nodes.map((n) => n.id)).toEqual(['n_a', 'n_b']);
    expect(merged.edges).toEqual([]);
    expect(conflicts).toEqual([{ kind: 'node', id: 'n_b', reason: 'deleted-remotely', fields: [] }]);
  });

  it('keeps a node the other writer removed while a local edge still points at it', () => {
    const doc = documentToYDoc(base);
    doc.transact(() => {
      doc.getMap<Y.Map<unknown>>('edges').set('e_mine', new Y.Map<unknown>([['id', 'e_mine'], ['kind', 'sync-call'], ['from', { node: 'n_a' }], ['to', { node: 'n_c' }], ['order', 5]]));
    });
    const remote = agentWrite((d) => { d.nodes = d.nodes.filter((n) => n.id !== 'n_c'); });

    const { conflicts } = mergeRemote(doc, base, remote);

    expect(yDocToDocument(doc).nodes.map((n) => n.id)).toEqual(['n_a', 'n_b', 'n_c']);
    expect(conflicts).toEqual([{ kind: 'node', id: 'n_c', reason: 'deleted-remotely', fields: [] }]);
  });

  it('keeps a local removal, reporting it only when the other writer edited what was removed', () => {
    const doc = documentToYDoc(base);
    removeElements(doc, new Set(['n_b', 'n_c']));
    const remote = agentWrite((d) => { relabel(d, 'n_b', 'B (agent)'); });

    const { conflicts } = mergeRemote(doc, base, remote);

    expect(yDocToDocument(doc).nodes.map((n) => n.id)).toEqual(['n_a']);
    expect(conflicts).toEqual([{ kind: 'node', id: 'n_b', reason: 'deleted-locally', fields: [] }]);
  });

  it('follows the other writer’s reorder unless the local user reordered too', () => {
    const reordered = agentWrite((d) => { d.nodes.reverse(); });

    const untouched = documentToYDoc(base);
    expect(mergeRemote(untouched, base, reordered).conflicts).toEqual([]);
    expect(yDocToDocument(untouched).nodes.map((n) => n.id)).toEqual(['n_c', 'n_b', 'n_a']);

    const local = documentToYDoc(base);
    setElementOrder(local, { nodes: ['n_b', 'n_a', 'n_c'].map((id) => nodeOf(base, id)), edges: base.edges });
    expect(mergeRemote(local, base, reordered).conflicts).toEqual([{ kind: 'document', id: 'doc_merge', reason: 'order', fields: [] }]);
    expect(yDocToDocument(local).nodes.map((n) => n.id)).toEqual(['n_b', 'n_a', 'n_c']);
  });

  describe('a sequence diagram, where the edge order is the time order', () => {
    const message = (id: string, from: Id, to: Id): Edge => ({ id, kind: 'sync', from: { node: from }, to: { node: to } });
    const sequenceBase: Document = {
      ...base,
      type: 'sequence',
      nodes: base.nodes.map((n) => ({ id: n.id, kind: n.kind, label: n.label, shape: n.shape })),
      edges: [message('m_1', 'n_a', 'n_b'), message('m_2', 'n_b', 'n_c'), message('m_3', 'n_c', 'n_a')],
    };
    const agentSequence = (change: (document: Document) => void): Document => {
      const document = structuredClone(sequenceBase);
      change(document);
      document.meta.updatedAt = '2026-09-28T10:00:00.000Z';
      return document;
    };
    const edgeOrder = (doc: Y.Doc): Id[] => yDocToDocument(doc).edges.map((edge) => edge.id);

    it('follows an agent’s moveEdge, a pure reorder, as a change', () => {
      const doc = documentToYDoc(sequenceBase);
      const moved = agentSequence((d) => { d.edges = [must(d.edges[2]), must(d.edges[0]), must(d.edges[1])]; });
      expect(mergeRemote(doc, sequenceBase, moved)).toEqual({ changed: true, conflicts: [] });
      expect(edgeOrder(doc)).toEqual(['m_3', 'm_1', 'm_2']);
      expect(JSON.stringify(yDocToDocument(doc))).toBe(JSON.stringify(moved));
    });

    it('puts an agent’s mid-stream insert where the agent put it, not at the end', () => {
      const doc = documentToYDoc(sequenceBase);
      const inserted = agentSequence((d) => { d.edges.splice(1, 0, message('m_new', 'n_a', 'n_c')); });
      expect(mergeRemote(doc, sequenceBase, inserted)).toEqual({ changed: true, conflicts: [] });
      expect(edgeOrder(doc)).toEqual(['m_1', 'm_new', 'm_2', 'm_3']);
      expect(JSON.stringify(yDocToDocument(doc))).toBe(JSON.stringify(inserted));
    });

    it('keeps the local order, and reports it, when both sides reordered differently', () => {
      const doc = documentToYDoc(sequenceBase);
      setElementOrder(doc, { nodes: sequenceBase.nodes, edges: ['m_2', 'm_1', 'm_3'].map((id) => must(sequenceBase.edges.find((edge) => edge.id === id))) });
      const moved = agentSequence((d) => { d.edges = [must(d.edges[2]), must(d.edges[0]), must(d.edges[1])]; });
      expect(mergeRemote(doc, sequenceBase, moved).conflicts).toEqual([{ kind: 'document', id: 'doc_merge', reason: 'order', fields: [] }]);
      expect(edgeOrder(doc)).toEqual(['m_2', 'm_1', 'm_3']);
    });

    it('keeps the local order when only the agent inserted and the user had reordered', () => {
      const doc = documentToYDoc(sequenceBase);
      setElementOrder(doc, { nodes: sequenceBase.nodes, edges: ['m_2', 'm_1', 'm_3'].map((id) => must(sequenceBase.edges.find((edge) => edge.id === id))) });
      const inserted = agentSequence((d) => { d.edges.splice(1, 0, message('m_new', 'n_a', 'n_c')); });
      mergeRemote(doc, sequenceBase, inserted);
      // The user's own reorder stands, and the agent's message is in, after everything local.
      expect(edgeOrder(doc)).toEqual(['m_2', 'm_1', 'm_3', 'm_new']);
    });

    describe('two writers inserting at once', () => {
      // The editor's own insert (insertEdgeAt) on this client, another client's saved insert as `theirs`.
      const local = (index: number, id: string): Y.Doc => {
        const doc = documentToYDoc(sequenceBase);
        insertEdgeAt(doc, message(id, 'n_a', 'n_c'), index);
        return doc;
      };
      const theirs = (index: number, id: string): Document => agentSequence((d) => { d.edges.splice(index, 0, message(id, 'n_c', 'n_b')); });

      it('keeps both messages, each at the slot its writer chose, when the slots differ', () => {
        const doc = local(1, 'm_mine');
        expect(mergeRemote(doc, sequenceBase, theirs(2, 'm_theirs'))).toEqual({ changed: true, conflicts: [] });
        expect(edgeOrder(doc)).toEqual(['m_1', 'm_mine', 'm_2', 'm_theirs', 'm_3']);
      });

      it('keeps both, mine first, when they chose the same slot, and the other client converges on the same order', () => {
        const mine = local(1, 'm_mine');
        expect(mergeRemote(mine, sequenceBase, theirs(1, 'm_theirs')).conflicts).toEqual([]);
        expect(edgeOrder(mine)).toEqual(['m_1', 'm_mine', 'm_theirs', 'm_2', 'm_3']);
        // The other client, having saved its own insert, later merges the result the first one saved.
        const other = documentToYDoc(theirs(1, 'm_theirs'));
        const saved = yDocToDocument(mine);
        expect(mergeRemote(other, theirs(1, 'm_theirs'), saved).conflicts).toEqual([]);
        expect(edgeOrder(other)).toEqual(edgeOrder(mine));
      });

      it('handles one insert at the head and the other at the tail', () => {
        const doc = local(0, 'm_mine');
        mergeRemote(doc, sequenceBase, theirs(3, 'm_theirs'));
        expect(edgeOrder(doc)).toEqual(['m_mine', 'm_1', 'm_2', 'm_3', 'm_theirs']);
      });

      it('leaves the order numbers contiguous, so a later insert at an index lands exactly there', () => {
        const doc = local(1, 'm_mine');
        mergeRemote(doc, sequenceBase, theirs(2, 'm_theirs'));
        insertEdgeAt(doc, message('m_next', 'n_b', 'n_a'), 2);
        expect(edgeOrder(doc)).toEqual(['m_1', 'm_mine', 'm_next', 'm_2', 'm_theirs', 'm_3']);
      });
    });
  });

  it('merges header fields the same way: remote-only taken, both-changed kept and reported', () => {
    const remoteTitle = documentToYDoc(base);
    mergeRemote(remoteTitle, base, agentWrite((d) => { d.title = 'Agent title'; }));
    expect(yDocToDocument(remoteTitle).title).toBe('Agent title');

    const both = documentToYDoc(base);
    both.transact(() => { both.getMap('header').set('title', 'My title'); });
    const { conflicts } = mergeRemote(both, base, agentWrite((d) => { d.title = 'Agent title'; }));
    expect(yDocToDocument(both).title).toBe('My title');
    expect(conflicts).toEqual([{ kind: 'document', id: 'doc_merge', reason: 'both-changed', fields: ['title'] }]);
  });

  it('is never undone by the local undo manager, which still undoes the local edit made before it', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    setNodeGeometries(doc, new Map([['n_a', moved]]));
    endUndoStep(undo);
    mergeRemote(doc, base, agentWrite((d) => { relabel(d, 'n_b', 'B (agent)'); }));

    undo.undo();

    const merged = yDocToDocument(doc);
    expect(node(merged, 'n_a')?.geometry).toEqual(base.nodes[0]?.geometry);
    expect(node(merged, 'n_b')?.label).toBe('B (agent)');
    expect(undo.canUndo()).toBe(false);
  });
});

describe('takeRemote', () => {
  it('applies the other writer’s side of every conflict as one undo step', () => {
    const doc = documentToYDoc(base);
    const undo = createUndoManager(doc);
    setNodeLabel(doc, 'n_a', 'A (mine)');
    setNodeLabel(doc, 'n_b', 'B (mine)');
    removeElements(doc, new Set(['n_c']));
    endUndoStep(undo);
    const remote = agentWrite((d) => {
      relabel(d, 'n_a', 'A (agent)');
      relabel(d, 'n_c', 'C (agent)');
      d.nodes.splice(1, 1);
      d.edges = [];
    });
    const { conflicts } = mergeRemote(doc, base, remote);
    expect(conflicts.map((c) => `${c.id}:${c.reason}`).sort()).toEqual(['n_a:both-changed', 'n_b:deleted-remotely', 'n_c:deleted-locally']);
    const beforeTaking = JSON.stringify(yDocToDocument(doc));

    takeRemote(doc, remote, conflicts);
    endUndoStep(undo);

    const taken = yDocToDocument(doc);
    expect(taken.nodes.map((n) => [n.id, n.label])).toEqual([['n_a', 'A (agent)'], ['n_c', 'C (agent)']]);
    expect(jsonEqual(taken.nodes, remote.nodes)).toBe(true);
    undo.undo();
    expect(JSON.stringify(yDocToDocument(doc))).toBe(beforeTaking);
  });
});

describe('jsonEqual', () => {
  it('ignores member order and undefined members, and nothing else', () => {
    expect(jsonEqual({ x: 1, y: [1, { a: 2 }] }, { y: [1, { a: 2 }], x: 1 })).toBe(true);
    expect(jsonEqual({ x: 1, z: undefined }, { x: 1 })).toBe(true);
    expect(jsonEqual({ x: 1 }, { x: 2 })).toBe(false);
    expect(jsonEqual([1, 2], [2, 1])).toBe(false);
    expect(jsonEqual({ x: 1 }, { x: 1, y: 0 })).toBe(false);
    expect(jsonEqual(null, undefined)).toBe(false);
  });
});
