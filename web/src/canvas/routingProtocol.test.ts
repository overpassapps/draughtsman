import type { Rect } from './geometry.ts';
import { handleRouteBatch, RouteBatchTracker, type RouteNode, type RouteRequest, type RouteResult } from './routingProtocol.ts';

const rect = (x: number, y: number, w: number, h: number): Rect => ({ x, y, w, h });

/** A row of 100 x 50 nodes along y = 0, named by their left edge. */
function node(x: number): RouteNode {
  return { id: `n${String(x)}`, bounds: rect(x, 0, 100, 50) };
}

/** A right-to-left connector from the node whose right side is at x1 to the node whose left side is at x2. */
function request(edgeId: string, x1: number, x2: number): RouteRequest {
  return {
    edgeId,
    start: { point: { x: x1, y: 25 }, side: 'right', nodeId: `n${String(x1 - 100)}` },
    end: { point: { x: x2, y: 25 }, side: 'left', nodeId: `n${String(x2)}` },
  };
}

/** A stand-in result: the tracker never looks inside a route, only at which edge and batch it belongs to. */
function result(edgeId: string, marker: number): RouteResult {
  return { edgeId, route: { status: 'routed', points: [{ x: marker, y: 0 }] } };
}

describe('handleRouteBatch (the worker side)', () => {
  it('answers under the same batch number with one route per request, in request order', () => {
    // Nodes n0 -> n300 for edge a, n300 -> n700 for edge b, and n500 standing in b's way. Edge a's own nodes are
    // obstacles to b and vice versa, but an edge never routes around the nodes it is attached to.
    const nodes = [node(0), node(300), node(500), node(700)];
    const reply = handleRouteBatch({ type: 'route', batch: 7, nodes, requests: [request('b', 400, 700), request('a', 100, 300)] });
    expect(reply.type).toBe('routed');
    expect(reply.batch).toBe(7);
    if (reply.type !== 'routed') {
      return;
    }
    expect(reply.results.map((entry) => entry.edgeId)).toEqual(['b', 'a']);
    // Edge a has nothing in its way: one straight segment between its ports.
    expect(reply.results[1]?.route.points).toEqual([
      { x: 100, y: 25 },
      { x: 300, y: 25 },
    ]);
    // Edge b has a node across its straight line, so its route detours: more than two points, same two ends.
    const detour = reply.results[0]?.route.points ?? [];
    expect(reply.results[0]?.route.status).toBe('routed');
    expect(detour.length).toBeGreaterThan(2);
    expect(detour[0]).toEqual({ x: 400, y: 25 });
    expect(detour[detour.length - 1]).toEqual({ x: 700, y: 25 });
  });

  it('routes an edge whose two ends are on the same node around every other node', () => {
    const loop: RouteRequest = {
      edgeId: 'self',
      start: { point: { x: 100, y: 25 }, side: 'right', nodeId: 'n0' },
      end: { point: { x: 50, y: 50 }, side: 'bottom', nodeId: 'n0' },
    };
    const reply = handleRouteBatch({ type: 'route', batch: 2, nodes: [node(0), node(300)], requests: [loop] });
    expect(reply.type).toBe('routed');
    if (reply.type !== 'routed') {
      return;
    }
    expect(reply.results[0]?.route.status).toBe('routed');
  });

  it('reports a batch naming a node it does not carry as failed against that batch, instead of throwing inside the worker', () => {
    const reply = handleRouteBatch({ type: 'route', batch: 3, nodes: [node(0)], requests: [request('a', 100, 300)] });
    expect(reply).toEqual({ type: 'failed', batch: 3, message: 'unknown node n300' });
  });

  it('reports a batch that throws as failed against that batch, instead of throwing inside the worker', () => {
    const broken = { ...request('a', 100, 300), start: undefined } as unknown as RouteRequest;
    const reply = handleRouteBatch({ type: 'route', batch: 3, nodes: [node(0), node(300)], requests: [broken] });
    expect(reply.type).toBe('failed');
    expect(reply.batch).toBe(3);
  });
});

describe('RouteBatchTracker (the stale-result rule)', () => {
  it('delivers every result of a batch nothing newer has touched', () => {
    const tracker = new RouteBatchTracker();
    const batch = tracker.begin(['a', 'b']);
    expect(tracker.settle(batch, [result('a', 1), result('b', 1)])).toEqual([result('a', 1), result('b', 1)]);
  });

  it('drops the stale result for an edge a newer batch asked for again, and keeps the rest of the old batch', () => {
    const tracker = new RouteBatchTracker();
    const older = tracker.begin(['a', 'b']);
    const newer = tracker.begin(['b']);
    expect(newer).not.toBe(older);
    // The worker answers in order: the older batch first, with its now-stale route for b.
    expect(tracker.settle(older, [result('a', 1), result('b', 1)])).toEqual([result('a', 1)]);
    expect(tracker.settle(newer, [result('b', 2)])).toEqual([result('b', 2)]);
  });

  it('still drops the stale result when the newer batch answers first', () => {
    const tracker = new RouteBatchTracker();
    const older = tracker.begin(['a']);
    const newer = tracker.begin(['a']);
    expect(tracker.settle(newer, [result('a', 2)])).toEqual([result('a', 2)]);
    expect(tracker.settle(older, [result('a', 1)])).toEqual([]);
  });

  it('treats an edge as fresh again once its latest batch has settled', () => {
    const tracker = new RouteBatchTracker();
    tracker.settle(tracker.begin(['a']), [result('a', 1)]);
    const again = tracker.begin(['a']);
    expect(tracker.settle(again, [result('a', 3)])).toEqual([result('a', 3)]);
  });

  it('ignores a result for an edge the batch never asked for, and a batch it never began or already settled', () => {
    const tracker = new RouteBatchTracker();
    const batch = tracker.begin(['a']);
    expect(tracker.settle(batch + 1, [result('a', 1)])).toEqual([]);
    expect(tracker.settle(batch, [result('a', 1), result('z', 1)])).toEqual([result('a', 1)]);
    expect(tracker.settle(batch, [result('a', 1)])).toEqual([]);
  });
});
