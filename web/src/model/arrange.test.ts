import { describe, expect, it } from 'vitest';
import { indexDocument } from '../canvas/documentIndex.ts';
import { arrange, type ArrangeCommand } from './arrange.ts';
import { parseDocument } from './parse.ts';
import type { Document, Edge, Node } from './types.ts';

function node(id: string, parent?: string): Node {
  const n: Node = { id, kind: 'service', label: id, shape: 'rectangle', geometry: { x: 0, y: 0, w: 40, h: 20 } };
  if (parent !== undefined) {
    n.parent = parent;
  }
  return n;
}

function edge(id: string): Edge {
  return { id, kind: 'flow', from: { node: 'a' }, to: { node: 'b' } };
}

function doc(nodes: Node[], edges: Edge[] = []): Document {
  return {
    id: 'doc_arrange',
    schemaVersion: 1,
    title: 'Arrange',
    type: 'freeform',
    nodes,
    edges,
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 'test' },
  };
}

/** Five root nodes a..e, drawn back (a) to front (e). */
function flat(): Document {
  return doc(['a', 'b', 'c', 'd', 'e'].map((id) => node(id)));
}

function deepFreeze<T>(value: T): T {
  if (value !== null && typeof value === 'object') {
    for (const inner of Object.values(value)) {
      deepFreeze(inner);
    }
    Object.freeze(value);
  }
  return value;
}

const nodeIds = (d: Document): string[] => d.nodes.map((n) => n.id);
const edgeIds = (d: Document): string[] => d.edges.map((e) => e.id);

describe('arrange on a flat document', () => {
  it.each<[ArrangeCommand, string[]]>([
    ['bringToFront', ['a', 'c', 'e', 'b', 'd']],
    ['sendToBack', ['b', 'd', 'a', 'c', 'e']],
    ['bringForward', ['a', 'c', 'b', 'e', 'd']],
    ['sendBackward', ['b', 'a', 'd', 'c', 'e']],
  ])('%s moves a split selection {b, d}, whatever order the ids are given in', (command, expected) => {
    expect(nodeIds(arrange(flat(), ['b', 'd'], command))).toEqual(expected);
    expect(nodeIds(arrange(flat(), ['d', 'b'], command))).toEqual(expected);
  });

  it('bringForward moves an adjacent block together past one unselected node, keeping its order', () => {
    expect(nodeIds(arrange(flat(), ['b', 'c'], 'bringForward'))).toEqual(['a', 'd', 'b', 'c', 'e']);
  });

  it('sendBackward moves an adjacent block together past one unselected node, keeping its order', () => {
    expect(nodeIds(arrange(flat(), ['c', 'd'], 'sendBackward'))).toEqual(['a', 'c', 'd', 'b', 'e']);
  });

  it('bringForward moves only one step, not to the front', () => {
    expect(nodeIds(arrange(flat(), ['a'], 'bringForward'))).toEqual(['b', 'a', 'c', 'd', 'e']);
  });

  it('sendBackward moves only one step, not to the back', () => {
    expect(nodeIds(arrange(flat(), ['e'], 'sendBackward'))).toEqual(['a', 'b', 'c', 'e', 'd']);
  });

  it('bringForward leaves a node already at the front where it is while moving the rest', () => {
    expect(nodeIds(arrange(flat(), ['c', 'e'], 'bringForward'))).toEqual(['a', 'b', 'd', 'c', 'e']);
  });

  it('sendBackward leaves a node already at the back where it is while moving the rest', () => {
    expect(nodeIds(arrange(flat(), ['a', 'c'], 'sendBackward'))).toEqual(['a', 'c', 'b', 'd', 'e']);
  });

  it('ignores ids that are not in the document', () => {
    expect(nodeIds(arrange(flat(), ['n_missing', 'b', 'e_missing'], 'bringToFront'))).toEqual(['a', 'c', 'd', 'e', 'b']);
  });

  it('counts a duplicated id once', () => {
    expect(nodeIds(arrange(flat(), ['b', 'b'], 'bringForward'))).toEqual(['a', 'c', 'b', 'd', 'e']);
  });
});

describe('arrange returns the input when nothing changes', () => {
  it.each<[string, string[], ArrangeCommand]>([
    ['an empty selection', [], 'bringToFront'],
    ['a selection of unknown ids', ['zz'], 'sendToBack'],
    ['bringToFront on the front block', ['d', 'e'], 'bringToFront'],
    ['sendToBack on the back block', ['a', 'b'], 'sendToBack'],
    ['bringForward on the front node', ['e'], 'bringForward'],
    ['sendBackward on the back node', ['a'], 'sendBackward'],
    ['bringForward on everything', ['a', 'b', 'c', 'd', 'e'], 'bringForward'],
  ])('for %s', (_, selection, command) => {
    const input = flat();
    expect(arrange(input, selection, command)).toBe(input);
  });

  it('returns a new document when something does change', () => {
    const input = flat();
    const result = arrange(input, ['e'], 'sendToBack');
    expect(result).not.toBe(input);
    expect(result.nodes).not.toBe(input.nodes);
  });

  it('keeps the edges array itself when only nodes moved', () => {
    const input = doc([node('a'), node('b')], [edge('e1'), edge('e2')]);
    const result = arrange(input, ['a'], 'bringToFront');
    expect(nodeIds(result)).toEqual(['b', 'a']);
    expect(result.edges).toBe(input.edges);
  });
});

describe('arrange leaves the input intact', () => {
  it('never mutates the document and reuses the element objects', () => {
    const input = deepFreeze(flat());
    const result = arrange(input, ['b', 'd'], 'bringToFront');
    expect(nodeIds(input)).toEqual(['a', 'b', 'c', 'd', 'e']);
    expect(result.nodes[3]).toBe(input.nodes[1]);
    expect(result.meta).toBe(input.meta);
    expect(result.title).toBe('Arrange');
  });
});

describe('arrange is scoped per parent container', () => {
  // Array order: n_root1, c_vpc, n_api (in c_vpc), n_root2, n_db (in c_vpc), n_cache (in c_vpc), n_root3.
  // Root group back to front: n_root1, c_vpc, n_root2, n_root3. c_vpc group: n_api, n_db, n_cache.
  function nested(): Document {
    return doc([
      node('n_root1'),
      { ...node('c_vpc'), kind: 'container', geometry: { x: 0, y: 0, w: 400, h: 300 } },
      node('n_api', 'c_vpc'),
      node('n_root2'),
      node('n_db', 'c_vpc'),
      node('n_cache', 'c_vpc'),
      node('n_root3'),
    ]);
  }

  it('bringToFront moves a child to the front of its siblings only, leaving root nodes in their slots', () => {
    expect(nodeIds(arrange(nested(), ['n_api'], 'bringToFront'))).toEqual(['n_root1', 'c_vpc', 'n_db', 'n_root2', 'n_cache', 'n_api', 'n_root3']);
  });

  it('bringForward moves a child past the next sibling, not past an interleaved root node', () => {
    expect(nodeIds(arrange(nested(), ['n_api'], 'bringForward'))).toEqual(['n_root1', 'c_vpc', 'n_db', 'n_root2', 'n_api', 'n_cache', 'n_root3']);
  });

  it('sendBackward moves a root node past the previous root node, skipping the container children between them', () => {
    expect(nodeIds(arrange(nested(), ['n_root2'], 'sendBackward'))).toEqual(['n_root1', 'n_root2', 'n_api', 'c_vpc', 'n_db', 'n_cache', 'n_root3']);
  });

  it('sendToBack on the back child of a group is a no-op even though a root node sits earlier in the array', () => {
    const input = nested();
    expect(arrange(input, ['n_api'], 'sendToBack')).toBe(input);
  });

  it('arranges a selection spanning two groups within each group independently', () => {
    expect(nodeIds(arrange(nested(), ['n_cache', 'n_root3'], 'sendToBack'))).toEqual([
      'n_root3',
      'n_root1',
      'n_cache',
      'c_vpc',
      'n_api',
      'n_db',
      'n_root2',
    ]);
  });

  it('draws the arranged children in the new order', () => {
    const result = arrange(nested(), ['n_api'], 'bringToFront');
    expect(indexDocument(result).childrenOf.get('c_vpc')?.map((n) => n.id)).toEqual(['n_db', 'n_cache', 'n_api']);
    expect(indexDocument(result).rootNodes.map((n) => n.id)).toEqual(['n_root1', 'c_vpc', 'n_root2', 'n_root3']);
  });

  it('treats a dangling parent as the root group', () => {
    const input = doc([node('a'), node('orphan', 'c_gone'), node('b')]);
    expect(nodeIds(arrange(input, ['orphan'], 'bringToFront'))).toEqual(['a', 'b', 'orphan']);
  });

  it('treats a self parent as the root group', () => {
    const input = doc([node('a'), node('self', 'self'), node('b')]);
    expect(nodeIds(arrange(input, ['self'], 'sendToBack'))).toEqual(['self', 'a', 'b']);
  });

  it('treats nodes in a parent cycle as the root group', () => {
    const input = doc([node('r'), node('x', 'y'), node('y', 'x')]);
    expect(nodeIds(arrange(input, ['y'], 'sendToBack'))).toEqual(['y', 'r', 'x']);
  });

  it('groups a grandchild with its own siblings, not its grandparent\'s', () => {
    const input = doc([node('c_outer'), node('c_inner', 'c_outer'), node('n_a', 'c_inner'), node('n_side', 'c_outer'), node('n_b', 'c_inner')]);
    expect(nodeIds(arrange(input, ['n_a'], 'bringForward'))).toEqual(['c_outer', 'c_inner', 'n_b', 'n_side', 'n_a']);
  });
});

describe('arrange on edges', () => {
  function withEdges(): Document {
    return doc([node('a'), node('b')], [edge('e1'), edge('e2'), edge('e3')]);
  }

  it('reorders selected edges among edges', () => {
    expect(edgeIds(arrange(withEdges(), ['e1'], 'bringToFront'))).toEqual(['e2', 'e3', 'e1']);
    expect(edgeIds(arrange(withEdges(), ['e3'], 'sendBackward'))).toEqual(['e1', 'e3', 'e2']);
  });

  it('moves nodes and edges in the same selection without mixing them', () => {
    const result = arrange(withEdges(), ['a', 'e1'], 'bringToFront');
    expect(nodeIds(result)).toEqual(['b', 'a']);
    expect(edgeIds(result)).toEqual(['e2', 'e3', 'e1']);
  });
});

describe('arrange persists through save and reload', () => {
  it('keeps the new order across a JSON round trip', () => {
    const result = arrange(flat(), ['a'], 'bringToFront');
    const reloaded = parseDocument(JSON.parse(JSON.stringify(result)));
    expect(nodeIds(reloaded)).toEqual(['b', 'c', 'd', 'e', 'a']);
  });
});
