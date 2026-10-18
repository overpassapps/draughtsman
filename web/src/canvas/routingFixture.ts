/**
 * Seeded worst-case layout for the connector router's perf test (routing.perf.test.ts). Committed as a generator
 * rather than a JSON fixture: the layout is a pure function of the seed, so the test and a bench run always see the
 * same obstacles without a large file in the repository.
 *
 * It is the item 29 scale (2,000 nodes, the 50 x 40 grid of docs/examples/perf-2000n-2500e.json) with every node
 * jittered by a fractional offset and a fractional size change, so no two nodes share an edge coordinate: the Hanan
 * grid gets its maximum of distinct rails, the case that exhausted the unbounded search.
 */

import type { Rect } from './geometry.ts';
import type { RouteEndpoint } from './routing.ts';

export const JITTER_SEED = 0x0b57ac1e;
export const JITTER_COLUMNS = 50;
export const JITTER_ROWS = 40;
const CELL_W = 220;
const CELL_H = 120;
const NODE_W = 160;
const NODE_H = 64;
/** Largest offset from the cell origin, and largest size change: enough to stagger every rail, never enough to overlap. */
const MAX_SHIFT = 24;
const MAX_GROW = 16;

/** mulberry32: the same tiny deterministic PRNG as scripts/generate-benchmark.ts. */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** The 2,000 jittered node bounds, row by row. Index `row * JITTER_COLUMNS + col`. */
export function jitteredNodes(seed = JITTER_SEED): Rect[] {
  const rng = mulberry32(seed);
  const nodes: Rect[] = [];
  for (let row = 0; row < JITTER_ROWS; row += 1) {
    for (let col = 0; col < JITTER_COLUMNS; col += 1) {
      nodes.push({
        x: col * CELL_W + rng() * MAX_SHIFT,
        y: row * CELL_H + rng() * MAX_SHIFT,
        w: NODE_W + rng() * MAX_GROW,
        h: NODE_H + rng() * MAX_GROW,
      });
    }
  }
  return nodes;
}

/** A route between two of the nodes, from the middle of `from`'s right side to the middle of `to`'s left side. */
export function jitteredRoute(
  nodes: readonly Rect[],
  from: { col: number; row: number },
  to: { col: number; row: number },
): { start: RouteEndpoint; end: RouteEndpoint; obstacles: Rect[] } {
  const fromIndex = from.row * JITTER_COLUMNS + from.col;
  const toIndex = to.row * JITTER_COLUMNS + to.col;
  const source = nodes[fromIndex];
  const target = nodes[toIndex];
  if (source === undefined || target === undefined) {
    throw new Error('jitteredRoute: node index out of range');
  }
  return {
    start: { point: { x: source.x + source.w, y: source.y + source.h / 2 }, side: 'right', bounds: source },
    end: { point: { x: target.x, y: target.y + target.h / 2 }, side: 'left', bounds: target },
    obstacles: nodes.filter((_, index) => index !== fromIndex && index !== toIndex),
  };
}
