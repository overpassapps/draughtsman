import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';
import { BUDGET_EXHAUSTED_REASON, CORRIDOR_MARGIN, NO_ROUTE_REASON, routeOrthogonal, type RouteEndpoint } from './routing.ts';
import { JITTER_COLUMNS, JITTER_ROWS, jitteredNodes, jitteredRoute } from './routingFixture.ts';

const rect = (x: number, y: number, w: number, h: number): Rect => ({ x, y, w, h });

/** An endpoint on a 100 x 50 node whose `side` has the port at its midpoint. */
function at(x: number, y: number, side: RouteEndpoint['side']): RouteEndpoint {
  const bounds =
    side === 'right'
      ? rect(x - 100, y - 25, 100, 50)
      : side === 'left'
        ? rect(x, y - 25, 100, 50)
        : side === 'bottom'
          ? rect(x - 50, y - 50, 100, 50)
          : rect(x - 50, y, 100, 50);
  return { point: { x, y }, side, bounds };
}

/** Consecutive pairs of points, each an axis-aligned step or a (deliberately) diagonal one if the route is wrong. */
function segments(points: Point[]): [Point, Point][] {
  const result: [Point, Point][] = [];
  for (let i = 1; i < points.length; i += 1) {
    const a = points[i - 1];
    const b = points[i];
    if (a !== undefined && b !== undefined) {
      result.push([a, b]);
    }
  }
  return result;
}

/**
 * Independent check that every segment is horizontal or vertical. Written separately from the router so a test cannot
 * pass by reusing the same line of reasoning the implementation used.
 */
function isOrthogonal(points: Point[]): boolean {
  return segments(points).every(([a, b]) => a.x === b.x || a.y === b.y);
}

/** Independent strict-interior crossing check, again separate from the router's, over the whole polyline. */
function crossesInterior(points: Point[], obstacle: Rect): boolean {
  return segments(points).some(([a, b]) => {
    if (a.y === b.y) {
      const y = a.y;
      const minX = Math.min(a.x, b.x);
      const maxX = Math.max(a.x, b.x);
      return obstacle.y < y && y < obstacle.y + obstacle.h && maxX > obstacle.x && minX < obstacle.x + obstacle.w;
    }
    const x = a.x;
    const minY = Math.min(a.y, b.y);
    const maxY = Math.max(a.y, b.y);
    return obstacle.x < x && x < obstacle.x + obstacle.w && maxY > obstacle.y && minY < obstacle.y + obstacle.h;
  });
}

describe('routeOrthogonal', () => {
  it('finds a single straight segment when nothing blocks the way', () => {
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), []);
    expect(route.status).toBe('routed');
    expect(route.points).toEqual([
      { x: 100, y: 25 },
      { x: 300, y: 25 },
    ]);
  });

  it('routes around obstacles on orthogonal segments without crossing any interior', () => {
    const start = at(100, 25, 'right');
    const end = at(500, 25, 'left');
    // Two tall blockers side by side leave a gap, but the straight line between the endpoints runs through both.
    const obstacles = [rect(200, -40, 60, 160), rect(320, -40, 60, 160)];
    const route = routeOrthogonal(start, end, obstacles);
    expect(route.status).toBe('routed');
    expect(isOrthogonal(route.points)).toBe(true);
    for (const obstacle of obstacles) {
      expect(crossesInterior(route.points, obstacle)).toBe(false);
    }
  });

  it('re-routes when an obstacle moves onto a previously valid route', () => {
    const start = at(100, 25, 'right');
    const end = at(300, 25, 'left');

    const clear = routeOrthogonal(start, end, []);
    expect(clear.status).toBe('routed');

    // Drop an obstacle exactly over the middle of that straight route.
    const obstacle = rect(150, 0, 50, 50);
    expect(crossesInterior(clear.points, obstacle)).toBe(true);

    const rerouted = routeOrthogonal(start, end, [obstacle]);
    expect(rerouted.status).toBe('routed');
    expect(crossesInterior(rerouted.points, obstacle)).toBe(false);
  });

  it('returns the marked fallback when the outward direction is blocked, instead of throwing', () => {
    // An obstacle sits flush against the source's right edge, so the route cannot leave at all.
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), [rect(100, 0, 50, 50)]);
    expect(route.status).toBe('fallback');
    expect(route.reason).toBeDefined();
    expect(isOrthogonal(route.points)).toBe(true);
  });

  it('handles the two endpoints leaving and entering on the same side by looping round', () => {
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'right'), []);
    expect(route.status).toBe('routed');
    expect(isOrthogonal(route.points)).toBe(true);
  });

  it('never cuts through the body of either node it is attached to', () => {
    const start = at(100, 25, 'right');
    const end = at(300, 25, 'right');
    expect(end.bounds).toEqual(rect(200, 0, 100, 50));

    // What a router blind to the attached nodes draws: straight through the target, then a hook back into its port.
    const throughTarget = [
      { x: 100, y: 25 },
      { x: 300, y: 25 },
      { x: 300, y: 17 },
      { x: 308, y: 17 },
      { x: 308, y: 25 },
      { x: 300, y: 25 },
    ];
    expect(crossesInterior(throughTarget, end.bounds)).toBe(true);

    const route = routeOrthogonal(start, end, []);
    expect(route.status).toBe('routed');
    expect(isOrthogonal(route.points)).toBe(true);
    expect(route.points[0]).toEqual(start.point);
    expect(route.points[route.points.length - 1]).toEqual(end.point);
    const middle = route.points.slice(1, -1);
    expect(crossesInterior(middle, start.bounds)).toBe(false);
    expect(crossesInterior(middle, end.bounds)).toBe(false);
    const second = route.points[1];
    const beforeLast = route.points[route.points.length - 2];
    expect(second?.y).toBe(start.point.y);
    expect(second?.x).toBeGreaterThan(start.point.x);
    expect(beforeLast?.y).toBe(end.point.y);
    expect(beforeLast?.x).toBeGreaterThan(end.point.x);
    expect(crossesInterior(route.points, start.bounds)).toBe(false);
    expect(crossesInterior(route.points, end.bounds)).toBe(false);
  });

  it('leaves a clear straight route through the ports unchanged by the attached nodes', () => {
    const start = at(100, 25, 'right');
    const end = at(300, 25, 'left');
    const route = routeOrthogonal(start, end, []);
    expect(route.points).toEqual([start.point, end.point]);
    expect(crossesInterior(route.points, start.bounds)).toBe(false);
    expect(crossesInterior(route.points, end.bounds)).toBe(false);
  });

  it('still reaches a port that an unrelated node sits within the clearance gap of', () => {
    const end = at(300, 25, 'left');
    const neighbour = rect(260, 30, 36, 40);
    const route = routeOrthogonal(at(100, 25, 'right'), end, [neighbour]);
    expect(route.status).toBe('routed');
    expect(route.points).toEqual([{ x: 100, y: 25 }, end.point]);
    expect(crossesInterior(route.points, neighbour)).toBe(false);
  });

  it('falls back when an obstacle sits flush against the target port', () => {
    const blocker = rect(250, 0, 50, 50);
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), [blocker]);
    expect(route.status).toBe('fallback');
    expect(isOrthogonal(route.points)).toBe(true);
  });

  it('keeps a detour at least 12 units from the node it goes round (QA-07: it used to hug it at 8)', () => {
    const blocker = rect(200, 0, 60, 50);
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), [blocker]);
    expect(route.status).toBe('routed');
    // Independent of the router: the distance from each axis-aligned segment to the blocker's box.
    const gap = (a: Point, b: Point): number => {
      const dx = Math.max(Math.min(a.x, b.x) - (blocker.x + blocker.w), blocker.x - Math.max(a.x, b.x), 0);
      const dy = Math.max(Math.min(a.y, b.y) - (blocker.y + blocker.h), blocker.y - Math.max(a.y, b.y), 0);
      return Math.hypot(dx, dy);
    };
    const tightest = Math.min(...segments(route.points).map(([a, b]) => gap(a, b)));
    expect(tightest).toBeGreaterThanOrEqual(12);
    expect(tightest).toBeLessThan(40);
  });

  it('keeps the detour around a single blocker to a handful of bends', () => {
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), [rect(200, 0, 60, 50)]);
    expect(route.status).toBe('routed');
    expect(crossesInterior(route.points, rect(200, 0, 60, 50))).toBe(false);
    expect(route.points.length).toBeLessThanOrEqual(8);
  });

  it('is a pure function of its inputs', () => {
    const obstacles = [rect(200, -40, 60, 160)];
    const first = routeOrthogonal(at(100, 25, 'right'), at(500, 25, 'left'), obstacles);
    const second = routeOrthogonal(at(100, 25, 'right'), at(500, 25, 'left'), obstacles);
    expect(first).toEqual(second);
  });

  it('does not mutate the obstacle list it was given', () => {
    const obstacles = [rect(200, -40, 60, 160)];
    const snapshot = obstacles.map((o) => ({ ...o }));
    routeOrthogonal(at(100, 25, 'right'), at(500, 25, 'left'), obstacles);
    expect(obstacles).toEqual(snapshot);
  });
});
/** Path length plus BEND_COST per bend, computed from the returned points rather than taken from the router. */
function routeCost(points: Point[]): number {
  const length = segments(points).reduce((sum, [a, b]) => sum + Math.abs(b.x - a.x) + Math.abs(b.y - a.y), 0);
  return length + 24 * Math.max(0, points.length - 2);
}

/** A laid-out diagram: nodes in distinct cells of a loose grid, never overlapping, with a gap of at least 30 between. */
function laidOutDiagram(seed: number): { start: RouteEndpoint; end: RouteEndpoint; obstacles: Rect[] } {
  let state = seed >>> 0;
  const random = (): number => {
    state = (state * 1664525 + 1013904223) >>> 0;
    return state / 4294967296;
  };
  const count = 20 + Math.floor(random() * 41);
  const columns = Math.ceil(Math.sqrt(count * 1.5));
  const cellW = 200 + Math.floor(random() * 80);
  const cellH = 110 + Math.floor(random() * 50);
  const cells = new Set<number>();
  while (cells.size < count) {
    cells.add(Math.floor(random() * columns * columns));
  }
  const nodes = [...cells].map((cell) => {
    const w = 90 + Math.round(random() * 70);
    const h = 40 + Math.round(random() * 30);
    return rect((cell % columns) * cellW + Math.round(random() * (cellW - w - 30)), Math.floor(cell / columns) * cellH + Math.round(random() * (cellH - h - 30)), w, h);
  });
  const sides: RouteEndpoint['side'][] = ['top', 'right', 'bottom', 'left'];
  const endpoint = (bounds: Rect): RouteEndpoint => {
    const side = sides[Math.floor(random() * 4)] ?? 'right';
    const point =
      side === 'top'
        ? { x: bounds.x + bounds.w / 2, y: bounds.y }
        : side === 'bottom'
          ? { x: bounds.x + bounds.w / 2, y: bounds.y + bounds.h }
          : side === 'left'
            ? { x: bounds.x, y: bounds.y + bounds.h / 2 }
            : { x: bounds.x + bounds.w, y: bounds.y + bounds.h / 2 };
    return { point, side, bounds };
  };
  const from = Math.floor(random() * nodes.length);
  const to = (from + 1 + Math.floor(random() * (nodes.length - 1))) % nodes.length;
  const source = nodes[from];
  const target = nodes[to];
  if (source === undefined || target === undefined) {
    throw new Error('laidOutDiagram: node index out of range');
  }
  return { start: endpoint(source), end: endpoint(target), obstacles: nodes.filter((_, index) => index !== from && index !== to) };
}

describe('routeOrthogonal work budget', () => {
  // A single blocker between the endpoints: a real detour exists and needs a handful of expansions to find.
  const start = at(100, 25, 'right');
  const end = at(400, 25, 'left');
  const blocker = rect(220, -20, 60, 90);

  it('finds the real route when it fits in the budget', () => {
    const route = routeOrthogonal(start, end, [blocker]);
    expect(route.status).toBe('routed');
    expect(route.reason).toBeUndefined();
    expect(crossesInterior(route.points, blocker)).toBe(false);
  });

  it('returns the marked budget fallback, not a route and not "no route", when the budget runs out', () => {
    const route = routeOrthogonal(start, end, [blocker], { budget: 1 });
    expect(route.status).toBe('fallback');
    expect(route.reason).toBe(BUDGET_EXHAUSTED_REASON);
    expect(route.reason).not.toBe(NO_ROUTE_REASON);
    expect(isOrthogonal(route.points)).toBe(true);
    // The fallback elbow is the straight-through shape the real route exists to avoid.
    expect(crossesInterior(route.points, blocker)).toBe(true);
  });

  it('still says "no route" when the search proves there is none inside the budget', () => {
    const route = routeOrthogonal(at(100, 25, 'right'), at(300, 25, 'left'), [rect(100, 0, 50, 50)]);
    expect(route.status).toBe('fallback');
    expect(route.reason).toBe(NO_ROUTE_REASON);
  });

  it('widens the corridor to find a detour that leaves the first one', () => {
    // A wall far taller than the first corridor (the endpoints' box grown by CORRIDOR_MARGIN) forces the only route
    // out beyond it.
    const wall = rect(200, -400, 40, 800);
    const route = routeOrthogonal(start, end, [wall]);
    expect(route.status).toBe('routed');
    expect(crossesInterior(route.points, wall)).toBe(false);
    const topmost = Math.min(...route.points.map((point) => point.y));
    const bottommost = Math.max(...route.points.map((point) => point.y));
    expect(topmost < start.bounds.y - CORRIDOR_MARGIN || bottommost > start.bounds.y + start.bounds.h + CORRIDOR_MARGIN).toBe(true);
  });

  it('does not settle for a corridor route when a cheaper one leaves the corridor', () => {
    // The first corridor spans y -129..129 around these endpoints. Inside it, wall A leaves a gap only at the bottom and
    // wall B only at the top, so the route there is a six-bend snake costing about 976; just outside it, a four-bend
    // pass over or under both walls costs about 770.
    const from = at(0, 0, 'right');
    const to = at(400, 0, 'left');
    const walls = [rect(150, -129, 20, 229), rect(250, -100, 20, 229)];
    const route = routeOrthogonal(from, to, walls);
    const whole = routeOrthogonal(from, to, walls, { corridorMargin: Infinity });
    expect(route.status).toBe('routed');
    expect(whole.status).toBe('routed');
    expect(walls.some((wall) => crossesInterior(route.points, wall))).toBe(false);
    expect(routeCost(route.points)).toBe(routeCost(whole.points));
    const ys = route.points.map((point) => point.y);
    expect(Math.min(...ys) < -129 || Math.max(...ys) > 129).toBe(true);
  });

  it('routes as cheaply as a search of the whole grid across 150 laid-out diagrams of 20 to 60 nodes', () => {
    for (let seed = 1; seed <= 150; seed += 1) {
      const { start: from, end: to, obstacles } = laidOutDiagram(seed);
      const bounded = routeOrthogonal(from, to, obstacles);
      const whole = routeOrthogonal(from, to, obstacles, { budget: Number.MAX_SAFE_INTEGER, corridorMargin: Infinity });
      expect({ seed, status: bounded.status }).toEqual({ seed, status: whole.status });
      expect({ seed, cost: routeCost(bounded.points) }).toEqual({ seed, cost: routeCost(whole.points) });
      if (bounded.status === 'routed') {
        expect(isOrthogonal(bounded.points)).toBe(true);
        expect(obstacles.some((obstacle) => crossesInterior(bounded.points, obstacle))).toBe(false);
      }
    }
  });
});

describe('routeOrthogonal among 2,000 jittered nodes', () => {
  const nodes = jitteredNodes();

  it('is a worst case: no two nodes share a left or right edge', () => {
    const edges = new Set(nodes.flatMap((node) => [node.x, node.x + node.w]));
    expect(nodes.length).toBe(JITTER_COLUMNS * JITTER_ROWS);
    expect(edges.size).toBe(nodes.length * 2);
  });

  it('routes a connector across ten columns and ten rows for real, clear of every other node', () => {
    const { start: from, end: to, obstacles } = jitteredRoute(nodes, { col: 10, row: 10 }, { col: 20, row: 20 });
    const route = routeOrthogonal(from, to, obstacles);
    expect(route.status).toBe('routed');
    expect(isOrthogonal(route.points)).toBe(true);
    expect(route.points[0]).toEqual(from.point);
    expect(route.points[route.points.length - 1]).toEqual(to.point);
    expect(obstacles.filter((obstacle) => crossesInterior(route.points, obstacle))).toEqual([]);
  });

  it('bounds a route across the whole layout with the budget fallback instead of exhausting the grid', () => {
    const { start: from, end: to, obstacles } = jitteredRoute(nodes, { col: 0, row: 0 }, { col: 49, row: 39 });
    const route = routeOrthogonal(from, to, obstacles);
    expect(route.status).toBe('fallback');
    expect(route.reason).toBe(BUDGET_EXHAUSTED_REASON);
  });
});
