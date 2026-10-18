import type { Document, Edge, Node } from '../model/types.ts';
import { edgePolyline, indexDocument, type PlacedNode } from './documentIndex.ts';
import { rotatedBounds } from './geometry.ts';

it('lifts moving roots without changing model parents or unclipping their children', () => {
  const document: Document = {
    id: 'doc', schemaVersion: 1, title: 'Drag', type: 'flowchart',
    nodes: [
      { id: 'outer', kind: 'container', label: 'Outer', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 100 } },
      { id: 'inner', parent: 'outer', kind: 'container', label: 'Inner', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 100 } },
      { id: 'child', parent: 'inner', kind: 'service', label: 'Child', shape: 'rectangle', geometry: { x: 220, y: 20, w: 100, h: 40 } },
    ], edges: [],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 'test' },
  };
  const before = structuredClone(document);
  const index = indexDocument(document, new Set(['inner', 'child']));
  expect(index.rootNodes.map((node) => node.id)).toEqual(['outer', 'inner']);
  expect(index.childrenOf.get('inner')?.map((node) => node.id)).toEqual(['child']);
  expect(index.nodeBounds.get('inner')).toEqual({ x: 200, y: 0, w: 100, h: 100 });
  expect(index.nodeBounds.get('child')).toEqual({ x: 220, y: 20, w: 80, h: 40 });
  expect(document).toEqual(before);
  expect(indexDocument(document).rootNodes.map((node) => node.id)).toEqual(['outer']);
});

it('ends an edge at a declared port where the rotated node draws it, and unchanged on an unrotated node', () => {
  const end = (rotation: number | undefined): unknown => {
    const geometry = rotation === undefined ? { x: 0, y: 0, w: 200, h: 100 } : { x: 0, y: 0, w: 200, h: 100, rotation };
    const document: Document = {
      id: 'doc', schemaVersion: 1, title: 'Ports', type: 'flowchart',
      nodes: [
        { id: 'a', kind: 'service', label: 'A', shape: 'rectangle', geometry, ports: [{ id: 'out', x: 1, y: 0.5 }] },
        { id: 'b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 400, y: 400, w: 100, h: 50 } },
      ],
      edges: [{ id: 'e', kind: 'flow', from: { node: 'a', port: 'out' }, to: { node: 'b' }, routing: { mode: 'straight' } }],
      meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 'test' },
    };
    return indexDocument(document).edgePaths.get('e')?.points[0];
  };
  expect(end(90)).toEqual({ x: 100, y: 150 });
  expect(end(0)).toEqual({ x: 200, y: 50 });
  expect(end(undefined)).toEqual({ x: 200, y: 50 });
});

const outlineCases: [Node['shape'], number, number][] = [['document', 0.5, 0.85], ['document', 0.2265625, 0.934375], ['document', 0.7734375, 0.765625], ['document', 1, 0.5], ['manual-input', 0.5, 0.125], ['manual-input', 0.2, 0.2], ['off-page-connector', 0.75, 0.825], ['off-page-connector', 0.5, 1], ['predefined-process', 1, 0.75], ['rectangle', 1, 0.75]];

it.each(outlineCases)('clips unnamed endpoints to %s at (%s, %s)', (shape, x, y) => {
  for (const rotation of shape === 'rectangle' ? [0] : [0, 37, 90]) {
    for (const reverse of [false, true]) {
      for (const waypoint of [false, true]) {
        for (const declared of [false, true]) {
          const angle = rotation * Math.PI / 180;
          const world = (px: number, py: number) => ({
            x: 110 + (px - 0.5) * 200 * Math.cos(angle) - (py - 0.5) * 100 * Math.sin(angle),
            y: 70 + (px - 0.5) * 200 * Math.sin(angle) + (py - 0.5) * 100 * Math.cos(angle),
          });
          const expected = world(declared ? 0.2 : x, declared ? 0.3 : y);
          const aim = world(0.5 + (x - 0.5) * 5, 0.5 + (y - 0.5) * 5);
          const a: PlacedNode = { id: 'a', kind: 'process', label: '', shape,
            geometry: { x: 10, y: 20, w: 200, h: 100, rotation }, ports: [{ id: 'p', x: 0.2, y: 0.3 }] };
          const b: PlacedNode = { id: 'b', kind: 'process', label: '', shape: 'rectangle',
            geometry: { x: aim.x - 10, y: aim.y - 10, w: 20, h: 20 } };
          const endpoint = declared ? { node: 'a', port: 'p' } : { node: 'a' };
          const edge: Edge = { id: 'e', kind: 'flow', from: reverse ? { node: 'b' } : endpoint,
            to: reverse ? endpoint : { node: 'b' }, routing: { mode: 'straight', waypoints: waypoint ? [aim] : [] } };
          const points = edgePolyline(edge, reverse ? b : a, reverse ? a : b);
          const actual = points.at(reverse ? -1 : 0);
          expect(actual?.x).toBeCloseTo(expected.x, 7);
          expect(actual?.y).toBeCloseTo(expected.y, 7);
        }
      }
    }
  }
});

describe('pinned waypoints after an endpoint node changes size (checklist item 10)', () => {
  const from: PlacedNode = { id: 'a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 100, w: 100, h: 50 } };
  // A bend the user dragged (or ELK left behind), well clear of both nodes at their starting sizes.
  const waypoints = [
    { x: 200, y: 125 },
    { x: 200, y: 60 },
    { x: 320, y: 60 },
  ];
  const edge = (): Edge => ({ id: 'e', kind: 'flow', from: { node: 'a' }, to: { node: 'b' }, routing: { mode: 'orthogonal', waypoints } });
  const smallB: PlacedNode = { id: 'b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 100, w: 100, h: 50 } };

  it('keeps every waypoint while the endpoint has not grown over them', () => {
    const points = edgePolyline(edge(), from, smallB);
    for (const waypoint of waypoints) {
      expect(points).toContainEqual(waypoint);
    }
  });

  it('drops a waypoint a corner resize has pulled inside the endpoint, leaving no segment through its interior', () => {
    // A corner-handle resize of b growing left and up over the pinned bend, exactly the M2 gap-audit repro.
    const grownB: PlacedNode = { ...smallB, geometry: { x: 150, y: 30, w: 250, h: 150 } };
    const points = edgePolyline(edge(), from, grownB);
    const bounds = rotatedBounds(grownB.geometry);
    for (const point of points) {
      expect(pointStrictlyInside(point, bounds)).toBe(false);
    }
  });
});

function pointStrictlyInside(point: { x: number; y: number }, rect: { x: number; y: number; w: number; h: number }): boolean {
  return point.x > rect.x && point.x < rect.x + rect.w && point.y > rect.y && point.y < rect.y + rect.h;
}

describe('two connectors leaving the same port (M2 final Finding D)', () => {
  const document: Document = {
    id: 'doc', schemaVersion: 1, title: 'Shared port', type: 'architecture',
    nodes: [
      { id: 'source', kind: 'service', label: 'Source', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 }, ports: [{ id: 'out', x: 1, y: 0.5 }] },
      { id: 'queue', kind: 'queue', label: 'Queue', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
      { id: 'db', kind: 'database', label: 'DB', shape: 'rectangle', geometry: { x: 300, y: 200, w: 100, h: 50 } },
    ],
    edges: [
      { id: 'e_publish', kind: 'flow', from: { node: 'source', port: 'out' }, to: { node: 'queue' } },
      { id: 'e_write', kind: 'flow', from: { node: 'source', port: 'out' }, to: { node: 'db' } },
    ],
    meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 'test' },
  };

  it('gives each edge sharing the port a distinct first point, without changing where either target attaches', () => {
    const index = indexDocument(document);
    const first = index.edgePaths.get('e_publish')?.points[0];
    const second = index.edgePaths.get('e_write')?.points[0];
    expect(first).toEqual({ x: 100, y: 25 }); // the first sharer in document order keeps the exact port position
    expect(second).not.toEqual(first); // the required property: the second sharer's first point moves
    expect(second?.x).toBe(first?.x); // the port is on the node's right (vertical) side: the nudge is vertical, not away from the node
    // Each edge's far end (an undeclared port, on its own distinct target node) is nowhere near the other's.
    const publishEnd = index.edgePaths.get('e_publish')?.points.at(-1);
    const writeEnd = index.edgePaths.get('e_write')?.points.at(-1);
    expect(writeEnd?.y).toBeGreaterThan((publishEnd?.y ?? 0) + 100);
  });

  it('leaves a lone edge at a port unmoved (the negative case: sharing must actually be required to nudge anything)', () => {
    const solo: Document = { ...document, edges: document.edges.slice(0, 1) };
    const index = indexDocument(solo);
    expect(index.edgePaths.get('e_publish')?.points[0]).toEqual({ x: 100, y: 25 });
  });
});
