import * as Y from 'yjs';
import example from '../../../docs/examples/order-service.json';
import { applyDocumentDiff, diffDocument } from './documentDiff.ts';
import { createUndoManager, documentToYDoc, endUndoStep, LOCAL_ORIGIN, NODES_MAP, setNodeLabel, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';

function required<T>(value: T | undefined): T { if (value === undefined) throw new Error('Missing test element'); return value; }
const original = parseDocument(example);

describe('DSL document diff', () => {
  it('updates surviving maps, adds/removes/reorders elements and optional fields as one local undo step', () => {
    const doc = documentToYDoc(original);
    const undo = createUndoManager(doc);
    const map = doc.getMap<Y.Map<unknown>>(NODES_MAP).get('n_api');
    const next = structuredClone(original);
    next.title = 'Edited in Text';
    next.theme = { 'node.fill': 'red' };
    next.meta.createdBy = 'editor';
    const api = required(next.nodes.find((node) => node.id === 'n_api'));
    api.label = 'New API';
    delete api.parent;
    delete api.ports;
    api.geometry = { x: -12, y: 27, w: 180, h: 90, rotation: 33 };
    api.props = { nullable: null, object: { nested: [true, 2] } };
    api.style = { fill: 'blue' };
    next.nodes = next.nodes.filter((node) => node.id !== 'n_web').reverse();
    next.nodes.splice(1, 0, { id: 'new', kind: 'note', label: 'New', shape: 'rectangle' });
    next.edges = next.edges.filter((edge) => edge.id !== 'e_1');
    required(next.edges[0]).label = 'New edge label';
    delete required(next.edges[0]).routing;
    next.edges.push({ id: 'added', kind: 'association', from: { node: 'new' }, to: { node: 'n_api' }, props: { value: null } });
    const origins: unknown[] = [];
    doc.on('afterTransaction', (transaction: Y.Transaction) => { origins.push(transaction.origin); });

    expect(applyDocumentDiff(doc, next, original)).toBe(true);
    expect(yDocToDocument(doc)).toEqual(next);
    expect(doc.getMap<Y.Map<unknown>>(NODES_MAP).get('n_api')).toBe(map);
    expect(origins).toEqual([LOCAL_ORIGIN]);
    expect(undo.undoStack).toHaveLength(1);
    endUndoStep(undo);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(original);
    expect(undo.undoStack).toHaveLength(0);
    expect(undo.redoStack).toHaveLength(1);
    undo.redo();
    expect(yDocToDocument(doc)).toEqual(next);
    expect(undo.undoStack).toHaveLength(1);
  });

  it('emits PatchApplier.cs op names and shapes for element edits', () => {
    const next = structuredClone(original);
    const api = required(next.nodes.find((node) => node.id === 'n_api'));
    api.label = 'Renamed';
    api.style = { ...api.style, fill: 'blue' };
    api.props = { ...api.props, owner: 'team' };
    const edge = required(next.edges[0]);
    edge.label = 'Edge renamed';
    next.edges = next.edges.filter((element) => element.id !== required(original.edges[1]).id);
    const ops = diffDocument(original, next);
    expect(ops).toEqual(expect.arrayContaining([
      { op: 'updateNode', id: 'n_api', set: { label: 'Renamed' } },
      { op: 'restyle', target: 'node', id: 'n_api', style: { fill: 'blue' } },
      { op: 'setProp', target: 'node', id: 'n_api', key: 'owner', value: 'team' },
      { op: 'updateEdge', id: edge.id, set: { label: 'Edge renamed' } },
      { op: 'removeEdge', id: required(original.edges[1]).id },
    ]));
  });

  it('unchanged JSON, including reordered object keys, writes nothing', () => {
    const doc = documentToYDoc(original);
    const undo = createUndoManager(doc);
    const next = { ...original, meta: { createdBy: original.meta.createdBy, updatedAt: original.meta.updatedAt, createdAt: original.meta.createdAt, aiProvenance: required(original.meta.aiProvenance) } };
    expect(diffDocument(original, next)).toEqual([]);
    expect(applyDocumentDiff(doc, next, original)).toBe(false);
    expect(undo.undoStack).toHaveLength(0);
    expect(yDocToDocument(doc)).toEqual(original);
  });

  it('rejects stale drafts, duplicate ids and identity changes before any writes', () => {
    const doc = documentToYDoc(original);
    const undo = createUndoManager(doc);
    const next = structuredClone(original);
    next.title = 'Must not land';
    next.nodes.push({ ...required(next.nodes[0]) });
    expect(() => applyDocumentDiff(doc, next, original)).toThrow('unique id');
    expect(() => applyDocumentDiff(doc, { ...original, id: 'different' }, original)).toThrow('document id');
    expect(() => applyDocumentDiff(doc, { ...original, schemaVersion: 2 } as unknown as typeof original, original)).toThrow('/schemaVersion');
    expect(yDocToDocument(doc)).toEqual(original);
    expect(undo.undoStack).toHaveLength(0);
    setNodeLabel(doc, 'n_api', 'Canvas edit');
    endUndoStep(undo);
    expect(() => applyDocumentDiff(doc, original, original)).toThrow('canvas changed');
    expect(yDocToDocument(doc).nodes.find((node) => node.id === 'n_api')?.label).toBe('Canvas edit');
    expect(undo.undoStack).toHaveLength(1);
  });
});
