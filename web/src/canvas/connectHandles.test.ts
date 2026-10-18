import { parseDocument } from '../model/parse.ts';
import type { Node } from '../model/types.ts';
import {
  CONNECT_HANDLE_OFFSET,
  CONNECT_HIT_RADIUS,
  connectHandleAt,
  connectHandlePosition,
  connectZones,
  EDGE_END_HANDLE_INSET,
  EDGE_END_HANDLE_MIN_ROUTE,
  edgeEndAt,
  edgeEndHandlePositions,
  handleHoverNode,
  handleNodes,
  ROTATE_HANDLE_RADIUS,
  rotateHandleNode,
  rotateHandlePosition,
} from './connectHandles.ts';
import { indexDocument, type PlacedNode } from './documentIndex.ts';

const meta = { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' };

function node(id: string, x: number, extra: Partial<Node> = {}): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry: { x, y: 0, w: 100, h: 50 }, ...extra };
}

const index = indexDocument(
  parseDocument({ id: 'doc_handles', schemaVersion: 1, title: 'Handles', type: 'architecture', nodes: [node('n_a', 0), node('n_b', 300), node('n_r', 600, { geometry: { x: 600, y: 0, w: 100, h: 50, rotation: 90 } })], edges: [], meta }),
);

function placed(id: string): PlacedNode {
  const found = index.placed.get(id);
  if (found === undefined) {
    throw new Error(`${id} not placed`);
  }
  return found;
}

describe('connectHandlePosition', () => {
  it('sets each default port out along the side it leaves by, by a fixed number of screen pixels at any zoom', () => {
    const a = placed('n_a');
    for (const zoom of [0.5, 1, 4]) {
      const out = CONNECT_HANDLE_OFFSET / zoom;
      expect(connectHandlePosition(a, { x: 0.5, y: 0 }, zoom)).toEqual({ x: 50, y: -out });
      expect(connectHandlePosition(a, { x: 1, y: 0.5 }, zoom)).toEqual({ x: 100 + out, y: 25 });
      expect(connectHandlePosition(a, { x: 0.5, y: 1 }, zoom)).toEqual({ x: 50, y: 50 + out });
      expect(connectHandlePosition(a, { x: 0, y: 0.5 }, zoom)).toEqual({ x: -out, y: 25 });
    }
  });

  it('turns with a rotated node, so a handle stays outside the drawn shape', () => {
    // Rotated a quarter-turn about its centre (650, 25): the box's top edge now faces right.
    const r = placed('n_r');
    expect(connectHandlePosition(r, { x: 0.5, y: 0 }, 1)).toEqual({ x: 675 + CONNECT_HANDLE_OFFSET, y: 25 });
  });

  it('steps a handle sideways clear of the rotate handle, on a turned node and at any zoom, and moves no other', () => {
    for (const [id, zoom] of [['n_a', 1], ['n_a', 3], ['n_r', 1], ['n_r', 0.5]] as const) {
      const n = placed(id);
      const rotator = rotateHandlePosition(n, zoom);
      for (const port of [{ x: 0.5, y: 0 }, { x: 1, y: 0.5 }, { x: 0.5, y: 1 }, { x: 0, y: 0.5 }]) {
        const plain = connectHandlePosition(n, port, zoom);
        const clear = connectHandlePosition(n, port, zoom, true);
        const gap = Math.hypot(clear.x - rotator.x, clear.y - rotator.y) * zoom;
        expect(gap).toBeGreaterThan(CONNECT_HIT_RADIUS + ROTATE_HANDLE_RADIUS);
        if (port.y === 0) {
          // The top handle stays the same distance out from the shape: it only slides along its side.
          expect(clear).not.toEqual(plain);
        } else {
          expect(clear).toEqual(plain);
        }
      }
    }
  });

  it('lets a press on the rotate handle through to it, and still offers the stepped-aside top handle', () => {
    const a = placed('n_a');
    expect(rotateHandleNode(new Set(['n_a']))).toBe('n_a');
    expect(rotateHandleNode(new Set(['n_a', 'n_b']))).toBeUndefined();
    expect(connectHandleAt([a], rotateHandlePosition(a, 1), 1, 'n_a')).toBeUndefined();
    expect(connectHandleAt([a], connectHandlePosition(a, { x: 0.5, y: 0 }, 1, true), 1, 'n_a')?.port.id).toBe('top');
  });
});

describe('connectHandleAt', () => {
  const a = placed('n_a');
  const b = placed('n_b');

  it('takes a press within the hit radius in screen pixels and misses one just beyond it, at every zoom', () => {
    for (const zoom of [0.5, 1, 3]) {
      const right = connectHandlePosition(a, { x: 1, y: 0.5 }, zoom);
      const inside = (CONNECT_HIT_RADIUS - 1) / zoom;
      const outside = (CONNECT_HIT_RADIUS + 1) / zoom;
      expect(connectHandleAt([a], { x: right.x, y: right.y + inside }, zoom)).toEqual({ node: a, port: { id: 'right', x: 1, y: 0.5 } });
      expect(connectHandleAt([a], { x: right.x, y: right.y + outside }, zoom)).toBeUndefined();
    }
  });

  it('only offers the nodes it is given, and picks the nearer handle of two', () => {
    const bLeft = connectHandlePosition(b, { x: 0, y: 0.5 }, 1);
    expect(connectHandleAt([a], bLeft, 1)).toBeUndefined();
    expect(connectHandleAt([a, b], bLeft, 1)?.port.id).toBe('left');
    expect(connectHandleAt([a, b], bLeft, 1)?.node.id).toBe('n_b');
  });

  it('never takes a press on the port itself, where a selected node has its middle resize handle', () => {
    expect(connectHandleAt([a], { x: 100, y: 25 }, 1)).toBeUndefined();
  });
});

describe('handleHoverNode', () => {
  it('keeps the node hovered all the way out to its handles, and lets go past them', () => {
    const reach = CONNECT_HANDLE_OFFSET + CONNECT_HIT_RADIUS;
    expect(handleHoverNode(index, { x: 100 + reach - 1, y: 25 }, 1)?.id).toBe('n_a');
    expect(handleHoverNode(index, { x: 100 + reach + 1, y: 25 }, 1)).toBeUndefined();
    // The same screen distance at twice the zoom is half the world distance.
    expect(handleHoverNode(index, { x: 100 + (reach + 1) / 2, y: 25 }, 2)).toBeUndefined();
    expect(handleHoverNode(index, { x: 100 + (reach - 1) / 2, y: 25 }, 2)?.id).toBe('n_a');
  });

  it('prefers the node the pointer is on over a later neighbour whose grown box reaches over it', () => {
    const tight = indexDocument(
      parseDocument({ id: 'doc_tight', schemaVersion: 1, title: 'Tight', type: 'architecture', nodes: [node('n_a', 0), node('n_b', 120)], edges: [], meta }),
    );
    expect(handleHoverNode(tight, { x: 95, y: 25 }, 1)?.id).toBe('n_a');
    expect(handleHoverNode(tight, { x: 125, y: 25 }, 1)?.id).toBe('n_b');
    // In the gap, on neither shape, the later node's grown box still wins, as before.
    expect(handleHoverNode(tight, { x: 110, y: 25 }, 1)?.id).toBe('n_b');
  });

  it('keeps a child\'s handles over its container out to each handle, and the container\'s own away from any child', () => {
    const nested = indexDocument(
      parseDocument({
        id: 'doc_nested',
        schemaVersion: 1,
        title: 'Nested',
        type: 'architecture',
        nodes: [
          { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 300 } },
          node('n_in', 100, { parent: 'c_box', geometry: { x: 100, y: 100, w: 100, h: 50 } }),
        ],
        edges: [],
        meta,
      }),
    );
    for (const at of [
      { x: 200 + CONNECT_HANDLE_OFFSET, y: 125 },
      { x: 100 - CONNECT_HANDLE_OFFSET, y: 125 },
      { x: 150, y: 100 - CONNECT_HANDLE_OFFSET },
      { x: 150, y: 150 + CONNECT_HANDLE_OFFSET },
    ]) {
      expect(handleHoverNode(nested, at, 1)?.id).toBe('n_in');
    }
    expect(handleHoverNode(nested, { x: 350, y: 250 }, 1)?.id).toBe('c_box');
  });
});

describe('edgeEndHandlePositions', () => {
  it('sets each end handle in along the route by a fixed number of screen pixels, round a bend, at any zoom', () => {
    const bent = [
      { x: 0, y: 0 },
      { x: 10, y: 0 },
      { x: 10, y: 200 },
      { x: 300, y: 200 },
    ];
    for (const zoom of [0.5, 1, 4]) {
      const inset = EDGE_END_HANDLE_INSET / zoom;
      const handles = edgeEndHandlePositions(bent, zoom);
      expect(handles?.from).toEqual(inset <= 10 ? { x: inset, y: 0 } : { x: 10, y: inset - 10 });
      expect(handles?.to).toEqual({ x: 300 - inset, y: 200 });
    }
  });

  it('clears the port and connection handle hit circles, and has no handles on a route too short to fit both apart', () => {
    expect(EDGE_END_HANDLE_INSET - CONNECT_HIT_RADIUS).toBeGreaterThanOrEqual(CONNECT_HANDLE_OFFSET + CONNECT_HIT_RADIUS);
    for (const zoom of [0.5, 1, 4]) {
      const fits = [
        { x: 0, y: 0 },
        { x: EDGE_END_HANDLE_MIN_ROUTE / zoom, y: 0 },
      ];
      const handles = edgeEndHandlePositions(fits, zoom);
      expect(handles).toEqual({ from: { x: EDGE_END_HANDLE_INSET / zoom, y: 0 }, to: { x: (EDGE_END_HANDLE_MIN_ROUTE - EDGE_END_HANDLE_INSET) / zoom, y: 0 } });
      expect(Math.abs((handles?.to?.x ?? 0) - (handles?.from?.x ?? 0))).toBeGreaterThanOrEqual((2 * CONNECT_HIT_RADIUS) / zoom);
      expect(edgeEndHandlePositions([{ x: 0, y: 0 }, { x: (EDGE_END_HANDLE_MIN_ROUTE - 1) / zoom, y: 0 }], zoom)).toBeUndefined();
    }
    expect(edgeEndHandlePositions([{ x: 0, y: 0 }, { x: 60, y: 0 }], 1)).toBeUndefined();
    expect(edgeEndHandlePositions([{ x: 0, y: 0 }, { x: 60, y: 0 }], 2)).toBeDefined();
    expect(edgeEndHandlePositions([], 1)).toBeUndefined();
  });
});

describe('connectZones', () => {
  it('keeps an end handle clear of a neighbouring shape\'s ports and connection handles, or hides it when none is clear', () => {
    // A route from n_a's right port straight through n_b (x 300 to 400) to x 1000.
    const route = [
      { x: 100, y: 25 },
      { x: 1000, y: 25 },
    ];
    for (const zoom of [0.5, 1, 4]) {
      const zones = connectZones(index, route, zoom);
      const handles = edgeEndHandlePositions(route, zoom, zones);
      for (const end of [handles?.from, handles?.to]) {
        expect(end).toBeDefined();
        for (const zone of zones) {
          expect(Math.hypot(zone.x - (end?.x ?? 0), zone.y - (end?.y ?? 0))).toBeGreaterThanOrEqual((2 * CONNECT_HIT_RADIUS) / zoom - 1e-9);
        }
      }
    }
    // At zoom 1 the fixed inset clears n_a, whose right port is the route's start.
    expect(edgeEndHandlePositions(route, 1, connectZones(index, route, 1))?.from).toEqual({ x: 100 + EDGE_END_HANDLE_INSET, y: 25 });
    // Pushed along past n_b's left connection handle when the inset lands on it.
    const past = [
      { x: 300 - CONNECT_HANDLE_OFFSET - EDGE_END_HANDLE_INSET, y: 25 },
      { x: 1000, y: 25 },
    ];
    const from = edgeEndHandlePositions(past, 1, connectZones(index, past, 1))?.from;
    expect(from?.x).toBeGreaterThanOrEqual(300 + 2 * CONNECT_HIT_RADIUS);
    expect(edgeEndAt(past, { x: 300 - CONNECT_HANDLE_OFFSET, y: 25 }, 1, connectZones(index, past, 1))).toBeUndefined();
    // A route crowded with zones along its whole length has no clear spot, so no handles.
    const crowded = [
      { x: 0, y: 25 },
      { x: 400, y: 25 },
    ];
    const wall = Array.from({ length: 41 }, (_, i) => ({ x: i * 10, y: 25 }));
    expect(edgeEndHandlePositions(crowded, 1, wall)).toBeUndefined();
    expect(connectZones(index, [], 1)).toEqual([]);
  });
});

describe('edgeEndAt', () => {
  const route = [
    { x: 100, y: 25 },
    { x: 200, y: 25 },
    { x: 300, y: 25 },
  ];

  it('names the end whose handle a press is on, within the hit radius in screen pixels, and never the port itself', () => {
    const from = 100 + EDGE_END_HANDLE_INSET;
    expect(edgeEndAt(route, { x: from + CONNECT_HIT_RADIUS - 1, y: 25 }, 1)).toBe('from');
    expect(edgeEndAt(route, { x: 300 - EDGE_END_HANDLE_INSET, y: 25 - (CONNECT_HIT_RADIUS - 1) }, 1)).toBe('to');
    expect(edgeEndAt(route, { x: from + CONNECT_HIT_RADIUS + 1, y: 25 }, 1)).toBeUndefined();
    expect(edgeEndAt(route, { x: 100, y: 25 }, 1)).toBeUndefined();
    expect(edgeEndAt(route, { x: 300, y: 25 }, 1)).toBeUndefined();
    expect(edgeEndAt(route, { x: 200, y: 25 }, 1)).toBeUndefined();
    const zoomed = 100 + EDGE_END_HANDLE_INSET / 4;
    expect(edgeEndAt(route, { x: zoomed + (CONNECT_HIT_RADIUS + 1) / 4, y: 25 }, 4)).toBeUndefined();
    expect(edgeEndAt(route, { x: zoomed + (CONNECT_HIT_RADIUS - 1) / 4, y: 25 }, 4)).toBe('from');
  });

  it('takes no press on a route too short for end handles', () => {
    const short = [
      { x: 0, y: 0 },
      { x: 60, y: 0 },
    ];
    for (const x of [0, 16, 20, 30, 40, 44, 60]) {
      expect(edgeEndAt(short, { x, y: 0 }, 1)).toBeUndefined();
    }
    expect(edgeEndAt([], { x: 0, y: 0 }, 1)).toBeUndefined();
  });
});

describe('handleNodes', () => {
  it('shows the hovered node and a lone selected node, once each, and never a multi-selection or an edge', () => {
    const ids = (hovered: string | undefined, selection: string[]) => handleNodes(index, hovered, new Set(selection)).map((n) => n.id);
    expect(ids(undefined, [])).toEqual([]);
    expect(ids('n_a', [])).toEqual(['n_a']);
    expect(ids(undefined, ['n_b'])).toEqual(['n_b']);
    expect(ids('n_a', ['n_b'])).toEqual(['n_a', 'n_b']);
    expect(ids('n_a', ['n_a'])).toEqual(['n_a']);
    expect(ids(undefined, ['n_a', 'n_b'])).toEqual([]);
    expect(ids(undefined, ['e_missing'])).toEqual([]);
  });
});
