import * as Y from 'yjs';
import { createUndoManager, documentToYDoc, endUndoStep, HEADER_MAP, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';
import { mergeRemote } from './remoteMerge.ts';
import { checkoutSequence, must } from './sequenceFixture.ts';
import { describeTextConflicts, mergeTextEdit, takeMine } from './textMerge.ts';
import type { Document, Id, Node } from './types.ts';

const base: Document = {
  id: 'doc_text',
  schemaVersion: 1,
  title: 'Text',
  type: 'architecture',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 400, y: 0, w: 100, h: 50 } },
  ],
  edges: [{ id: 'e_ab', kind: 'sync-call', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
};

function changed(from: Document, change: (document: Document) => void): Document {
  const copy = structuredClone(from);
  change(copy);
  return copy;
}
const relabel = (document: Document, id: Id, label: string): void => { document.nodes = document.nodes.map((node) => (node.id === id ? { ...node, label } : node)); };
const nodeOf = (document: Document, id: Id): Node => must(document.nodes.find((node) => node.id === id));
/** The diagram as it is by the time the text is applied: `base` plus what a collaborator saved meanwhile. */
function diagramAfter(collaborator: (document: Document) => void): { doc: Y.Doc; undo: Y.UndoManager; now: Document } {
  const doc = documentToYDoc(base);
  const undo = createUndoManager(doc);
  const now = changed(base, collaborator);
  mergeRemote(doc, base, now);
  return { doc, undo, now: yDocToDocument(doc) };
}

describe('applying text edited from an older version of the diagram', () => {
  it('lands the text\'s changes exactly when nobody else changed the diagram', () => {
    const { doc, now } = diagramAfter(() => undefined);
    const mine = changed(now, (document) => { relabel(document, 'n_a', 'Typed'); document.title = 'Typed title'; });
    expect(mergeTextEdit(doc, base, mine)).toEqual({ changed: true, conflicts: [] });
    expect(yDocToDocument(doc)).toEqual(mine);
  });

  it('keeps what a collaborator changed meanwhile and takes what the text changed, positions and all', () => {
    const { doc, now } = diagramAfter((document) => {
      document.nodes = document.nodes.map((node) => (node.id === 'n_b' ? { ...node, geometry: { x: 640, y: 480, w: 100, h: 50 } } : node));
      relabel(document, 'n_c', 'C by collaborator');
      document.meta.updatedAt = '2026-09-28T10:00:00.000Z';
    });
    const mine = changed(base, (document) => {
      relabel(document, 'n_a', 'A typed');
      document.nodes.push({ id: 'n_new', kind: 'service', label: 'Typed node', shape: 'rectangle' });
    });
    const result = mergeTextEdit(doc, base, mine);
    expect(result).toEqual({ changed: true, conflicts: [] });
    const merged = yDocToDocument(doc);
    expect(nodeOf(merged, 'n_a').label).toBe('A typed');
    expect(nodeOf(merged, 'n_b').geometry).toEqual({ x: 640, y: 480, w: 100, h: 50 });
    expect(nodeOf(merged, 'n_c').label).toBe('C by collaborator');
    expect(nodeOf(merged, 'n_new').label).toBe('Typed node');
    expect(merged.meta).toEqual(now.meta);
  });

  it('never takes meta from text, only from the diagram', () => {
    const { doc, now } = diagramAfter(() => undefined);
    const mine = changed(base, (document) => { document.meta = { ...document.meta, updatedAt: '2030-01-01T00:00:00.000Z', createdBy: 'someone typed this' }; relabel(document, 'n_a', 'A typed'); });
    mergeTextEdit(doc, base, mine);
    expect(yDocToDocument(doc).meta).toEqual(now.meta);
  });

  it('is one undo step that gives back the diagram as it was, collaborator\'s change included', () => {
    const { doc, undo, now } = diagramAfter((document) => { relabel(document, 'n_c', 'C by collaborator'); });
    expect(undo.undoStack).toHaveLength(0);
    mergeTextEdit(doc, base, changed(base, (document) => { relabel(document, 'n_a', 'A typed'); document.nodes.pop(); document.title = 'T'; }));
    endUndoStep(undo);
    expect(undo.undoStack).toHaveLength(1);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(now);
    undo.redo();
    expect(nodeOf(yDocToDocument(doc), 'n_a').label).toBe('A typed');
  });

  it('keeps the diagram\'s side where both changed the same thing, says so, and takes the text\'s on request', () => {
    const { doc, undo } = diagramAfter((document) => { relabel(document, 'n_a', 'A by collaborator'); });
    const mine = changed(base, (document) => { relabel(document, 'n_a', 'A typed'); relabel(document, 'n_b', 'B typed'); });
    const { conflicts } = mergeTextEdit(doc, base, mine);
    expect(conflicts).toEqual([{ kind: 'node', id: 'n_a', reason: 'both-changed', fields: ['label'] }]);
    expect(nodeOf(yDocToDocument(doc), 'n_a').label).toBe('A by collaborator');
    expect(nodeOf(yDocToDocument(doc), 'n_b').label).toBe('B typed');
    expect(describeTextConflicts(conflicts, yDocToDocument(doc))).toEqual(['“A by collaborator” (n_a): label']);
    endUndoStep(undo);
    takeMine(doc, mine, conflicts);
    endUndoStep(undo);
    expect(nodeOf(yDocToDocument(doc), 'n_a').label).toBe('A typed');
    expect(undo.undoStack).toHaveLength(2);
  });

  it('does not delete what a collaborator edited, nor revive what they deleted, without saying so', () => {
    const { doc } = diagramAfter((document) => {
      relabel(document, 'n_b', 'B edited by collaborator');
      document.nodes = document.nodes.filter((node) => node.id !== 'n_c');
    });
    const mine = changed(base, (document) => {
      document.nodes = document.nodes.filter((node) => node.id !== 'n_b');
      document.edges = [];
      relabel(document, 'n_c', 'C typed');
    });
    const { conflicts } = mergeTextEdit(doc, base, mine);
    expect(conflicts).toEqual(expect.arrayContaining([
      { kind: 'node', id: 'n_b', reason: 'deleted-remotely', fields: [] },
      { kind: 'node', id: 'n_c', reason: 'deleted-locally', fields: [] },
    ]));
    expect(yDocToDocument(doc).nodes.map((node) => node.id)).toEqual(['n_a', 'n_b']);
    expect(describeTextConflicts(conflicts, yDocToDocument(doc))).toEqual(expect.arrayContaining([
      '“B edited by collaborator” (n_b): your text removed it, but it was changed on the diagram',
      'node n_c: it was removed from the diagram, but your text changed it',
    ]));
  });

  it('carries a change of diagram type, and reports one both sides made', () => {
    const quiet = diagramAfter(() => undefined);
    mergeTextEdit(quiet.doc, base, changed(base, (document) => { document.type = 'flowchart'; }));
    expect(yDocToDocument(quiet.doc).type).toBe('flowchart');

    // The live-sync merge does not carry a retype, so the diagram's own type is changed directly here (the canvas can).
    const clash = diagramAfter(() => undefined);
    clash.doc.getMap<unknown>(HEADER_MAP).set('type', 'freeform');
    const mine = changed(base, (document) => { document.type = 'flowchart'; });
    const { conflicts } = mergeTextEdit(clash.doc, base, mine);
    expect(conflicts).toEqual([{ kind: 'document', id: base.id, reason: 'both-changed', fields: ['type'] }]);
    expect(yDocToDocument(clash.doc).type).toBe('freeform');
    expect(describeTextConflicts(conflicts, yDocToDocument(clash.doc))).toEqual(['the diagram type']);
    takeMine(clash.doc, mine, conflicts);
    expect(yDocToDocument(clash.doc).type).toBe('flowchart');
  });

  it.each([
    ['text that names another diagram', (document: Document) => { document.id = 'doc_other'; }],
    ['two elements with one id', (document: Document) => { document.nodes.push({ ...must(document.nodes[0]) }); }],
    ['a node the schema refuses', (document: Document) => { document.nodes.push({ id: 'bad id', kind: 'service', label: 'x', shape: 'rectangle' }); }],
  ])('refuses %s and writes nothing', (_name, damage) => {
    const { doc, now } = diagramAfter(() => undefined);
    const updates: Uint8Array[] = [];
    doc.on('update', (update: Uint8Array) => updates.push(update));
    expect(() => mergeTextEdit(doc, base, changed(base, damage))).toThrow();
    expect(updates).toEqual([]);
    expect(yDocToDocument(doc)).toEqual(now);
  });

  it('puts a message the text inserted where it was put, in a sequence diagram a collaborator also changed', () => {
    const sequence = checkoutSequence();
    const doc = documentToYDoc(sequence);
    const theirs = changed(sequence, (document) => {
      document.edges.push({ ...must(document.edges[0]), id: 'm_tail', label: 'Added at the end by a collaborator' });
    });
    mergeRemote(doc, sequence, theirs);
    const mine = changed(sequence, (document) => {
      document.edges.splice(2, 0, { ...must(document.edges[0]), id: 'm_typed', label: 'Typed between two messages' });
    });
    expect(mergeTextEdit(doc, sequence, mine).conflicts).toEqual([]);
    const order = yDocToDocument(doc).edges.map((edge) => edge.id);
    expect(order).toEqual([...sequence.edges.slice(0, 2).map((edge) => edge.id), 'm_typed', ...sequence.edges.slice(2).map((edge) => edge.id), 'm_tail']);
  });

  it('applies to a document read back from JSON as well as to one built here', () => {
    const doc = documentToYDoc(parseDocument(JSON.parse(JSON.stringify(base))));
    const mine = changed(base, (document) => { relabel(document, 'n_a', 'Typed'); });
    expect(mergeTextEdit(doc, base, mine).conflicts).toEqual([]);
    expect(nodeOf(yDocToDocument(doc), 'n_a').label).toBe('Typed');
  });
});
