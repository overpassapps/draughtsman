/**
 * Orthogonal connector routing. A pure function over canvas units: two endpoints (a point plus the side it leaves
 * its node from, plus its bounds) and the obstacles between them, producing a polyline of axis-aligned segments.
 * No DOM, no React, no waypoint ownership; the canvas reaches it through the routing worker (edgeRoutes.ts).
 *
 * Every produced segment is horizontal or vertical. For a `status: 'routed'` result, no segment crosses the interior
 * of any unrelated obstacle; a fallback may cross obstacles and does not guarantee the endpoint directions.
 * Obstacles are the bounds of every node *other than the two the edge is attached to*; the attached pair arrive as
 * each endpoint's `bounds` instead, and block a routed path like any other
 * node except along their own port's lane (the straight ray from the port outward along its side), so a route leaves
 * its source and enters its target through the port and never cuts across either body. Node rotation is
 * ignored for now: an obstacle is treated as its axis-aligned box, which is a deliberate simplification recorded here
 * rather than silently assumed.
 *
 * Routing keeps a clearance gap around every obstacle (ROUTE_CLEARANCE) and hugs the *inflated* box, so the search
 * also keeps the real node at arm's length and stays well clear of the "crosses the interior" line. A node sitting
 * closer than that gap to a port it does not own keeps its real bounds instead, so the port stays reachable. The grid the
 * search walks is the set of inflated obstacle edges plus the two endpoints (the Hanan grid), which is complete for
 * axis-aligned rectangles: whenever an obstacle-free route exists within this grid, one is returned. When none does,
 * the function returns a clearly marked fallback elbow instead of throwing.
 *
 * Work is bounded. With every obstacle's edges as rails, a large document with no two nodes aligned puts thousands of
 * rails through even a short route's neighbourhood, and the search exhausted that grid: about 96 s for one route among
 * 2,000 jittered nodes, and across the whole layout it overflowed a Map and threw. So the search starts in a corridor: the endpoints' bounding box grown by CORRIDOR_MARGIN,
 * with only the obstacles that reach into it contributing rails. A route found there is accepted only when it is
 * provably as cheap as any route in the whole grid: leaving the corridor through a side it did not clamp costs more
 * than the Manhattan distance plus twice the margin, so a corridor route at or under that bound cannot be beaten
 * outside, and the search abandons a corridor as soon as every route left in it would cost more than that. Otherwise
 * the margin quadruples and the search runs again, until the corridor covers the whole content (the unbounded grid,
 * as before). The search also counts a lower bound on the bends still needed in its estimate, which keeps it exact
 * while it stops fanning out over equally long staircases. Among routes of equal cost the one returned can differ from
 * the unbounded search's, since the corridor has fewer rails to bend on; the cost itself never rises, except in the
 * degenerate case of two ports a few units apart, whose cheapest hook bends on whatever unrelated rail happens to fall
 * between them. Every stage draws on one shared count of expanded search states
 * (DEFAULT_ROUTE_BUDGET); when it runs out the result is the same marked fallback elbow, with a reason that says the
 * budget ran out rather than that no route exists. The budget is a count, not a clock, so the result is deterministic.
 */

import type { Point } from '../model/types.ts';
import { padRect, type Rect } from './geometry.ts';

/** Which edge of a node an endpoint leaves from. */
export type Side = 'top' | 'right' | 'bottom' | 'left';

/** One end of a connector: where it sits, which side of its node it leaves, and the bounds of that node. */
export interface RouteEndpoint {
  point: Point;
  side: Side;
  bounds: Rect;
}

/** An attached node's clearance box, passable only along the lanes of the endpoints it owns. */
interface AttachedObstacle {
  rect: Rect;
  lanes: RouteEndpoint[];
}

/** How a route ended: found a clear path, or fell back to a straight elbow because none existed. */
export interface OrthogonalRoute {
  status: 'routed' | 'fallback';
  points: Point[];
  /** Present only on a fallback, to say why no obstacle-free route was found. */
  reason?: string;
}

/** Gap left between a route and every obstacle it detours around. */
export const ROUTE_CLEARANCE = 12;

/** Length penalty per bend: the search minimises path length plus this cost for each bend. */
export const BEND_COST = 24;

/** Distance a fallback elbow steps outward from each endpoint before turning. */
const FALLBACK_STUB = 24;

/** How far the first search corridor extends beyond the endpoints' bounding box; each retry quadruples it. */
export const CORRIDOR_MARGIN = 96;

/**
 * Expanded search states allowed across every corridor stage of one route before it falls back. Sized so the worst
 * case measured (a corner-to-corner route across 2,000 jittered nodes, routingFixture.ts) stays near 10 ms, while a
 * route in a laid-out diagram of 20 to 60 nodes needs a few thousand at most; routing.perf.test.ts has the numbers.
 */
export const DEFAULT_ROUTE_BUDGET = 8000;

/** The two ways a route can fail to find a clear path; `reason` on a fallback is one of these. */
export const NO_ROUTE_REASON = 'no obstacle-free orthogonal route exists';
export const BUDGET_EXHAUSTED_REASON = 'the routing budget ran out before a route was found';

/** Tuning for one call. Omitted fields take the defaults above. */
export interface RouteOptions {
  /** Expanded search states allowed before returning the budget fallback. */
  budget?: number;
  /** First corridor margin; `Infinity` searches the whole grid straight away. */
  corridorMargin?: number;
}

// Directions as integers: 0 right (+x), 1 down (+y, canvas y grows downward), 2 left (-x), 3 up (-y).
function outwardDir(side: Side): number {
  switch (side) {
    case 'right':
      return 0;
    case 'bottom':
      return 1;
    case 'left':
      return 2;
    case 'top':
      return 3;
  }
}

function oppositeDir(dir: number): number {
  return dir ^ 2;
}

/** Grid index steps along x and y for a direction. 0 right, 1 down, 2 left, 3 up. */
function stepX(dir: number): number {
  return dir === 0 ? 1 : dir === 2 ? -1 : 0;
}

function stepY(dir: number): number {
  return dir === 1 ? 1 : dir === 3 ? -1 : 0;
}

/**
 * Whether an axis-aligned segment crosses the *interior* of a rectangle. Touching an edge or corner, or running along
 * an edge, is not crossing: the strict inequalities leave the boundary itself passable so routes may legally hug it.
 */
function segmentCrossesInterior(a: Point, b: Point, obstacle: Rect): boolean {
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
}

/** Whether a point lies on an endpoint's lane: the ray from its port outward along its side, port included. */
function onLane(p: Point, endpoint: RouteEndpoint): boolean {
  const { x, y } = endpoint.point;
  switch (endpoint.side) {
    case 'right':
      return p.y === y && p.x >= x;
    case 'bottom':
      return p.x === x && p.y >= y;
    case 'left':
      return p.y === y && p.x <= x;
    case 'top':
      return p.x === x && p.y <= y;
  }
}

function strictlyInside(p: Point, rect: Rect): boolean {
  return rect.x < p.x && p.x < rect.x + rect.w && rect.y < p.y && p.y < rect.y + rect.h;
}

/** A node's clearance box, or its real bounds when the padding would swallow a port the node does not own. */
function clearanceBox(bounds: Rect, foreignPorts: readonly Point[]): Rect {
  const padded = padRect(bounds, ROUTE_CLEARANCE);
  return foreignPorts.some((port) => strictlyInside(port, padded)) ? bounds : padded;
}

function sameRect(a: Rect, b: Rect): boolean {
  return a.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h;
}

function uniqueSorted(values: number[]): number[] {
  const result: number[] = [];
  let previous: number | undefined;
  for (const value of [...values].sort((a, b) => a - b)) {
    if (previous === undefined || value !== previous) {
      result.push(value);
    }
    previous = value;
  }
  return result;
}

/**
 * A uniform-grid spatial filter: buckets each obstacle into every cell its inflated box overlaps, so a segment query
 * only tests the obstacles genuinely near it rather than all of them. Cells are a dense array over the obstacles'
 * extent and a query visits them without collecting or de-duplicating: every question asked of it (does anything
 * cross this segment, where is the first thing along this ray) gives the same answer when an obstacle is seen twice.
 */
class ObstacleGrid {
  private readonly cells: Rect[][] = [];
  private readonly cellSize: number;
  private readonly originCx: number;
  private readonly originCy: number;
  private readonly columns: number;
  private readonly rows: number;

  constructor(rects: readonly Rect[]) {
    let cellSize = chooseCellSize(rects);
    const extent = extentOf(rects, []);
    // A lone far-off obstacle must not turn the dense array into millions of empty cells: coarsen until it is at most
    // a few cells per obstacle.
    const cellLimit = rects.length * 4 + 64;
    const span = (low: number, high: number, size: number): number =>
      rects.length === 0 ? 0 : Math.floor(high / size) - Math.floor(low / size) + 1;
    while (span(extent.minX, extent.maxX, cellSize) * span(extent.minY, extent.maxY, cellSize) > cellLimit) {
      cellSize *= 2;
    }
    this.cellSize = cellSize;
    this.originCx = rects.length === 0 ? 0 : Math.floor(extent.minX / cellSize);
    this.originCy = rects.length === 0 ? 0 : Math.floor(extent.minY / cellSize);
    this.columns = span(extent.minX, extent.maxX, cellSize);
    this.rows = span(extent.minY, extent.maxY, cellSize);
    for (let i = 0; i < this.columns * this.rows; i += 1) {
      this.cells.push([]);
    }
    for (const rect of rects) {
      const minCx = this.column(rect.x);
      const maxCx = this.column(rect.x + rect.w);
      const minCy = this.row(rect.y);
      const maxCy = this.row(rect.y + rect.h);
      for (let cx = minCx; cx <= maxCx; cx += 1) {
        for (let cy = minCy; cy <= maxCy; cy += 1) {
          this.cells[cx * this.rows + cy]?.push(rect);
        }
      }
    }
  }

  /** Cell column of an x coordinate, relative to the array; may fall outside it. */
  private column(x: number): number {
    return Math.floor(x / this.cellSize) - this.originCx;
  }

  private row(y: number): number {
    return Math.floor(y / this.cellSize) - this.originCy;
  }

  private cell(cx: number, cy: number): readonly Rect[] | undefined {
    if (cx < 0 || cx >= this.columns || cy < 0 || cy >= this.rows) {
      return undefined;
    }
    return this.cells[cx * this.rows + cy];
  }

  /** Whether any obstacle's interior is crossed by the axis-aligned segment from `a` to `b`. */
  crosses(a: Point, b: Point): boolean {
    const minCx = Math.max(0, this.column(Math.min(a.x, b.x)));
    const maxCx = Math.min(this.columns - 1, this.column(Math.max(a.x, b.x)));
    const minCy = Math.max(0, this.row(Math.min(a.y, b.y)));
    const maxCy = Math.min(this.rows - 1, this.row(Math.max(a.y, b.y)));
    for (let cx = minCx; cx <= maxCx; cx += 1) {
      for (let cy = minCy; cy <= maxCy; cy += 1) {
        for (const rect of this.cell(cx, cy) ?? []) {
          if (segmentCrossesInterior(a, b, rect)) {
            return true;
          }
        }
      }
    }
    return false;
  }

  /**
   * How far a straight run from (x, y) heading `dir` can go before the first obstacle whose interior spans its line,
   * capped at `limit` (the grid edge in that direction). Walks cells outward from the start and stops at the first
   * cell past the nearest hit, so a run into open space costs its length in cells and a blocked one costs a few.
   */
  reach(x: number, y: number, dir: number, limit: number): number {
    const horizontal = dir === 0 || dir === 2;
    const forward = dir === 0 || dir === 1;
    const along = horizontal ? x : y;
    const lane = horizontal ? this.row(y) : this.column(x);
    const cellCount = horizontal ? this.columns : this.rows;
    if (lane < 0 || lane >= (horizontal ? this.rows : this.columns)) {
      return limit;
    }
    const step = forward ? 1 : -1;
    const from = horizontal ? this.column(x) : this.row(y);
    let reach = limit;
    for (let c = forward ? Math.max(0, from) : Math.min(cellCount - 1, from); c >= 0 && c < cellCount; c += step) {
      const edge = horizontal ? this.column(reach) : this.row(reach);
      if (forward ? c > edge : c < edge) {
        break;
      }
      for (const rect of (horizontal ? this.cell(c, lane) : this.cell(lane, c)) ?? []) {
        const spans = horizontal ? rect.y < y && y < rect.y + rect.h : rect.x < x && x < rect.x + rect.w;
        if (!spans) {
          continue;
        }
        const low = horizontal ? rect.x : rect.y;
        const high = low + (horizontal ? rect.w : rect.h);
        if (forward) {
          if (low >= along && low < reach) {
            reach = low;
          }
        } else if (high <= along && high > reach) {
          reach = high;
        }
      }
    }
    return reach;
  }
}

/** Cell size is the median obstacle extent so the buckets are neither one huge list nor thousands of near-empty cells. */
function chooseCellSize(rects: readonly Rect[]): number {
  if (rects.length === 0) {
    return 64;
  }
  const extents = rects.map((rect) => Math.max(rect.w, rect.h)).sort((a, b) => a - b);
  const median = extents[Math.floor(extents.length / 2)] ?? 64;
  return Math.min(1024, Math.max(16, median));
}

function indexOf(sorted: number[], value: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    const found = sorted[mid];
    if (found === undefined) {
      return -1;
    }
    if (found === value) {
      return mid;
    }
    if (found < value) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return -1;
}

function samePoint(a: Point, b: Point): boolean {
  return a.x === b.x && a.y === b.y;
}

function areCollinear(a: Point, b: Point, c: Point): boolean {
  return (a.x === b.x && b.x === c.x) || (a.y === b.y && b.y === c.y);
}

/** Drops any point that sits on a straight line between its neighbours, leaving one vertex per bend. */
function collapseCollinear(points: Point[]): Point[] {
  const result: Point[] = [];
  for (const point of points) {
    while (result.length >= 2) {
      const previous = result[result.length - 1];
      const before = result[result.length - 2];
      if (previous === undefined || before === undefined || !areCollinear(before, previous, point)) {
        break;
      }
      result.pop();
    }
    result.push(point);
  }
  return result;
}

/** The fallback elbow before collinear points are collapsed; see the module contract for its limitations. */
function fallbackElbow(start: RouteEndpoint, end: RouteEndpoint): Point[] {
  const outDir = outwardDir(start.side);
  const approachDir = oppositeDir(outwardDir(end.side));
  const first = { x: start.point.x + stepX(outDir) * FALLBACK_STUB, y: start.point.y + stepY(outDir) * FALLBACK_STUB };
  const beforeEnd = { x: end.point.x + stepX(approachDir) * FALLBACK_STUB, y: end.point.y + stepY(approachDir) * FALLBACK_STUB };

  const points: Point[] = [start.point];
  if (!samePoint(first, start.point)) {
    points.push(first);
  }
  if (!samePoint(beforeEnd, points[points.length - 1] ?? start.point)) {
    // Connect the outgoing stub to the incoming one with a single right-angle corner, mated to whichever axis the
    // first step moved along.
    const corner = stepX(outDir) !== 0 ? { x: beforeEnd.x, y: first.y } : { x: first.x, y: beforeEnd.y };
    if (!samePoint(corner, points[points.length - 1] ?? start.point)) {
      points.push(corner);
    }
    if (!samePoint(corner, beforeEnd)) {
      points.push(beforeEnd);
    }
  }
  if (!samePoint(end.point, points[points.length - 1] ?? start.point)) {
    points.push(end.point);
  }
  return points;
}

/**
 * Routes an orthogonal connector between two endpoints around every obstacle. See the module comment for the
 * contract, including endpoint bounds, obstacle clearance and fallback limitations.
 */
export function routeOrthogonal(
  start: RouteEndpoint,
  end: RouteEndpoint,
  obstacles: readonly Rect[],
  options: RouteOptions = {},
): OrthogonalRoute {
  if (samePoint(start.point, end.point)) {
    return { status: 'fallback', points: [start.point, end.point], reason: 'the two endpoints coincide' };
  }

  const ports = [start.point, end.point];
  const inflated = obstacles.map((obstacle) => clearanceBox(obstacle, ports));
  const attached: AttachedObstacle[] = sameRect(start.bounds, end.bounds)
    ? [{ rect: clearanceBox(start.bounds, []), lanes: [start, end] }]
    : [
        { rect: clearanceBox(start.bounds, [end.point]), lanes: [start] },
        { rect: clearanceBox(end.bounds, [start.point]), lanes: [end] },
      ];
  const attachedBoxes = attached.map((obstacle) => obstacle.rect);

  // The whole grid is the obstacle edges plus the endpoints, with one extra clearance-wide lane around the whole
  // content so there is always a perimeter to route along when a path has to go the long way round. That lane is the
  // router's notion of "within canvas bounds": no search leaks off toward infinity. Every corridor is clipped to it.
  const content = extentOf([...inflated, ...attachedBoxes], ports);
  const perimeter: Extent = {
    minX: content.minX - ROUTE_CLEARANCE,
    minY: content.minY - ROUTE_CLEARANCE,
    maxX: content.maxX + ROUTE_CLEARANCE,
    maxY: content.maxY + ROUTE_CLEARANCE,
  };
  const endpointsBox = extentOf(attachedBoxes, ports);
  const manhattan = Math.abs(end.point.x - start.point.x) + Math.abs(end.point.y - start.point.y);

  const budget = { remaining: options.budget ?? DEFAULT_ROUTE_BUDGET };
  let margin = Math.max(0, options.corridorMargin ?? CORRIDOR_MARGIN);
  for (;;) {
    const corridor: Extent = {
      minX: Math.max(endpointsBox.minX - margin, perimeter.minX),
      minY: Math.max(endpointsBox.minY - margin, perimeter.minY),
      maxX: Math.min(endpointsBox.maxX + margin, perimeter.maxX),
      maxY: Math.min(endpointsBox.maxY + margin, perimeter.maxY),
    };
    const whole =
      corridor.minX === perimeter.minX &&
      corridor.minY === perimeter.minY &&
      corridor.maxX === perimeter.maxX &&
      corridor.maxY === perimeter.maxY;

    const inside = whole ? inflated : inflated.filter((rect) => reachesInto(rect, corridor));
    const xs = railsWithin(inside, attachedBoxes, [start.point.x, end.point.x], corridor.minX, corridor.maxX, 'x');
    const ys = railsWithin(inside, attachedBoxes, [start.point.y, end.point.y], corridor.minY, corridor.maxY, 'y');

    // A route that leaves the corridor has to travel more than `margin` beyond the endpoints' box on a side the
    // perimeter did not clip, and come back, so it costs more than this bound. On or under it, nothing outside wins;
    // the search gives up on the corridor as soon as every route it has left to find would cost more.
    const costLimit = whole ? Infinity : manhattan + 2 * margin;
    const outcome = search(start, end, new ObstacleGrid(inside), attached, xs, ys, budget, costLimit);
    if (outcome === 'exhausted') {
      return { status: 'fallback', points: collapseCollinear(fallbackElbow(start, end)), reason: BUDGET_EXHAUSTED_REASON };
    }
    if (outcome !== undefined) {
      return { status: 'routed', points: outcome.points };
    }
    if (whole) {
      return { status: 'fallback', points: collapseCollinear(fallbackElbow(start, end)), reason: NO_ROUTE_REASON };
    }
    margin = margin > 0 ? margin * 4 : CORRIDOR_MARGIN;
  }
}

/** A box as its four edges, so a corridor grown by an infinite margin stays well defined. */
interface Extent {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** Extent of some rectangles and points; there is always at least one point, so it is never empty. */
function extentOf(rects: readonly Rect[], points: readonly Point[]): Extent {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const rect of rects) {
    minX = Math.min(minX, rect.x);
    minY = Math.min(minY, rect.y);
    maxX = Math.max(maxX, rect.x + rect.w);
    maxY = Math.max(maxY, rect.y + rect.h);
  }
  for (const point of points) {
    minX = Math.min(minX, point.x);
    minY = Math.min(minY, point.y);
    maxX = Math.max(maxX, point.x);
    maxY = Math.max(maxY, point.y);
  }
  return { minX, minY, maxX, maxY };
}

/** Whether a rectangle's interior overlaps the corridor: one that only touches its edge cannot block a path inside. */
function reachesInto(rect: Rect, corridor: Extent): boolean {
  return rect.x < corridor.maxX && rect.x + rect.w > corridor.minX && rect.y < corridor.maxY && rect.y + rect.h > corridor.minY;
}

/**
 * One axis of a corridor's grid: the edges of the obstacles reaching into it and of the attached nodes, the endpoint
 * coordinates, and the corridor's own two edges as its perimeter lane. Edges beyond the corridor are dropped, so the
 * search can never step outside it.
 */
function railsWithin(
  inside: readonly Rect[],
  attachedBoxes: readonly Rect[],
  endpoints: readonly number[],
  low: number,
  high: number,
  axis: 'x' | 'y',
): number[] {
  const values = [...endpoints, low, high];
  for (const rect of [...inside, ...attachedBoxes]) {
    const near = axis === 'x' ? rect.x : rect.y;
    const far = near + (axis === 'x' ? rect.w : rect.h);
    if (near >= low && near <= high) {
      values.push(near);
    }
    if (far >= low && far <= high) {
      values.push(far);
    }
  }
  return uniqueSorted(values);
}

interface HeapEntry {
  key: number;
  g: number;
  f: number;
}

/** A* tie-break: on equal `f`, expand the entry that has travelled further (larger `g`), so open space is crossed in
 * one straight run instead of spreading across every cell that shares the same Manhattan estimate. */
function outranks(a: HeapEntry, b: HeapEntry): boolean {
  return a.f < b.f || (a.f === b.f && a.g > b.g);
}

class MinHeap {
  private readonly items: HeapEntry[] = [];

  push(entry: HeapEntry): void {
    const items = this.items;
    items.push(entry);
    let i = items.length - 1;
    while (i > 0) {
      const parent = (i - 1) >> 1;
      const parentEntry = items[parent];
      const current = items[i];
      if (parentEntry === undefined || current === undefined || !outranks(current, parentEntry)) {
        break;
      }
      items[parent] = current;
      items[i] = parentEntry;
      i = parent;
    }
  }

  pop(): HeapEntry | undefined {
    const items = this.items;
    const first = items[0];
    if (first === undefined) {
      return undefined;
    }
    const last = items.pop();
    if (items.length > 0 && last !== undefined) {
      items[0] = last;
      let i = 0;
      for (;;) {
        const left = i * 2 + 1;
        const right = left + 1;
        let smallest = i;
        const current = items[i];
        if (current === undefined) {
          break;
        }
        const leftEntry = items[left];
        if (leftEntry !== undefined && outranks(leftEntry, current)) {
          smallest = left;
        }
        const smallestEntry = items[smallest];
        const rightEntry = items[right];
        if (smallestEntry !== undefined && rightEntry !== undefined && outranks(rightEntry, smallestEntry)) {
          smallest = right;
        }
        if (smallest === i) {
          break;
        }
        const a = items[i];
        const b = items[smallest];
        if (a === undefined || b === undefined) {
          break;
        }
        items[i] = b;
        items[smallest] = a;
        i = smallest;
      }
    }
    return first;
  }
}

function coordinate(value: number | undefined): number {
  if (value === undefined) {
    // Reaching here is a bug in the search bookkeeping, not an input condition: indices are always produced inside
    // the rail arrays' bounds. Kept as a throw so a miscount fails loudly instead of silently drawing a wrong route.
    throw new Error('Draughtsman router: grid index out of range');
  }
  return value;
}

/**
 * The grid node just before the first obstacle in `dir`, i.e. the farthest rail a straight run can reach before it
 * would have to turn. This is the long jump that lets A* cross open space in one segment instead of one rail at a
 * time. The caller still verifies the segment with `clear`, so a candidate that overreaches (for example past a
 * straddling obstacle) is dropped harmlessly; the adjacent step remains for the thin gaps this jump cannot see.
 * Returns the flattened node index `ix * ysLength + iy`, or -1 when the reach rail has no grid index.
 */
function farNode(
  ix: number,
  iy: number,
  dir: number,
  xs: number[],
  ys: number[],
  ysLength: number,
  grid: ObstacleGrid,
): number {
  const x = coordinate(xs[ix]);
  const y = coordinate(ys[iy]);
  const forward = dir === 0 || dir === 1;

  if (dir === 0 || dir === 2) {
    const limit = forward ? coordinate(xs[xs.length - 1]) : coordinate(xs[0]);
    const reachIx = indexOf(xs, grid.reach(x, y, dir, limit));
    return reachIx < 0 ? -1 : reachIx * ysLength + iy;
  }

  const limit = forward ? coordinate(ys[ys.length - 1]) : coordinate(ys[0]);
  const reachIy = indexOf(ys, grid.reach(x, y, dir, limit));
  return reachIy < 0 ? -1 : ix * ysLength + reachIy;
}

/**
 * A lower bound on the bends still needed to arrive at the target heading `targetDir`, from heading `dir` with the
 * target at offset (dx, dy). Keeping it a lower bound keeps A* exact; counting bends at all stops the search from
 * fanning out over every equally long staircase in a dense grid.
 */
function minBends(dir: number, targetDir: number, dx: number, dy: number): number {
  const ahead = (d: number): number => stepX(d) * dx + stepY(d) * dy;
  if (dir === targetDir) {
    // Straight on into the target, or off the line and back onto it.
    const lateral = stepX(dir) === 0 ? dx : dy;
    return lateral === 0 && ahead(dir) >= 0 ? 0 : 2;
  }
  if (dir === oppositeDir(targetDir)) {
    return 2;
  }
  // Perpendicular: one bend when the target is still ahead on this heading and then ahead on the target heading.
  return ahead(dir) >= 0 && ahead(targetDir) > 0 ? 1 : 2;
}

/** Where a corridor search ended: a route, no route inside the corridor within its cost limit, or the budget ran out. */
type SearchOutcome = { points: Point[] } | undefined | 'exhausted';

function search(
  start: RouteEndpoint,
  end: RouteEndpoint,
  grid: ObstacleGrid,
  attached: readonly AttachedObstacle[],
  xs: number[],
  ys: number[],
  budget: { remaining: number },
  costLimit: number,
): SearchOutcome {
  const ysLength = ys.length;
  const startIx = indexOf(xs, start.point.x);
  const startIy = indexOf(ys, start.point.y);
  const targetIx = indexOf(xs, end.point.x);
  const targetIy = indexOf(ys, end.point.y);
  if (startIx < 0 || startIy < 0 || targetIx < 0 || targetIy < 0) {
    return undefined;
  }

  const targetDir = oppositeDir(outwardDir(end.side));
  const targetNode = targetIx * ysLength + targetIy;
  const targetX = end.point.x;
  const targetY = end.point.y;

  const encode = (ix: number, iy: number, dir: number): number => (ix * ysLength + iy) * 4 + dir;
  const estimate = (x: number, y: number, dir: number): number =>
    Math.abs(x - targetX) + Math.abs(y - targetY) + BEND_COST * minBends(dir, targetDir, targetX - x, targetY - y);

  const clear = (a: Point, b: Point): boolean => {
    if (grid.crosses(a, b)) {
      return false;
    }
    return attached.every(
      ({ rect, lanes }) => !segmentCrossesInterior(a, b, rect) || lanes.some((lane) => onLane(a, lane) && onLane(b, lane)),
    );
  };

  const endOut = outwardDir(end.side);
  const approachIx = targetIx + stepX(endOut);
  const approachIy = targetIy + stepY(endOut);
  if (
    !inBounds(approachIx, xs.length) ||
    !inBounds(approachIy, ysLength) ||
    !clear({ x: coordinate(xs[approachIx]), y: coordinate(ys[approachIy]) }, end.point)
  ) {
    return undefined;
  }

  const gScore = new Map<number, number>();
  const cameFrom = new Map<number, number>();
  const heap = new MinHeap();

  /** Enqueue a clear straight edge (adjacent or long-jump) from `from` to grid node (nx, ny) heading `nd`. */
  const relax = (parentKey: number | undefined, nx: number, ny: number, nd: number, fromG: number, from: Point, incomingDir: number): void => {
    const there = { x: coordinate(xs[nx]), y: coordinate(ys[ny]) };
    if (!clear(from, there)) {
      return;
    }
    const stepLength = Math.abs(there.x - from.x) + Math.abs(there.y - from.y);
    const bend = nd === incomingDir ? 0 : BEND_COST;
    const tentative = fromG + stepLength + bend;
    const nextKey = encode(nx, ny, nd);
    const known = gScore.get(nextKey);
    if (known !== undefined && known <= tentative) {
      return;
    }
    gScore.set(nextKey, tentative);
    if (parentKey !== undefined) {
      cameFrom.set(nextKey, parentKey);
    }
    heap.push({ key: nextKey, g: tentative, f: tentative + estimate(there.x, there.y, nd) });
  };

  // The first move is pinned to the start side's outward direction; seed the search along it so the route always
  // leaves the source going outward rather than sliding sideways along its own boundary.
  const startDir = outwardDir(start.side);
  const startPoint = { x: coordinate(xs[startIx]), y: coordinate(ys[startIy]) };
  const adjacentIx = startIx + stepX(startDir);
  const adjacentIy = startIy + stepY(startDir);
  if (inBounds(adjacentIx, xs.length) && inBounds(adjacentIy, ys.length)) {
    relax(undefined, adjacentIx, adjacentIy, startDir, 0, startPoint, startDir);
  }
  const startFar = farNode(startIx, startIy, startDir, xs, ys, ysLength, grid);
  if (startFar >= 0 && startFar !== adjacentIx * ysLength + adjacentIy && startFar !== startIx * ysLength + startIy) {
    relax(undefined, (startFar - (startFar % ysLength)) / ysLength, startFar % ysLength, startDir, 0, startPoint, startDir);
  }
  if (gScore.size === 0) {
    return undefined;
  }

  for (;;) {
    const entry = heap.pop();
    if (entry === undefined) {
      return undefined;
    }
    const best = gScore.get(entry.key);
    if (best === undefined || entry.g > best) {
      continue;
    }
    // Entries leave the heap in rising estimate order and the estimate never overstates, so once one is over the
    // limit, every route still to be found here is too.
    if (entry.f > costLimit) {
      return undefined;
    }

    const dir = entry.key % 4;
    const node = (entry.key - dir) / 4;
    const iy = node % ysLength;
    const ix = (node - iy) / ysLength;

    if (node === targetNode && dir === targetDir) {
      return { points: rebuild(startPoint, entry.key, cameFrom, xs, ys, ysLength) };
    }
    if (budget.remaining <= 0) {
      return 'exhausted';
    }
    budget.remaining -= 1;

    const here = { x: coordinate(xs[ix]), y: coordinate(ys[iy]) };
    for (let nextDir = 0; nextDir < 4; nextDir += 1) {
      if (nextDir === oppositeDir(dir)) {
        continue;
      }
      // The one-rail step preserves every corner the long jump below cannot see (a thin gap that needs an early turn).
      const nx = ix + stepX(nextDir);
      const ny = iy + stepY(nextDir);
      if (inBounds(nx, xs.length) && inBounds(ny, ysLength)) {
        relax(entry.key, nx, ny, nextDir, best, here, dir);
      }
      // The long jump to the rail just before the first obstacle crosses open space in one segment.
      const far = farNode(ix, iy, nextDir, xs, ys, ysLength, grid);
      if (far >= 0 && far !== nx * ysLength + ny && far !== ix * ysLength + iy) {
        relax(entry.key, (far - (far % ysLength)) / ysLength, far % ysLength, nextDir, best, here, dir);
      }
    }
  }
}

function inBounds(index: number, length: number): boolean {
  return index >= 0 && index < length;
}

function rebuild(
  startPoint: Point,
  goalKey: number,
  cameFrom: ReadonlyMap<number, number>,
  xs: number[],
  ys: number[],
  ysLength: number,
): Point[] {
  const keys: number[] = [];
  let key: number | undefined = goalKey;
  while (key !== undefined) {
    keys.push(key);
    key = cameFrom.get(key);
  }
  keys.reverse();
  const points = [startPoint, ...keys.map((stateKey) => decodePoint(stateKey, xs, ys, ysLength))];
  return collapseCollinear(points);
}

function decodePoint(key: number, xs: number[], ys: number[], ysLength: number): Point {
  const dir = key % 4;
  const node = (key - dir) / 4;
  const iy = node % ysLength;
  const ix = (node - iy) / ysLength;
  return { x: coordinate(xs[ix]), y: coordinate(ys[iy]) };
}
