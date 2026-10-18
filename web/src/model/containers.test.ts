import { describe, expect, it } from 'vitest';
import {
  absoluteRect,
  descendantsOf,
  dropTarget,
  fitToContents,
  isContainer,
  moveWithContents,
  reparent,
} from './containers.ts';
import type { Document, Node } from './types.ts';

// Absolute geometry throughout. c_vpc (100,100 400x300) holds n_api and the nested container c_subnet
// (300,150 150x150), which holds n_db. n_client is top level. n_note has no geometry yet and sits in c_vpc.
function doc(): Document {
  return {
    id: 'doc_containers',
    schemaVersion: 1,
    title: 'Containers',
    type: 'architecture',
    nodes: [
      { id: 'n_client', kind: 'actor', label: 'Client', shape: 'rectangle', geometry: { x: 0, y: 0, w: 80, h: 40 } },
      { id: 'c_vpc', kind: 'container', label: 'VPC', shape: 'rectangle', geometry: { x: 100, y: 100, w: 400, h: 300 } },
      {
        id: 'n_api',
        kind: 'service',
        label: 'API',
        shape: 'rounded',
        parent: 'c_vpc',
        geometry: { x: 120, y: 140, w: 80, h: 40 },
      },
      {
        id: 'c_subnet',
        kind: 'container',
        label: 'Subnet',
        shape: 'rectangle',
        parent: 'c_vpc',
        geometry: { x: 300, y: 150, w: 150, h: 150 },
      },
      {
        id: 'n_db',
        kind: 'database',
        label: 'DB',
        shape: 'cylinder',
        parent: 'c_subnet',
        geometry: { x: 320, y: 180, w: 60, h: 50 },
      },
      { id: 'n_note', kind: 'note', label: 'Note', shape: 'text', parent: 'c_vpc' },
    ],
    edges: [
      { id: 'e_inner', kind: 'data', from: { node: 'n_api' }, to: { node: 'n_db' }, routing: { mode: 'orthogonal', waypoints: [{ x: 250, y: 160 }] } },
      { id: 'e_cross', kind: 'data', from: { node: 'n_client' }, to: { node: 'n_api' }, routing: { mode: 'orthogonal', waypoints: [{ x: 40, y: 160 }] } },
      { id: 'e_plain', kind: 'data', from: { node: 'n_api' }, to: { node: 'n_db' } },
    ],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 'test' },
  };
}

function withNodes(document: Document, update: (nodes: Node[]) => Node[]): Document {
  return { ...document, nodes: update(document.nodes) };
}

/** Applies a parent change the way an editor would, then reads back the absolute rect. */
function applyParent(document: Document, nodeId: string, parent: string | null): Document {
  return withNodes(document, (nodes) =>
    nodes.map((node) => {
      if (node.id !== nodeId) {
        return node;
      }
      const next = { ...node };
      if (parent === null) {
        delete next.parent;
      } else {
        next.parent = parent;
      }
      return next;
    }),
  );
}

function nodeOf(document: Document, id: string): Node {
  const node = document.nodes.find((n) => n.id === id);
  if (!node) {
    throw new Error(`No node ${id}`);
  }
  return node;
}

function withoutGeometry(node: Node): Node {
  const next = { ...node };
  delete next.geometry;
  return next;
}

describe('descendantsOf and isContainer', () => {
  it('collects children transitively and nothing outside', () => {
    expect([...descendantsOf(doc(), 'c_vpc')].sort()).toEqual(['c_subnet', 'n_api', 'n_db', 'n_note']);
    expect([...descendantsOf(doc(), 'c_subnet')]).toEqual(['n_db']);
    expect(descendantsOf(doc(), 'n_client').size).toBe(0);
  });

  it('terminates on a parent cycle', () => {
    const cyclic = withNodes(doc(), (nodes) => nodes.map((n) => (n.id === 'c_vpc' ? { ...n, parent: 'n_db' } : n)));
    expect([...descendantsOf(cyclic, 'c_vpc')].sort()).toEqual(['c_subnet', 'n_api', 'n_db', 'n_note']);
  });

  it('treats kind container or having children as a container, and nothing else', () => {
    const d = doc();
    expect(isContainer(d, nodeOf(d, 'c_vpc'))).toBe(true);
    expect(isContainer(d, nodeOf(d, 'n_client'))).toBe(false);
    const adopted = applyParent(d, 'n_client', 'n_api');
    expect(isContainer(adopted, nodeOf(adopted, 'n_api'))).toBe(true);
  });

  it('matches the renderer: only a placed child makes a placed node a container', () => {
    // n_note has no geometry, so the renderer does not nest it and draws n_api as a plain node.
    const unplacedChild = applyParent(doc(), 'n_note', 'n_api');
    expect(isContainer(unplacedChild, nodeOf(unplacedChild, 'n_api'))).toBe(false);
    expect(dropTarget(unplacedChild, 'n_client', { x: 130, y: 150, w: 10, h: 10 })).toBe('c_vpc');
    // A placed child does make it one, and the drop lands in it.
    const placedChild = applyParent(doc(), 'n_client', 'n_api');
    expect(isContainer(placedChild, nodeOf(placedChild, 'n_api'))).toBe(true);
    expect(dropTarget(placedChild, 'n_db', { x: 130, y: 150, w: 10, h: 10 })).toBe('n_api');
    // A parent without geometry is not drawn as a container even with a placed child.
    const unplacedParent = withNodes(placedChild, (nodes) => nodes.map((n) => (n.id === 'n_api' ? withoutGeometry(n) : n)));
    expect(isContainer(unplacedParent, nodeOf(unplacedParent, 'n_api'))).toBe(false);
  });
});

describe('absoluteRect', () => {
  it('reads a nested child at its stored absolute position', () => {
    expect(absoluteRect(doc(), 'n_db')).toEqual({ x: 320, y: 180, w: 60, h: 50 });
  });

  it('is undefined for an unknown or unplaced node', () => {
    expect(absoluteRect(doc(), 'n_missing')).toBeUndefined();
    expect(absoluteRect(doc(), 'n_note')).toBeUndefined();
  });
});

describe('fitToContents', () => {
  it('leaves exactly the padding on all four sides of the direct children', () => {
    // Children of c_vpc: n_api (120,140)-(200,180) and c_subnet (300,150)-(450,300). Union (120,140)-(450,300).
    const fitted = fitToContents(doc(), 'c_vpc', 16);
    expect(fitted).toEqual({ x: 104, y: 124, w: 362, h: 192 });
  });

  it('grows as well as shrinks: a container too small for its children is enlarged', () => {
    const tight = withNodes(doc(), (nodes) =>
      nodes.map((n) => (n.id === 'c_subnet' ? { ...n, geometry: { x: 330, y: 190, w: 10, h: 10 } } : n)),
    );
    // n_db is (320,180)-(380,230); with padding 5 the box is (315,175)-(385,235).
    expect(fitToContents(tight, 'c_subnet', 5)).toEqual({ x: 315, y: 175, w: 70, h: 60 });
  });

  it('does not move any child', () => {
    const d = doc();
    const fitted = fitToContents(d, 'c_vpc', 16);
    const after = withNodes(d, (nodes) => nodes.map((n) => (n.id === 'c_vpc' && fitted ? { ...n, geometry: fitted } : n)));
    expect(absoluteRect(after, 'n_api')).toEqual({ x: 120, y: 140, w: 80, h: 40 });
    expect(absoluteRect(after, 'n_db')).toEqual({ x: 320, y: 180, w: 60, h: 50 });
  });

  it('fits a rotated child by the box it is drawn in', () => {
    // A 100x20 child rotated 90 degrees about its centre (150,110) occupies (140,60)-(160,160).
    const rotated = withNodes(doc(), (nodes) => [
      { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 10, h: 10 } },
      { id: 'n_bar', kind: 'service', label: 'Bar', shape: 'rectangle', parent: 'c_box', geometry: { x: 100, y: 100, w: 100, h: 20, rotation: 90 } },
      ...nodes,
    ]);
    const fitted = fitToContents(rotated, 'c_box', 10);
    expect(fitted?.x).toBeCloseTo(130);
    expect(fitted?.y).toBeCloseTo(50);
    expect(fitted?.w).toBeCloseTo(40);
    expect(fitted?.h).toBeCloseTo(120);
  });

  it('keeps the container rotation and gives an unplaced container geometry', () => {
    const d = withNodes(doc(), (nodes) =>
      nodes.map((n) => (n.id === 'c_subnet' ? { ...n, geometry: { x: 0, y: 0, w: 1, h: 1, rotation: 15 } } : n)),
    );
    expect(fitToContents(d, 'c_subnet', 0)).toEqual({ x: 320, y: 180, w: 60, h: 50, rotation: 15 });
    const unplaced = withNodes(doc(), (nodes) => nodes.map((n) => (n.id === 'c_subnet' ? withoutGeometry(n) : n)));
    expect(fitToContents(unplaced, 'c_subnet', 0)).toEqual({ x: 320, y: 180, w: 60, h: 50 });
  });

  it('returns no change for no placed children, an unknown container, or a container that already fits', () => {
    expect(fitToContents(doc(), 'n_client', 16)).toBeUndefined();
    expect(fitToContents(doc(), 'n_missing', 16)).toBeUndefined();
    const onlyUnplaced = withNodes(doc(), (nodes) => nodes.filter((n) => n.id !== 'n_api' && n.id !== 'c_subnet' && n.id !== 'n_db'));
    expect(fitToContents(onlyUnplaced, 'c_vpc', 16)).toBeUndefined();
    const fitted = withNodes(doc(), (nodes) =>
      nodes.map((n) => (n.id === 'c_subnet' ? { ...n, geometry: { x: 310, y: 170, w: 80, h: 70 } } : n)),
    );
    expect(fitToContents(fitted, 'c_subnet', 10)).toBeUndefined();
  });

  it('rejects a negative or non-finite padding', () => {
    expect(() => fitToContents(doc(), 'c_vpc', -1)).toThrow(RangeError);
    expect(() => fitToContents(doc(), 'c_vpc', Number.NaN)).toThrow(RangeError);
  });
});

describe('moveWithContents', () => {
  it('moves the container and every descendant by exactly the delta, and nothing else', () => {
    const move = moveWithContents(doc(), 'c_vpc', { x: 25, y: -10 });
    expect(move.nodes.get('c_vpc')).toEqual({ x: 125, y: 90, w: 400, h: 300 });
    expect(move.nodes.get('n_api')).toEqual({ x: 145, y: 130, w: 80, h: 40 });
    expect(move.nodes.get('c_subnet')).toEqual({ x: 325, y: 140, w: 150, h: 150 });
    expect(move.nodes.get('n_db')).toEqual({ x: 345, y: 170, w: 60, h: 50 });
    expect(move.nodes.has('n_client')).toBe(false);
    expect(move.nodes.has('n_note')).toBe(false);
    expect(move.nodes.size).toBe(4);
  });

  it('keeps each child at the same offset from the container', () => {
    const d = doc();
    const move = moveWithContents(d, 'c_subnet', { x: -7, y: 12 });
    const container = move.nodes.get('c_subnet');
    const child = move.nodes.get('n_db');
    expect(child && container ? [child.x - container.x, child.y - container.y] : undefined).toEqual([20, 30]);
    expect(move.nodes.has('c_vpc')).toBe(false);
    expect(move.nodes.has('n_api')).toBe(false);
  });

  it('keeps rotation and size', () => {
    const d = withNodes(doc(), (nodes) => nodes.map((n) => (n.id === 'n_db' ? { ...n, geometry: { x: 320, y: 180, w: 60, h: 50, rotation: 30 } } : n)));
    expect(moveWithContents(d, 'c_subnet', { x: 1, y: 1 }).nodes.get('n_db')).toEqual({ x: 321, y: 181, w: 60, h: 50, rotation: 30 });
  });

  it('shifts waypoints of edges inside the moved set only', () => {
    const move = moveWithContents(doc(), 'c_vpc', { x: 25, y: -10 });
    expect(move.waypoints.get('e_inner')).toEqual([{ x: 275, y: 150 }]);
    expect(move.waypoints.has('e_cross')).toBe(false);
    expect(move.waypoints.has('e_plain')).toBe(false);
  });

  it('does not mutate the document', () => {
    const d = doc();
    moveWithContents(d, 'c_vpc', { x: 25, y: -10 });
    expect(d).toEqual(doc());
  });

  it('returns nothing for a zero delta or an unknown node', () => {
    const zero = moveWithContents(doc(), 'c_vpc', { x: 0, y: 0 });
    expect(zero.nodes.size + zero.waypoints.size).toBe(0);
    const unknown = moveWithContents(doc(), 'n_missing', { x: 5, y: 5 });
    expect(unknown.nodes.size + unknown.waypoints.size).toBe(0);
  });
});

describe('reparent', () => {
  it('sets a parent without changing the absolute rect', () => {
    const result = reparent(doc(), 'n_client', 'c_subnet');
    expect(result).toEqual({ changed: true, nodeId: 'n_client', parent: 'c_subnet' });
    const after = applyParent(doc(), 'n_client', 'c_subnet');
    expect(absoluteRect(after, 'n_client')).toEqual({ x: 0, y: 0, w: 80, h: 40 });
  });

  it('clears a nested parent without changing the absolute rect', () => {
    expect(reparent(doc(), 'n_db', null)).toEqual({ changed: true, nodeId: 'n_db', parent: null });
    const after = applyParent(doc(), 'n_db', null);
    expect(absoluteRect(after, 'n_db')).toEqual({ x: 320, y: 180, w: 60, h: 50 });
  });

  it('moves a nested container out to the top level with its children unchanged', () => {
    expect(reparent(doc(), 'c_subnet', null)).toEqual({ changed: true, nodeId: 'c_subnet', parent: null });
    const after = applyParent(doc(), 'c_subnet', null);
    expect(absoluteRect(after, 'c_subnet')).toEqual({ x: 300, y: 150, w: 150, h: 150 });
    expect(absoluteRect(after, 'n_db')).toEqual({ x: 320, y: 180, w: 60, h: 50 });
    expect([...descendantsOf(after, 'c_subnet')]).toEqual(['n_db']);
  });

  it('refuses to make a node its own ancestor', () => {
    expect(reparent(doc(), 'c_vpc', 'c_vpc')).toEqual({ changed: false, reason: 'self' });
    expect(reparent(doc(), 'c_vpc', 'c_subnet')).toEqual({ changed: false, reason: 'cycle' });
    expect(reparent(doc(), 'c_vpc', 'n_db')).toEqual({ changed: false, reason: 'cycle' });
  });

  it('allows moving a node into a sibling or an ancestor, which is not a cycle', () => {
    expect(reparent(doc(), 'n_api', 'c_subnet')).toEqual({ changed: true, nodeId: 'n_api', parent: 'c_subnet' });
    expect(reparent(doc(), 'n_db', 'c_vpc')).toEqual({ changed: true, nodeId: 'n_db', parent: 'c_vpc' });
  });

  it('reports unknown ids and no-op changes', () => {
    expect(reparent(doc(), 'n_missing', null)).toEqual({ changed: false, reason: 'unknown-node' });
    expect(reparent(doc(), 'n_client', 'c_missing')).toEqual({ changed: false, reason: 'unknown-parent' });
    expect(reparent(doc(), 'n_api', 'c_vpc')).toEqual({ changed: false, reason: 'unchanged' });
    expect(reparent(doc(), 'n_client', null)).toEqual({ changed: false, reason: 'unchanged' });
  });
});

describe('dropTarget', () => {
  it('picks the innermost container that fully contains the rect', () => {
    expect(dropTarget(doc(), 'n_client', { x: 330, y: 200, w: 20, h: 20 })).toBe('c_subnet');
    expect(dropTarget(doc(), 'n_client', { x: 150, y: 250, w: 20, h: 20 })).toBe('c_vpc');
  });

  it('returns null outside every container', () => {
    expect(dropTarget(doc(), 'n_api', { x: 600, y: 600, w: 20, h: 20 })).toBeNull();
  });

  it('needs full containment: a rect straddling a border falls to the outer container', () => {
    // Straddles c_subnet's left edge (x=300) but lies inside c_vpc.
    expect(dropTarget(doc(), 'n_client', { x: 290, y: 200, w: 20, h: 20 })).toBe('c_vpc');
    // Straddles c_vpc's left edge (x=100): top level.
    expect(dropTarget(doc(), 'n_client', { x: 90, y: 200, w: 20, h: 20 })).toBeNull();
  });

  it('counts a rect touching the border from inside as inside', () => {
    expect(dropTarget(doc(), 'n_client', { x: 300, y: 150, w: 150, h: 150 })).toBe('c_subnet');
    expect(dropTarget(doc(), 'n_client', { x: 300, y: 150, w: 150.5, h: 150 })).toBe('c_vpc');
  });

  it('never drops a node into itself or its own descendants', () => {
    // A rect inside c_subnet, dragging c_vpc: c_subnet is c_vpc's descendant, and c_vpc is itself.
    expect(dropTarget(doc(), 'c_vpc', { x: 330, y: 200, w: 20, h: 20 })).toBeNull();
    // Dragging c_subnet inside its own bounds: falls to c_vpc, not to itself.
    expect(dropTarget(doc(), 'c_subnet', { x: 330, y: 200, w: 20, h: 20 })).toBe('c_vpc');
  });

  it('does not drop into the part of a nested container clipped away by its parent', () => {
    // c_subnet widened to x=600 overhangs c_vpc (right edge 500); the overhang is not drawn.
    const overhang = withNodes(doc(), (nodes) =>
      nodes.map((n) => (n.id === 'c_subnet' ? { ...n, geometry: { x: 300, y: 150, w: 300, h: 150 } } : n)),
    );
    expect(dropTarget(overhang, 'n_client', { x: 520, y: 200, w: 20, h: 20 })).toBeNull();
    expect(dropTarget(overhang, 'n_client', { x: 460, y: 200, w: 20, h: 20 })).toBe('c_subnet');
  });

  it('breaks a tie between overlapping siblings by smaller area, then later document order', () => {
    const siblings = withNodes(doc(), (nodes) => [
      ...nodes,
      { id: 'c_big', kind: 'container', label: 'Big', shape: 'rectangle', geometry: { x: 1000, y: 0, w: 200, h: 200 } },
      { id: 'c_small', kind: 'container', label: 'Small', shape: 'rectangle', geometry: { x: 1050, y: 50, w: 100, h: 100 } },
      { id: 'c_same', kind: 'container', label: 'Same', shape: 'rectangle', geometry: { x: 1050, y: 50, w: 100, h: 100 } },
    ]);
    expect(dropTarget(siblings, 'n_client', { x: 1060, y: 60, w: 10, h: 10 })).toBe('c_same');
    const reordered = withNodes(siblings, (nodes) => [...nodes].reverse());
    expect(dropTarget(reordered, 'n_client', { x: 1060, y: 60, w: 10, h: 10 })).toBe('c_small');
    // Only c_big contains this one.
    expect(dropTarget(siblings, 'n_client', { x: 1010, y: 10, w: 10, h: 10 })).toBe('c_big');
  });

  it('treats a node with children as a container and ignores plain nodes', () => {
    // n_api has no children: dropping inside its box lands in c_vpc.
    expect(dropTarget(doc(), 'n_client', { x: 130, y: 145, w: 10, h: 10 })).toBe('c_vpc');
    const adopted = applyParent(doc(), 'n_db', 'n_api');
    expect(dropTarget(adopted, 'n_client', { x: 130, y: 145, w: 10, h: 10 })).toBe('n_api');
  });

  it('ignores containers without geometry', () => {
    const unplaced = withNodes(doc(), (nodes) => nodes.map((n) => (n.id === 'c_subnet' ? withoutGeometry(n) : n)));
    expect(dropTarget(unplaced, 'n_client', { x: 330, y: 200, w: 20, h: 20 })).toBe('c_vpc');
  });

  it('treats a composite state as a container, so a drop or reparent nests like any container', () => {
    const state = (): Document => ({
      id: 'doc_state',
      schemaVersion: 1,
      title: 'State',
      type: 'flowchart',
      nodes: [
        { id: 'n_cs', kind: 'composite-state', label: 'Order', shape: 'custom:state/composite-state', geometry: { x: 100, y: 100, w: 300, h: 240 } },
        { id: 'n_a', kind: 'process', label: 'Verify', shape: 'rounded', geometry: { x: 40, y: 40, w: 160, h: 80 } },
        { id: 'n_p', kind: 'process', label: 'Step', shape: 'rounded', geometry: { x: 600, y: 40, w: 160, h: 80 } },
      ],
      edges: [],
      meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 'test' },
    });

    // A composite state is a container even before it has children; a plain process is not.
    const find = (id: string): Node => {
      const found = state().nodes.find((node) => node.id === id);
      if (!found) {
        throw new Error(`no node ${id}`);
      }
      return found;
    };
    expect(isContainer(state(), find('n_cs'))).toBe(true);
    expect(isContainer(state(), find('n_p'))).toBe(false);

    // A node dropped inside it nests, and reparent records that parent.
    expect(dropTarget(state(), 'n_a', { x: 200, y: 200, w: 20, h: 20 })).toBe('n_cs');
    const parented = applyParent(state(), 'n_a', 'n_cs');
    expect(parented.nodes.find((node) => node.id === 'n_a')?.parent).toBe('n_cs');
    // Geometry is absolute, so nesting does not move the child.
    expect(absoluteRect(parented, 'n_a')).toEqual({ x: 40, y: 40, w: 160, h: 80 });
    expect(reparent(state(), 'n_a', 'n_cs')).toEqual({ changed: true, nodeId: 'n_a', parent: 'n_cs' });

    // A process cannot parent, so the same reparent is refused by kind elsewhere; here reparent itself stays neutral.
    expect(reparent(state(), 'n_a', 'n_p')).toEqual({ changed: true, nodeId: 'n_a', parent: 'n_p' });
  });
});
