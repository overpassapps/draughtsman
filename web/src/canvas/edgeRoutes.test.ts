import { parseDocument } from '../model/parse.ts';
import type { Document, Edge, Geometry, Id, Node, Point } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { EdgeRouteScheduler, routingSnapshot, staleEdges, withRoutes, type RouteSource } from './edgeRoutes.ts';
import type { Rect } from './geometry.ts';
import type { OrthogonalRoute } from './routing.ts';
import { handleRouteBatch, type RouteNode, type RouteRequest, type RouteResult } from './routingProtocol.ts';

const meta = { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' };

function node(id: string, geometry: Geometry, extra: Partial<Node> = {}): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry, ...extra };
}

function doc(nodes: Node[], edges: Edge[]): Document {
  return parseDocument({ id: 'doc_routes', schemaVersion: 1, title: 'Routes', type: 'architecture', nodes, edges, meta });
}

const orthogonal = { mode: 'orthogonal' as const };

// A and C side by side with B squarely between them: a straight line from A's right to C's left crosses B.
const a = node('n_a', { x: 0, y: 100, w: 100, h: 50 }, { ports: [{ id: 'out', x: 1, y: 0.5 }] });
const b = node('n_b', { x: 200, y: 90, w: 100, h: 70 });
const c = node('n_c', { x: 400, y: 100, w: 100, h: 50 }, { ports: [{ id: 'in', x: 0, y: 0.5 }] });
const blocked: Edge = { id: 'e_ac', kind: 'flow', from: { node: 'n_a', port: 'out' }, to: { node: 'n_c', port: 'in' }, routing: orthogonal };

/** Routes a batch in-process with the worker's own handler, so these tests exercise the real router. */
function inProcess(): RouteSource & { batches: RouteRequest[][] } {
  const batches: RouteRequest[][] = [];
  return {
    batches,
    route(nodes: readonly RouteNode[], requests: readonly RouteRequest[]): Promise<RouteResult[]> {
      batches.push([...requests]);
      const reply = handleRouteBatch({ type: 'route', batch: batches.length, nodes: [...nodes], requests: [...requests] });
      return reply.type === 'routed' ? Promise.resolve(reply.results) : Promise.reject(new Error(reply.message));
    },
  };
}

function crossesInterior(p: Point, q: Point, r: Rect): boolean {
  if (p.y === q.y) {
    return r.y < p.y && p.y < r.y + r.h && Math.max(p.x, q.x) > r.x && Math.min(p.x, q.x) < r.x + r.w;
  }
  return r.x < p.x && p.x < r.x + r.w && Math.max(p.y, q.y) > r.y && Math.min(p.y, q.y) < r.y + r.h;
}

function crosses(points: Point[], rect: Rect): boolean {
  return points.some((point, i) => {
    const previous = points[i - 1];
    return previous !== undefined && crossesInterior(previous, point, rect);
  });
}

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve();
  }
}

describe('flowchart outline attachments', () => {
  it('routes floating ends from document and manual-input outlines', () => {
    const from = node('n_doc', { x: 0, y: 0, w: 200, h: 100 }, { shape: 'document' });
    const to = node('n_input', { x: 0, y: 300, w: 200, h: 100 }, { shape: 'manual-input' });
    const edge: Edge = { id: 'e', kind: 'flow', from: { node: 'n_doc' }, to: { node: 'n_input' }, routing: orthogonal };
    const request = routingSnapshot(indexDocument(doc([from, to], [edge]))).inputs.get('e')?.request;
    expect(request?.start).toEqual({ point: { x: 100, y: 85 }, side: 'bottom', nodeId: 'n_doc' });
    expect(request?.end).toEqual({ point: { x: 100, y: 312.5 }, side: 'top', nodeId: 'n_input' });
  });
});

describe('routingSnapshot', () => {
  it('routes orthogonal edges only; straight and unset edges keep the index path', () => {
    const snapshot = routingSnapshot(
      indexDocument(
        doc(
          [a, b, c],
          [
            blocked,
            { id: 'e_straight', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_c' }, routing: { mode: 'straight' } },
            { id: 'e_unset', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } },
          ],
        ),
      ),
    );
    expect([...snapshot.inputs.keys()]).toEqual(['e_ac']);
  });

  it('attaches at a declared port and leaves by the side it sits on', () => {
    const request = routingSnapshot(indexDocument(doc([a, b, c], [blocked]))).inputs.get('e_ac')?.request;
    expect(request?.start).toEqual({ point: { x: 100, y: 125 }, side: 'right', nodeId: 'n_a' });
    expect(request?.end).toEqual({ point: { x: 400, y: 125 }, side: 'left', nodeId: 'n_c' });
  });

  it('floats an end with no port to the side facing the other end, and re-attaches when that end moves', () => {
    const floating: Edge = { id: 'e_f', kind: 'flow', from: { node: 'n_b' }, to: { node: 'n_c', port: 'in' }, routing: orthogonal };
    const beside = routingSnapshot(indexDocument(doc([b, c], [floating]))).inputs.get('e_f')?.request.start;
    expect(beside).toEqual({ point: { x: 300, y: 125 }, side: 'right', nodeId: 'n_b' });

    const below = node('n_b', { x: 350, y: 400, w: 100, h: 70 });
    const moved = routingSnapshot(indexDocument(doc([below, c], [floating]))).inputs.get('e_f')?.request.start;
    expect(moved).toEqual({ point: { x: 400, y: 400 }, side: 'top', nodeId: 'n_b' });
  });

  it('lets an edge through the containers holding its endpoints, and no others', () => {
    const vpc = node('c_vpc', { x: 350, y: 50, w: 200, h: 200 }, { kind: 'container' });
    const other = node('c_other', { x: 0, y: 400, w: 100, h: 100 }, { kind: 'container' });
    const inside = { ...c, parent: 'c_vpc' };
    const request = routingSnapshot(indexDocument(doc([a, vpc, other, inside], [blocked]))).inputs.get('e_ac')?.request;
    expect(request?.passThrough).toEqual(['c_vpc']);
    expect(routingSnapshot(indexDocument(doc([a, b, c], [blocked]))).inputs.get('e_ac')?.request.passThrough).toBeUndefined();
  });
});

describe('routingSnapshot on a rotated node', () => {
  it('attaches a declared port where the turned shape draws it, leaving by its turned side', () => {
    const turned = node('n_t', { x: 0, y: 0, w: 200, h: 100, rotation: 90 }, { ports: [{ id: 'out', x: 1, y: 0.5 }] });
    const below = node('n_d', { x: 50, y: 400, w: 100, h: 50 });
    const edge: Edge = { id: 'e_t', kind: 'flow', from: { node: 'n_t', port: 'out' }, to: { node: 'n_d' }, routing: orthogonal };
    const request = routingSnapshot(indexDocument(doc([turned, below], [edge]))).inputs.get('e_t')?.request;
    expect(request?.start).toEqual({ point: { x: 100, y: 150 }, side: 'bottom', nodeId: 'n_t' });
  });

  it('floats an end to the midpoint of the drawn side facing the other end', () => {
    const turned = node('n_t', { x: 0, y: 0, w: 200, h: 100, rotation: 90 });
    const beside = node('n_r', { x: 400, y: 25, w: 100, h: 50 }, { ports: [{ id: 'in', x: 0, y: 0.5 }] });
    const edge: Edge = { id: 'e_t', kind: 'flow', from: { node: 'n_t' }, to: { node: 'n_r', port: 'in' }, routing: orthogonal };
    const request = routingSnapshot(indexDocument(doc([turned, beside], [edge]))).inputs.get('e_t')?.request;
    expect(request?.start).toEqual({ point: { x: 150, y: 50 }, side: 'right', nodeId: 'n_t' });
  });
});

describe('routing through the worker handler', () => {
  it('routes around the node between the endpoints (checklist item 9), where the index draws straight through it', () => {
    const index = indexDocument(doc([a, b, c], [blocked]));
    const request = routingSnapshot(index).inputs.get('e_ac')?.request;
    if (!request) {
      throw new Error('no request');
    }
    const reply = handleRouteBatch({ type: 'route', batch: 1, nodes: routingSnapshot(index).nodes, requests: [request] });
    const route = reply.type === 'routed' ? reply.results[0]?.route : undefined;
    expect(route?.status).toBe('routed');
    expect(crosses(route?.points ?? [], b.geometry as Rect)).toBe(false);
    expect(crosses(index.edgePaths.get('e_ac')?.points ?? [], b.geometry as Rect)).toBe(true);
  });

  it('routes into a container that holds the target rather than giving up at its border', () => {
    const vpc = node('c_vpc', { x: 350, y: 50, w: 200, h: 200 }, { kind: 'container' });
    const index = indexDocument(doc([a, vpc, { ...c, parent: 'c_vpc' }], [blocked]));
    const snapshot = routingSnapshot(index);
    const request = snapshot.inputs.get('e_ac')?.request;
    if (!request) {
      throw new Error('no request');
    }
    const reply = handleRouteBatch({ type: 'route', batch: 1, nodes: snapshot.nodes, requests: [request] });
    expect(reply.type === 'routed' ? reply.results[0]?.route.status : undefined).toBe('routed');
  });
});

describe('staleEdges', () => {
  // A second edge far away from everything else, so a change near e_ac must not re-route it.
  const d = node('n_d', { x: 0, y: 800, w: 100, h: 50 });
  const e = node('n_e', { x: 300, y: 800, w: 100, h: 50 });
  const far: Edge = { id: 'e_de', kind: 'flow', from: { node: 'n_d' }, to: { node: 'n_e' }, routing: orthogonal };
  const snap = (nodes: Node[]) => routingSnapshot(indexDocument(doc(nodes, [blocked, far])));
  const straightRoute = (from: Point, to: Point): OrthogonalRoute => ({ status: 'routed', points: [from, to] });
  const routes: ReadonlyMap<Id, OrthogonalRoute> = new Map([
    ['e_ac', { status: 'routed', points: [{ x: 100, y: 125 }, { x: 150, y: 125 }, { x: 150, y: 60 }, { x: 350, y: 60 }, { x: 350, y: 125 }, { x: 400, y: 125 }] }],
    ['e_de', straightRoute({ x: 100, y: 825 }, { x: 300, y: 825 })],
  ]);

  it('marks every orthogonal edge on the first snapshot and none when nothing changed', () => {
    expect(staleEdges(undefined, snap([a, b, c, d, e]), new Map())).toEqual(new Set(['e_ac', 'e_de']));
    expect(staleEdges(snap([a, b, c, d, e]), snap([a, b, c, d, e]), routes)).toEqual(new Set());
  });

  it('marks the edges of a moved or resized endpoint, and not an unrelated edge', () => {
    const movedC = node('n_c', { x: 420, y: 130, w: 100, h: 50 }, { ports: c.ports ?? [] });
    expect(staleEdges(snap([a, b, c, d, e]), snap([a, b, movedC, d, e]), routes)).toEqual(new Set(['e_ac']));
    const grownA = node('n_a', { x: 0, y: 100, w: 120, h: 60 }, { ports: a.ports ?? [] });
    expect(staleEdges(snap([a, b, c, d, e]), snap([grownA, b, c, d, e]), routes)).toEqual(new Set(['e_ac']));
  });

  it('marks an edge re-attached to another port, or whose port moved on its node, with no node moving', () => {
    const twoPorts = node('n_a', { x: 0, y: 100, w: 100, h: 50 }, { ports: [{ id: 'out', x: 1, y: 0.5 }, { id: 'up', x: 0.5, y: 0 }] });
    const reattached = routingSnapshot(indexDocument(doc([twoPorts, b, c, d, e], [{ ...blocked, from: { node: 'n_a', port: 'up' } }, far])));
    expect(staleEdges(routingSnapshot(indexDocument(doc([twoPorts, b, c, d, e], [blocked, far]))), reattached, routes)).toEqual(new Set(['e_ac']));
    const portMoved = node('n_a', { x: 0, y: 100, w: 100, h: 50 }, { ports: [{ id: 'out', x: 1, y: 0.2 }] });
    expect(staleEdges(snap([a, b, c, d, e]), snap([portMoved, b, c, d, e]), routes)).toEqual(new Set(['e_ac']));
  });

  it('marks an edge when an obstacle moves into its path, and only that edge', () => {
    const away = node('n_b', { x: 200, y: 400, w: 100, h: 70 });
    const intoFar = node('n_b', { x: 150, y: 790, w: 100, h: 70 });
    expect(staleEdges(snap([a, away, c, d, e]), snap([a, away, c, d, e]), routes)).toEqual(new Set());
    // e_ac's route (above) lies nowhere near 790; e_de's straight route runs through it.
    expect(staleEdges(snap([a, away, c, d, e]), snap([a, intoFar, c, d, e]), routes)).toEqual(new Set(['e_de']));
  });

  it('marks an edge when the obstacle it detoured round moves out of the way', () => {
    const away = node('n_b', { x: 200, y: 400, w: 100, h: 70 });
    expect(staleEdges(snap([a, b, c, d, e]), snap([a, away, c, d, e]), routes)).toEqual(new Set(['e_ac']));
  });
});

describe('withRoutes', () => {
  it('draws a routed edge along its route and leaves every other edge on the index path', () => {
    const plain: Edge = { id: 'e_plain', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } };
    const index = indexDocument(doc([a, b, c], [blocked, plain]));
    const route: OrthogonalRoute = { status: 'routed', points: [{ x: 100, y: 125 }, { x: 150, y: 125 }, { x: 150, y: 40 }, { x: 400, y: 40 }] };
    const routed = withRoutes(index, new Map([['e_ac', route]]));
    expect(routed.edgePaths.get('e_ac')?.points).toBe(route.points);
    expect(routed.edgeBounds.get('e_ac')?.y).toBeLessThan(index.edgeBounds.get('e_ac')?.y ?? 0);
    expect(routed.edgePaths.get('e_plain')).toBe(index.edgePaths.get('e_plain'));
    expect(withRoutes(index, new Map())).toBe(index);
  });
});

describe('EdgeRouteScheduler', () => {
  it('publishes routes that avoid the obstacle and re-routes when an obstacle moves into the path', async () => {
    const source = inProcess();
    let published: ReadonlyMap<Id, OrthogonalRoute> = new Map();
    const scheduler = new EdgeRouteScheduler(source, (routes) => {
      published = routes;
    });
    // B starts well clear, so the first route is a straight run.
    const clear = node('n_b', { x: 200, y: 400, w: 100, h: 70 });
    scheduler.update(indexDocument(doc([a, clear, c], [blocked])));
    await settle();
    expect(published.get('e_ac')?.points).toEqual([
      { x: 100, y: 125 },
      { x: 400, y: 125 },
    ]);

    scheduler.update(indexDocument(doc([a, b, c], [blocked])));
    await settle();
    const rerouted = published.get('e_ac');
    expect(rerouted?.status).toBe('routed');
    expect(crosses(rerouted?.points ?? [], b.geometry as Rect)).toBe(false);
    expect(source.batches.map((batch) => batch.map((request) => request.edgeId))).toEqual([['e_ac'], ['e_ac']]);
  });

  it('keeps one batch in flight and folds the changes made meanwhile into the next', async () => {
    const answers: (() => void)[] = [];
    const batches: RouteRequest[][] = [];
    const source: RouteSource = {
      route(nodes, requests) {
        batches.push([...requests]);
        return new Promise((resolve) => {
          answers.push(() => {
            const reply = handleRouteBatch({ type: 'route', batch: batches.length, nodes: [...nodes], requests: [...requests] });
            resolve(reply.type === 'routed' ? reply.results : []);
          });
        });
      },
    };
    let published: ReadonlyMap<Id, OrthogonalRoute> = new Map();
    const scheduler = new EdgeRouteScheduler(source, (routes) => {
      published = routes;
    });
    scheduler.update(indexDocument(doc([a, b, c], [blocked])));
    // Three drag frames while the first batch is still out: none of them posts a batch of its own.
    for (const x of [410, 420, 430]) {
      scheduler.update(indexDocument(doc([a, b, node('n_c', { x, y: 100, w: 100, h: 50 }, { ports: c.ports ?? [] })], [blocked])));
    }
    expect(batches).toHaveLength(1);
    expect(published.size).toBe(0);

    answers.shift()?.();
    await settle();
    // The first answer is shown while the one follow-up batch, carrying the last frame, is out.
    expect(published.get('e_ac')?.points.at(-1)).toEqual({ x: 400, y: 125 });
    expect(batches).toHaveLength(2);
    expect(batches[1]?.[0]?.end.point).toEqual({ x: 430, y: 125 });

    answers.shift()?.();
    await settle();
    expect(published.get('e_ac')?.points.at(-1)).toEqual({ x: 430, y: 125 });
    expect(batches).toHaveLength(2);
  });

  it('drops the route of an edge that stops being orthogonal, and a late result for it', async () => {
    const source = inProcess();
    let published: ReadonlyMap<Id, OrthogonalRoute> = new Map();
    const scheduler = new EdgeRouteScheduler(source, (routes) => {
      published = routes;
    });
    scheduler.update(indexDocument(doc([a, b, c], [blocked])));
    scheduler.update(indexDocument(doc([a, b, c], [{ ...blocked, routing: { mode: 'straight' } }])));
    await settle();
    expect(published.has('e_ac')).toBe(false);
  });

  it('drops every route and stops posting once a batch fails, so no edge stays on a route for old positions', async () => {
    const inner = inProcess();
    let calls = 0;
    const source: RouteSource = {
      route(nodes, requests) {
        calls += 1;
        return calls === 2 ? Promise.reject(new Error('worker gone')) : inner.route(nodes, requests);
      },
    };
    let published: ReadonlyMap<Id, OrthogonalRoute> = new Map();
    const scheduler = new EdgeRouteScheduler(source, (routes) => {
      published = routes;
    });
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      scheduler.update(indexDocument(doc([a, b, c], [blocked])));
      await settle();
      expect(published.has('e_ac')).toBe(true);

      scheduler.update(indexDocument(doc([node('n_a', { x: 0, y: 400, w: 100, h: 50 }, { ports: a.ports ?? [] }), b, c], [blocked])));
      await settle();
      expect(published.size).toBe(0);
      expect(warn).toHaveBeenCalledTimes(1);

      scheduler.update(indexDocument(doc([a, b, c], [blocked])));
      await settle();
      expect(calls).toBe(2);
      expect(published.size).toBe(0);
    } finally {
      warn.mockRestore();
    }
  });

  it('re-routes clear of an endpoint that grew over the route by a corner resize, same as a move to that box', async () => {
    // n_c starts well clear of n_a's straight run, then a corner-handle resize grows it leftward over the route.
    const clearC = node('n_c', { x: 400, y: 300, w: 100, h: 50 }, { ports: c.ports ?? [] });
    const grownGeometry: Geometry = { x: 150, y: 90, w: 350, h: 100 };
    const grownC = node('n_c', grownGeometry, { ports: c.ports ?? [] });
    const edge: Edge = { id: 'e_ac2', kind: 'flow', from: { node: 'n_a', port: 'out' }, to: { node: 'n_c' }, routing: orthogonal };

    const routeAfter = async (finalC: Node): Promise<OrthogonalRoute | undefined> => {
      let published: ReadonlyMap<Id, OrthogonalRoute> = new Map();
      const scheduler = new EdgeRouteScheduler(inProcess(), (routes) => {
        published = routes;
      });
      scheduler.update(indexDocument(doc([a, clearC], [edge])));
      await settle();
      scheduler.update(indexDocument(doc([a, finalC], [edge])));
      await settle();
      return published.get('e_ac2');
    };

    const resized = await routeAfter(grownC);
    expect(resized?.status).toBe('routed');
    expect(crosses(resized?.points ?? [], grownGeometry as Rect)).toBe(false);

    // A move to the very same box (no size change) must re-route just as clear of it.
    const movedGeometry: Geometry = { ...grownGeometry, x: grownGeometry.x, y: grownGeometry.y };
    const moved = await routeAfter(node('n_c', movedGeometry, { ports: c.ports ?? [] }));
    expect(moved?.status).toBe('routed');
    expect(crosses(moved?.points ?? [], movedGeometry as Rect)).toBe(false);
  });
});
