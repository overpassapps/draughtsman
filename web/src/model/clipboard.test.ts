import { describe, expect, it } from 'vitest';
import {
  CLIPBOARD_FORMAT,
  CLIPBOARD_VERSION,
  copy,
  paste,
  randomIdGenerator,
  type ClipboardPayload,
  type IdGenerator,
} from './clipboard.ts';
import { parseDocument } from './parse.ts';
import type { Document, Edge, Node } from './types.ts';

// c_vpc contains n_api and n_db; n_db contains n_disk. n_client sits outside. n_note has no geometry yet.
function source(): Document {
  return {
    id: 'doc_source',
    schemaVersion: 1,
    title: 'Source',
    type: 'architecture',
    nodes: [
      { id: 'n_client', kind: 'actor', label: 'Client', shape: 'rectangle', geometry: { x: 0, y: 0, w: 80, h: 40 } },
      {
        id: 'c_vpc',
        kind: 'container',
        label: 'VPC',
        shape: 'rectangle',
        geometry: { x: 100, y: 100, w: 300, h: 200 },
      },
      {
        id: 'n_api',
        kind: 'service',
        label: 'API',
        shape: 'rounded',
        parent: 'c_vpc',
        geometry: { x: 120, y: 140, w: 80, h: 40 },
        ports: [{ id: 'db', x: 1, y: 0.5 }],
        props: { owner: 'platform' },
      },
      {
        id: 'n_db',
        kind: 'database',
        label: 'DB',
        shape: 'cylinder',
        parent: 'c_vpc',
        geometry: { x: 260, y: 150, w: 60, h: 60, rotation: 15 },
        style: { fill: '#FFFFFF' },
      },
      {
        id: 'n_disk',
        kind: 'service',
        label: 'Disk',
        shape: 'rectangle',
        parent: 'n_db',
        geometry: { x: 270, y: 160, w: 20, h: 20 },
      },
      { id: 'n_note', kind: 'note', label: 'Note', shape: 'text' },
    ],
    edges: [
      { id: 'e_1', kind: 'flow', from: { node: 'n_client' }, to: { node: 'n_api' } },
      {
        id: 'e_2',
        kind: 'data',
        label: 'reads',
        from: { node: 'n_api', port: 'db' },
        to: { node: 'n_db' },
        routing: { mode: 'orthogonal', waypoints: [{ x: 230, y: 160 }] },
      },
      { id: 'e_3', kind: 'flow', from: { node: 'n_db' }, to: { node: 'n_disk' } },
    ],
    meta: { createdAt: '2026-09-20T22:00:00.000Z', updatedAt: '2026-09-20T22:00:00.000Z', createdBy: 'test' },
  };
}

function emptyTarget(nodes: Node[] = [], edges: Edge[] = []): Document {
  return {
    id: 'doc_target',
    schemaVersion: 1,
    title: 'Target',
    type: 'architecture',
    nodes,
    edges,
    meta: { createdAt: '2026-09-21T09:00:00.000Z', updatedAt: '2026-09-21T09:00:00.000Z', createdBy: 'test' },
  };
}

/** Deterministic ids: p_n1, p_n2, ... and p_e1, ... with an independent counter per kind. */
function counter(prefix = 'p'): IdGenerator {
  let nodes = 0;
  let edges = 0;
  return (kind) => (kind === 'node' ? `${prefix}_n${String(++nodes)}` : `${prefix}_e${String(++edges)}`);
}

/** Proposes exactly these ids in order, whatever the kind, and fails the test if asked for more. */
function sequence(proposals: readonly string[]): IdGenerator {
  let next = 0;
  return () => nth(proposals, next++);
}

/** The element at `index`, failing the test rather than returning undefined. */
function nth<T>(items: readonly T[], index: number): T {
  const item = items[index];
  if (item === undefined) {
    throw new Error(`No element at index ${String(index)} of ${String(items.length)}.`);
  }
  return item;
}

function ids(elements: readonly { id: string }[]): string[] {
  return elements.map((element) => element.id);
}

describe('copy', () => {
  it('copies the selected nodes and only the edges between them', () => {
    const payload = copy(source(), ['n_api', 'n_db']);
    expect(payload.format).toBe('draughtsman/selection');
    expect(payload.version).toBe(1);
    expect(ids(payload.nodes)).toEqual(['n_api', 'n_db', 'n_disk']);
    // e_2 and e_3 join copied nodes; e_1 reaches n_client, which was not copied.
    expect(ids(payload.edges)).toEqual(['e_2', 'e_3']);
  });

  it('drops a selected edge whose endpoints were not both copied, and keeps it when they were', () => {
    expect(ids(copy(source(), ['n_api', 'e_1']).edges)).toEqual([]);
    expect(ids(copy(source(), ['n_client', 'n_api', 'e_1']).edges)).toEqual(['e_1']);
  });

  it('copies an edge between copied nodes even when the edge itself was not selected', () => {
    expect(ids(copy(source(), ['n_client', 'n_api']).edges)).toEqual(['e_1']);
  });

  it('copies every descendant of a selected container, transitively', () => {
    const payload = copy(source(), ['c_vpc']);
    expect(ids(payload.nodes)).toEqual(['c_vpc', 'n_api', 'n_db', 'n_disk']);
    expect(ids(payload.edges)).toEqual(['e_2', 'e_3']);
  });

  it('does not copy the container, siblings or unrelated nodes of a selected child', () => {
    const payload = copy(source(), ['n_api']);
    expect(ids(payload.nodes)).toEqual(['n_api']);
    expect(ids(payload.edges)).toEqual([]);
  });

  it('records the source document and keeps every parent, copied or not', () => {
    const payload = copy(source(), ['n_api', 'n_db']);
    const api = payload.nodes.find((node) => node.id === 'n_api');
    const disk = payload.nodes.find((node) => node.id === 'n_disk');
    expect(payload.source).toBe('doc_source');
    expect(api?.parent).toBe('c_vpc');
    expect(disk?.parent).toBe('n_db');
  });

  it('keeps document order rather than selection order', () => {
    expect(ids(copy(source(), ['n_db', 'n_client', 'n_api']).nodes)).toEqual(['n_client', 'n_api', 'n_db', 'n_disk']);
  });

  it('ignores selected ids that name nothing in the document', () => {
    const payload = copy(source(), ['n_missing', 'n_client']);
    expect(ids(payload.nodes)).toEqual(['n_client']);
  });

  it('copies geometry, style, props, ports and routing unchanged', () => {
    const payload = copy(source(), ['c_vpc']);
    const api = payload.nodes.find((node) => node.id === 'n_api');
    const db = payload.nodes.find((node) => node.id === 'n_db');
    expect(api?.geometry).toEqual({ x: 120, y: 140, w: 80, h: 40 });
    expect(api?.ports).toEqual([{ id: 'db', x: 1, y: 0.5 }]);
    expect(api?.props).toEqual({ owner: 'platform' });
    expect(db?.style).toEqual({ fill: '#FFFFFF' });
    expect(db?.geometry?.rotation).toBe(15);
    expect(nth(payload.edges, 0).routing).toEqual({ mode: 'orthogonal', waypoints: [{ x: 230, y: 160 }] });
  });

  it('shares no objects with the document, so editing either leaves the other alone', () => {
    const document = source();
    const payload = copy(document, ['c_vpc']);
    const copiedApi = nth(payload.nodes, 1);
    const copiedWaypoint = nth(nth(payload.edges, 0).routing?.waypoints ?? [], 0);
    copiedApi.geometry = { x: 999, y: 999, w: 1, h: 1 };
    copiedWaypoint.x = 999;
    nth(document.nodes, 1).label = 'Changed';
    expect(nth(document.nodes, 2).geometry?.x).toBe(120);
    expect(document.edges[1]?.routing?.waypoints?.[0]?.x).toBe(230);
    expect(nth(payload.nodes, 0).label).toBe('VPC');
  });

  it('does not modify the source document', () => {
    const document = source();
    copy(document, ['n_api', 'n_db']);
    expect(document).toEqual(source());
  });
});

describe('paste', () => {
  it('gives every node and edge a fresh id and remaps edge endpoints and parents to them', () => {
    const result = paste(emptyTarget(), copy(source(), ['c_vpc']), { x: 20, y: 20 }, counter());
    expect(ids(result.nodes)).toEqual(['p_n1', 'p_n2', 'p_n3', 'p_n4']);
    expect(ids(result.edges)).toEqual(['p_e1', 'p_e2']);
    expect(result.nodes.map((node) => node.parent)).toEqual([undefined, 'p_n1', 'p_n1', 'p_n3']);
    expect(result.edges.map((edge) => [edge.from, edge.to])).toEqual([
      [{ node: 'p_n2', port: 'db' }, { node: 'p_n3' }],
      [{ node: 'p_n3' }, { node: 'p_n4' }],
    ]);
  });

  it('shifts every node and waypoint by the offset, keeping sizes and rotation', () => {
    const result = paste(emptyTarget(), copy(source(), ['c_vpc']), { x: 20, y: -10 }, counter());
    expect(result.nodes.map((node) => node.geometry)).toEqual([
      { x: 120, y: 90, w: 300, h: 200 },
      { x: 140, y: 130, w: 80, h: 40 },
      { x: 280, y: 140, w: 60, h: 60, rotation: 15 },
      { x: 290, y: 150, w: 20, h: 20 },
    ]);
    expect(nth(result.edges, 0).routing).toEqual({ mode: 'orthogonal', waypoints: [{ x: 250, y: 150 }] });
  });

  it('reproduces the same layout in another document when the offset is zero', () => {
    const result = paste(emptyTarget(), copy(source(), ['n_client', 'n_api']), { x: 0, y: 0 }, counter());
    expect(result.nodes.map((node) => node.geometry)).toEqual([
      { x: 0, y: 0, w: 80, h: 40 },
      { x: 120, y: 140, w: 80, h: 40 },
    ]);
    expect(result.edges).toHaveLength(1);
  });

  it('leaves a node without geometry without geometry', () => {
    const result = paste(emptyTarget(), copy(source(), ['n_note']), { x: 20, y: 20 }, counter());
    expect(result.nodes).toHaveLength(1);
    expect('geometry' in nth(result.nodes, 0)).toBe(false);
  });

  it('keeps labels, kinds, shapes, style, props and ports', () => {
    const pasted = paste(emptyTarget(), copy(source(), ['n_api', 'n_db']), { x: 0, y: 0 }, counter()).nodes;
    const api = nth(pasted, 0);
    const db = nth(pasted, 1);
    expect(api).toMatchObject({ kind: 'service', label: 'API', shape: 'rounded', props: { owner: 'platform' } });
    expect(api.ports).toEqual([{ id: 'db', x: 1, y: 0.5 }]);
    expect(db).toMatchObject({ kind: 'database', label: 'DB', shape: 'cylinder', style: { fill: '#FFFFFF' } });
  });

  it('skips a proposed id already used by a node or edge in the target and uses one that is free', () => {
    const target = emptyTarget(
      [{ id: 'p_n1', kind: 'service', label: 'Taken', shape: 'rectangle' }],
      [{ id: 'p_e1', kind: 'flow', from: { node: 'p_n1' }, to: { node: 'p_n1' } }],
    );
    const result = paste(target, copy(source(), ['n_client', 'n_api']), { x: 0, y: 0 }, counter());
    expect(ids(result.nodes)).toEqual(['p_n2', 'p_n3']);
    expect(ids(result.edges)).toEqual(['p_e2']);
  });

  it('does not reuse payload ids when a counter starts at a source id in an empty target', () => {
    const document = emptyTarget(
      [{ id: 'p_n1', kind: 'service', label: 'Copied', shape: 'rectangle' }],
      [{ id: 'p_e1', kind: 'flow', from: { node: 'p_n1' }, to: { node: 'p_n1' } }],
    );
    const result = paste(emptyTarget(), copy(document, ['p_n1']), { x: 0, y: 0 }, counter());
    expect(ids(result.nodes)).toEqual(['p_n2']);
    expect(ids(result.edges)).toEqual(['p_e2']);
    expect(nth(result.edges, 0)).toEqual({
      id: 'p_e2', kind: 'flow', from: { node: 'p_n2' }, to: { node: 'p_n2' },
    });
    for (const id of [...ids(result.nodes), ...ids(result.edges)]) {
      expect(['p_n1', 'p_e1']).not.toContain(id);
    }
  });

  it('pastes into a target with none of the source ids while reserving both payload kinds', () => {
    const target = emptyTarget([{ id: 'n_unrelated', kind: 'note', label: 'Existing', shape: 'text' }]);
    const proposals = sequence(['e_1', 'n_client', 'p_n1', 'n_api', 'p_n2', 'n_client', 'e_1', 'p_e1']);
    const result = paste(target, copy(source(), ['n_client', 'n_api']), { x: 0, y: 0 }, proposals);
    expect(ids(result.nodes)).toEqual(['p_n1', 'p_n2']);
    expect(ids(result.edges)).toEqual(['p_e1']);
    expect(nth(result.edges, 0)).toEqual({
      id: 'p_e1', kind: 'flow', from: { node: 'p_n1' }, to: { node: 'p_n2' },
    });
    expect(ids(target.nodes)).toEqual(['n_unrelated']);
  });

  it('never gives two pasted elements the same id, even when the generator repeats itself', () => {
    const repeating = sequence(['x_a', 'x_a', 'x_b', 'x_b', 'x_a', 'x_c']);
    const result = paste(emptyTarget(), copy(source(), ['n_client', 'n_api']), { x: 0, y: 0 }, repeating);
    expect(ids(result.nodes)).toEqual(['x_a', 'x_b']);
    expect(ids(result.edges)).toEqual(['x_c']);
  });

  it('keeps node and edge ids apart when the generator proposes the same id for both', () => {
    const shared: IdGenerator = counter('same');
    const sameForBoth: IdGenerator = () => shared('node');
    const result = paste(emptyTarget(), copy(source(), ['n_client', 'n_api']), { x: 0, y: 0 }, sameForBoth);
    expect(ids(result.nodes)).toEqual(['same_n1', 'same_n2']);
    expect(ids(result.edges)).toEqual(['same_n3']);
  });

  it('pastes the same payload twice as two disjoint id sets, even from a restarted generator', () => {
    const document = source();
    const payload = copy(document, ['c_vpc']);
    const first = paste(document, payload, { x: 20, y: 20 }, counter());
    const afterFirst: Document = {
      ...document,
      nodes: [...document.nodes, ...first.nodes],
      edges: [...document.edges, ...first.edges],
    };
    const second = paste(afterFirst, payload, { x: 20, y: 20 }, counter());
    expect(ids(first.nodes)).toEqual(['p_n1', 'p_n2', 'p_n3', 'p_n4']);
    expect(ids(second.nodes)).toEqual(['p_n5', 'p_n6', 'p_n7', 'p_n8']);
    expect(ids(second.edges)).toEqual(['p_e3', 'p_e4']);
    expect(nth(second.nodes, 1).parent).toBe('p_n5');
  });

  it('does not reuse any id from the source document when pasting back into it', () => {
    // Node and edge ids share one namespace: e_2 is taken for a node and n_client for an edge.
    const collides = sequence(['n_api', 'c_vpc', 'n_one', 'e_2', 'n_two', 'e_1', 'n_client', 'e_new']);
    const result = paste(source(), copy(source(), ['n_client', 'n_api']), { x: 20, y: 20 }, collides);
    expect(ids(result.nodes)).toEqual(['n_one', 'n_two']);
    expect(ids(result.edges)).toEqual(['e_new']);
  });

  it('does not modify the payload, so it can be pasted again', () => {
    const payload = copy(source(), ['c_vpc']);
    const before = JSON.stringify(payload);
    paste(emptyTarget(), payload, { x: 20, y: 20 }, counter());
    expect(JSON.stringify(payload)).toBe(before);
  });

  it('drops an edge and a parent that point outside a hand-made payload', () => {
    const payload: ClipboardPayload = {
      format: CLIPBOARD_FORMAT,
      version: CLIPBOARD_VERSION,
      nodes: [{ id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', parent: 'n_gone' }],
      edges: [
        { id: 'e_out', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_gone' } },
        { id: 'e_in', kind: 'flow', from: { node: 'n_gone' }, to: { node: 'n_a' } },
        { id: 'e_self', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_a' } },
      ],
    };
    const result = paste(emptyTarget(), payload, { x: 0, y: 0 }, counter());
    expect('parent' in nth(result.nodes, 0)).toBe(false);
    expect(result.edges.map((edge) => [edge.from.node, edge.to.node])).toEqual([['p_n1', 'p_n1']]);
  });

  it('keeps a parent outside the payload when pasting into the source document while that container is still there', () => {
    const result = paste(source(), copy(source(), ['n_api', 'n_db']), { x: 20, y: 20 }, counter());
    expect(result.nodes.map((node) => node.parent)).toEqual(['c_vpc', 'c_vpc', 'p_n2']);
  });

  it('drops a parent outside the payload in another document, even one with a node of the same id', () => {
    const container: Node = { id: 'c_vpc', kind: 'container', label: 'Other VPC', shape: 'rectangle', geometry: { x: 0, y: 0, w: 300, h: 200 } };
    const result = paste(emptyTarget([container]), copy(source(), ['n_api']), { x: 0, y: 0 }, counter());
    expect('parent' in nth(result.nodes, 0)).toBe(false);
  });

  it('drops a parent outside the payload when the source document no longer has that container', () => {
    const payload = copy(source(), ['n_api']);
    const gone = { ...source(), nodes: source().nodes.filter((node) => node.id !== 'c_vpc') };
    expect('parent' in nth(paste(gone, payload, { x: 0, y: 0 }, counter()).nodes, 0)).toBe(false);
  });

  it('refuses a generated id outside the schema pattern', () => {
    const bad: IdGenerator = () => '1-starts-with-a-digit';
    expect(() => paste(emptyTarget(), copy(source(), ['n_client']), { x: 0, y: 0 }, bad)).toThrow(/id pattern/);
  });

  it('gives up rather than looping forever when the generator only proposes taken ids', () => {
    const stuck: IdGenerator = () => 'n_client';
    expect(() => paste(source(), copy(source(), ['n_client']), { x: 0, y: 0 }, stuck)).toThrow(/free node id/);
  });

  it('produces elements that make a schema-valid document when added to the target', () => {
    const target = source();
    const result = paste(target, copy(target, ['c_vpc', 'n_client']), { x: 20, y: 20 });
    const merged: Document = {
      ...target,
      nodes: [...target.nodes, ...result.nodes],
      edges: [...target.edges, ...result.edges],
    };
    expect(() => parseDocument(merged)).not.toThrow();
    const all = [...ids(merged.nodes), ...ids(merged.edges)];
    expect(new Set(all).size).toBe(all.length);
  });
});

describe('clipboard JSON round trip', () => {
  it('survives JSON serialisation unchanged and pastes identically from the parsed copy', () => {
    const payload = copy(source(), ['c_vpc', 'n_note']);
    const parsed = JSON.parse(JSON.stringify(payload)) as ClipboardPayload;
    expect(parsed).toStrictEqual(payload);
    const fromOriginal = paste(emptyTarget(), payload, { x: 20, y: 20 }, counter());
    const fromParsed = paste(emptyTarget(), parsed, { x: 20, y: 20 }, counter());
    expect(fromParsed).toStrictEqual(fromOriginal);
  });
});

describe('randomIdGenerator', () => {
  it('proposes schema-valid ids prefixed n_ for nodes and e_ for edges', () => {
    const pattern = /^[A-Za-z_][A-Za-z0-9_.-]*$/;
    const node = randomIdGenerator('node');
    const edge = randomIdGenerator('edge');
    expect(node).toMatch(/^n_[a-z0-9]+$/);
    expect(edge).toMatch(/^e_[a-z0-9]+$/);
    expect(node).toMatch(pattern);
    expect(edge).toMatch(pattern);
  });
});
