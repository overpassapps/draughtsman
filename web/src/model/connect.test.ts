import { connect, DEFAULT_PORTS, NEW_EDGE_KIND, reconnect, type Connection } from './connect.ts';
import { createUndoManager, documentToYDoc, endUndoStep, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

const source: Document = parseDocument({
  id: 'doc_connect',
  schemaVersion: 1,
  title: 'Connect',
  type: 'architecture',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 }, ports: [{ id: 'in', x: 0, y: 0.5 }] },
  ],
  edges: [{ id: 'e_1', kind: 'data', from: { node: 'n_b' }, to: { node: 'n_a' } }],
  meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
});

const right = { id: 'right', x: 1, y: 0.5 };
const toB: Connection = { from: { node: 'n_a', port: right }, to: { node: 'n_b', port: { id: 'in', x: 0, y: 0.5 } } };

describe('connect', () => {
  it.each([['document', 'bottom', 0.85], ['manual-input', 'top', 0.125]] as const)('declares %s outline ports with its edge in exactly one undo step', (shape, portId, y) => {
    const shaped = { ...source, nodes: source.nodes.map((node) => node.id === 'n_a' ? { ...node, shape } : node) };
    const doc = documentToYDoc(shaped);
    const undo = createUndoManager(doc);
    connect(doc, { from: { node: 'n_a', port: { id: portId, x: 0.5, y } }, to: toB.to }, () => 'e_shape');
    endUndoStep(undo);
    expect(undo.undoStack).toHaveLength(1);
    const after = yDocToDocument(doc);
    expect(after.nodes[0]?.ports?.find((port) => port.id === portId)).toEqual({ id: portId, x: 0.5, y });
    expect(after.edges.map((edge) => edge.id)).toEqual(['e_1', 'e_shape']);
    undo.undo();
    expect(yDocToDocument(doc).nodes[0]?.ports).toBeUndefined();
    expect(yDocToDocument(doc).edges.map((edge) => edge.id)).toEqual(['e_1']);
    undo.redo();
    expect(yDocToDocument(doc).nodes[0]?.ports?.find((port) => port.id === portId)?.y).toBe(y);
    expect(yDocToDocument(doc).edges.map((edge) => edge.id)).toEqual(['e_1', 'e_shape']);
  });

  it('adds an orthogonal flow edge between the two ports, after the existing edges', () => {
    const doc = documentToYDoc(source);
    const id = connect(doc, toB, () => 'e_new');
    const after = yDocToDocument(doc);
    expect(id).toBe('e_new');
    expect(after.edges.map((edge) => edge.id)).toEqual(['e_1', 'e_new']);
    expect(after.edges[1]).toEqual({
      id: 'e_new',
      kind: NEW_EDGE_KIND,
      from: { node: 'n_a', port: 'right' },
      to: { node: 'n_b', port: 'in' },
      routing: { mode: 'orthogonal' },
    });
  });

  it('declares the default ports on a node that had none, and leaves a node with its own ports alone', () => {
    const doc = documentToYDoc(source);
    connect(doc, toB, () => 'e_new');
    const after = yDocToDocument(doc);
    expect(after.nodes.find((node) => node.id === 'n_a')?.ports).toEqual(DEFAULT_PORTS);
    expect(after.nodes.find((node) => node.id === 'n_b')?.ports).toEqual([{ id: 'in', x: 0, y: 0.5 }]);
  });

  it('never reuses an id a node or edge already has', () => {
    const doc = documentToYDoc(source);
    const proposals = ['n_a', 'e_1', 'e_free'];
    const id = connect(doc, toB, () => proposals.shift() ?? 'e_exhausted');
    expect(id).toBe('e_free');
    expect(yDocToDocument(doc).edges.map((edge) => edge.id)).toEqual(['e_1', 'e_free']);
  });

  it('is one undo step: undo removes the edge and the ports it declared, redo brings both back', () => {
    const doc = documentToYDoc(source);
    const undo = createUndoManager(doc);
    connect(doc, toB, () => 'e_new');
    endUndoStep(undo);
    const connected = yDocToDocument(doc);

    undo.undo();
    expect(yDocToDocument(doc)).toEqual(source);
    undo.redo();
    expect(yDocToDocument(doc)).toEqual(connected);
  });

  it('writes nothing when an end names a port its node does not have', () => {
    const doc = documentToYDoc(source);
    expect(() => connect(doc, { from: toB.from, to: { node: 'n_b', port: right } }, () => 'e_new')).toThrow(/does not declare/);
    expect(() => connect(doc, { from: { node: 'n_a', port: { id: 'side', x: 1, y: 0.2 } }, to: toB.to }, () => 'e_new')).toThrow(/not a default one/);
    expect(() => connect(doc, { from: toB.from, to: { node: 'n_gone', port: right } }, () => 'e_new')).toThrow(/not in the document/);
    expect(yDocToDocument(doc)).toEqual(source);
  });
});

describe('reconnect', () => {
  const top = { id: 'top', x: 0.5, y: 0 };

  it('moves only the named end onto the new port and leaves everything else about the edge alone', () => {
    const doc = documentToYDoc(source);
    expect(reconnect(doc, { edge: 'e_1', end: 'to', to: { node: 'n_a', port: top } })).toBe(true);
    const after = yDocToDocument(doc);
    expect(after.edges).toEqual([{ id: 'e_1', kind: 'data', from: { node: 'n_b' }, to: { node: 'n_a', port: 'top' } }]);
    // The portless node now declares the default set, as a connect to it would.
    expect(after.nodes.find((node) => node.id === 'n_a')?.ports).toEqual(DEFAULT_PORTS);
  });

  it.each([['document', 'bottom', 0.85], ['manual-input', 'top', 0.125]] as const)('declares %s outline ports, as connect does, in exactly one undo step', (shape, portId, y) => {
    const shaped = { ...source, nodes: source.nodes.map((node) => node.id === 'n_a' ? { ...node, shape } : node) };
    const doc = documentToYDoc(shaped);
    const undo = createUndoManager(doc);
    expect(reconnect(doc, { edge: 'e_1', end: 'to', to: { node: 'n_a', port: { id: portId, x: 0.5, y } } })).toBe(true);
    endUndoStep(undo);
    expect(undo.undoStack).toHaveLength(1);
    const after = yDocToDocument(doc);
    expect(after.nodes[0]?.ports?.find((port) => port.id === portId)).toEqual({ id: portId, x: 0.5, y });
    expect(after.edges[0]?.to).toEqual({ node: 'n_a', port: portId });
    undo.undo();
    expect(yDocToDocument(doc).nodes[0]?.ports).toBeUndefined();
  });

  it('moves the tail when asked for `from`, to another port of the same node', () => {
    const doc = documentToYDoc(source);
    connect(doc, toB, () => 'e_new');
    expect(reconnect(doc, { edge: 'e_new', end: 'from', to: { node: 'n_a', port: top } })).toBe(true);
    const moved = yDocToDocument(doc).edges.find((edge) => edge.id === 'e_new');
    expect(moved?.from).toEqual({ node: 'n_a', port: 'top' });
    expect(moved?.to).toEqual({ node: 'n_b', port: 'in' });
    expect(moved?.routing).toEqual({ mode: 'orthogonal' });
  });

  it('writes nothing, and records no step, when the end is already on that port', () => {
    const doc = documentToYDoc(source);
    connect(doc, toB, () => 'e_new');
    const before = yDocToDocument(doc);
    const undo = createUndoManager(doc);
    expect(reconnect(doc, { edge: 'e_new', end: 'to', to: toB.to })).toBe(false);
    expect(yDocToDocument(doc)).toEqual(before);
    expect(undo.undoStack).toHaveLength(0);
  });

  it('is one undo step: undo puts the end and the node\'s ports back, redo re-applies both', () => {
    const doc = documentToYDoc(source);
    const undo = createUndoManager(doc);
    reconnect(doc, { edge: 'e_1', end: 'to', to: { node: 'n_a', port: top } });
    endUndoStep(undo);
    const reconnected = yDocToDocument(doc);
    expect(undo.undoStack).toHaveLength(1);
    undo.undo();
    expect(yDocToDocument(doc)).toEqual(source);
    undo.redo();
    expect(yDocToDocument(doc)).toEqual(reconnected);
  });

  it('throws and writes nothing for a missing edge, a missing node, a port the node lacks, or a self-loop', () => {
    const doc = documentToYDoc(source);
    expect(() => reconnect(doc, { edge: 'e_gone', end: 'to', to: { node: 'n_a', port: top } })).toThrow(/not in the document/);
    expect(() => reconnect(doc, { edge: 'e_1', end: 'to', to: { node: 'n_gone', port: top } })).toThrow(/not in the document/);
    expect(() => reconnect(doc, { edge: 'e_1', end: 'from', to: { node: 'n_b', port: right } })).toThrow(/does not declare/);
    expect(() => reconnect(doc, { edge: 'e_1', end: 'to', to: { node: 'n_b', port: { id: 'in', x: 0, y: 0.5 } } })).toThrow(/other end/);
    expect(yDocToDocument(doc)).toEqual(source);
  });
});

describe('connecting to a column row of a table', () => {
  const tables: Document = parseDocument({
    id: 'doc_cols',
    schemaVersion: 1,
    title: 'Cols',
    type: 'architecture',
    nodes: [
      { id: 't_order', kind: 'entity', label: 'Order', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'FK CustomerId int not null'] } },
      { id: 't_customer', kind: 'entity', label: 'Customer', shape: 'custom:er/entity', geometry: { x: 500, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null'] } },
      { id: 'n_box', kind: 'service', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 300, w: 100, h: 50 } },
    ],
    edges: [],
    meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
  });
  const fk = { id: 'col:CustomerId', x: 1, y: 60 / 92 };
  const pk = { id: 'col:Id', x: 0, y: 42 / 92 };

  it('writes the column port ids on the edge and declares nothing on either table', () => {
    const doc = documentToYDoc(tables);
    connect(doc, { from: { node: 't_order', port: fk }, to: { node: 't_customer', port: pk } }, () => 'e_fk');
    const after = yDocToDocument(doc);
    expect(after.edges[0]).toMatchObject({ id: 'e_fk', from: { node: 't_order', port: 'col:CustomerId' }, to: { node: 't_customer', port: 'col:Id' } });
    expect(after.nodes.every((node) => node.ports === undefined)).toBe(true);
  });

  it('still declares the default ports on a plain node at the other end', () => {
    const doc = documentToYDoc(tables);
    connect(doc, { from: { node: 'n_box', port: { id: 'top', x: 0.5, y: 0 } }, to: { node: 't_customer', port: pk } }, () => 'e_box');
    const after = yDocToDocument(doc);
    expect(after.nodes.find((node) => node.id === 'n_box')?.ports?.map((port) => port.id)).toEqual(['top', 'right', 'bottom', 'left']);
    expect(after.nodes.find((node) => node.id === 't_customer')?.ports).toBeUndefined();
  });

  it('refuses a column the table does not have, and a column port on a node that is not a table, writing nothing', () => {
    const doc = documentToYDoc(tables);
    expect(() => connect(doc, { from: { node: 't_order', port: { id: 'col:Nope', x: 1, y: 0.5 } }, to: { node: 't_customer', port: pk } }, () => 'e_x')).toThrow(/no such column/);
    expect(() => connect(doc, { from: { node: 'n_box', port: fk }, to: { node: 't_customer', port: pk } }, () => 'e_x')).toThrow(/no such column/);
    expect(yDocToDocument(doc).edges).toEqual([]);
  });

  it('moves an existing end onto another row of the same or another table, in one undo step', () => {
    const doc = documentToYDoc({ ...tables, edges: [{ id: 'e_fk', kind: 'association', from: { node: 't_order', port: 'col:CustomerId' }, to: { node: 't_customer', port: 'col:Id' }, routing: { mode: 'orthogonal' } }] });
    const undo = createUndoManager(doc);
    expect(reconnect(doc, { edge: 'e_fk', end: 'from', to: { node: 't_order', port: { id: 'col:Id', x: 1, y: 0.5 } } })).toBe(true);
    endUndoStep(undo);
    expect(yDocToDocument(doc).edges[0]?.from).toEqual({ node: 't_order', port: 'col:Id' });
    expect(reconnect(doc, { edge: 'e_fk', end: 'from', to: { node: 't_order', port: { id: 'col:Id', x: 0, y: 0.5 } } })).toBe(false);
    undo.undo();
    expect(yDocToDocument(doc).edges[0]?.from).toEqual({ node: 't_order', port: 'col:CustomerId' });
  });
});
