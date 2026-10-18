import { loadScale } from '../testing/loadScale.ts';
import { routeOrthogonal } from './routing.ts';
import { jitteredNodes, jitteredRoute } from './routingFixture.ts';

// The connector router's half of the M2 editor checklist, item 29: one route among the 2,000 nodes of a
// jittered layout (routingFixture.ts), where no two nodes share an edge coordinate and the unbounded search took
// about 96 s, or overflowed and threw across the whole layout. The work budget bounds it; this suite reports what a
// route costs and fails only on a gross regression, like DiagramSvg.perf.test.tsx, because a hard 16.7 ms assertion
// would gate on machine noise rather than the code. On the reference machine the worst case below (a corner-to-corner
// route that spends the whole budget and falls back) measured a median of ~9.5 ms, and a ten-by-ten-cell route ~1.5 ms.

/** Item 29's frame budget, kept as the number the checklist names rather than the assertion threshold. */
const FRAME_BUDGET_MS = 1000 / 60;

/** 15x the frame budget, the same gross-regression headroom the render benchmark gives a pan frame. */
const ROUTE_GROSS_MS = FRAME_BUDGET_MS * 15;

const nodes = jitteredNodes();

/** Median time of one route over several runs, after a warm-up so the sample is not the first JIT pass. */
function medianRouteMs(route: () => void): { median: number; max: number } {
  for (let i = 0; i < 3; i += 1) {
    route();
  }
  const samples: number[] = [];
  for (let i = 0; i < 15; i += 1) {
    const start = performance.now();
    route();
    samples.push(performance.now() - start);
  }
  samples.sort((a, b) => a - b);
  return { median: samples[Math.floor(samples.length / 2)] ?? 0, max: samples[samples.length - 1] ?? 0 };
}

describe('connector routing performance (checklist item 29)', () => {
  it('bounds the worst case: a route across the whole jittered layout spends its budget and falls back', () => {
    const { start, end, obstacles } = jitteredRoute(nodes, { col: 0, row: 0 }, { col: 49, row: 39 });
    expect(routeOrthogonal(start, end, obstacles).status).toBe('fallback');
    const { median, max } = medianRouteMs(() => routeOrthogonal(start, end, obstacles));
    console.log(`worst-case route: median ${median.toFixed(2)} ms, max ${max.toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${ROUTE_GROSS_MS.toFixed(2)} ms)`);
    expect(median).toBeLessThan(ROUTE_GROSS_MS * loadScale());
  });

  it('reports a real route across ten columns and ten rows of the jittered layout', () => {
    const { start, end, obstacles } = jitteredRoute(nodes, { col: 10, row: 10 }, { col: 20, row: 20 });
    expect(routeOrthogonal(start, end, obstacles).status).toBe('routed');
    const { median, max } = medianRouteMs(() => routeOrthogonal(start, end, obstacles));
    console.log(`ten-by-ten-cell route: median ${median.toFixed(2)} ms, max ${max.toFixed(2)} ms`);
    expect(median).toBeLessThan(ROUTE_GROSS_MS * loadScale());
  });

  it('reports a route between neighbouring nodes, the common case', () => {
    const { start, end, obstacles } = jitteredRoute(nodes, { col: 25, row: 20 }, { col: 26, row: 21 });
    expect(routeOrthogonal(start, end, obstacles).status).toBe('routed');
    const { median, max } = medianRouteMs(() => routeOrthogonal(start, end, obstacles));
    console.log(`neighbour route: median ${median.toFixed(2)} ms, max ${max.toFixed(2)} ms`);
    expect(median).toBeLessThan(ROUTE_GROSS_MS * loadScale());
  });
});
