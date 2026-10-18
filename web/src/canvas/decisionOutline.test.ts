import decisionFlow from '../../../docs/examples/decision-flow.json';
import type { Document } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';

/**
 * QA-01: a connector leaves a decision diamond on its drawn outline, never on the box around it. The same document
 * is rendered by the server (`SvgRendererTests.Decision_flow_edges_start_on_the_diamond_outline`), which pins the
 * same offsets from the diamond's top vertex, so the two renderers cannot drift apart.
 */
const document = decisionFlow as unknown as Document;
const TOP_VERTEX = { x: 280, y: 40 };
const HALF_W = 80;
const HALF_H = 50;

/** 1 on the diamond's outline, less inside it, more outside. */
function outlineDistance(point: { x: number; y: number }): number {
  return Math.abs(point.x - 280) / HALF_W + Math.abs(point.y - 90) / HALF_H;
}

function startOf(edgeId: string): { x: number; y: number } {
  const start = indexDocument(document).edgePaths.get(edgeId)?.points[0];
  if (start === undefined) {
    throw new Error(`edge ${edgeId} has no drawn path`);
  }
  return start;
}

const EXPECTED: readonly (readonly [string, number, number])[] = [
  // The bottom vertex, then the next two sharers fanned +16 / -16 along the outline (not along the box's side).
  ['e_yes', 0, 100],
  ['e_no', 16, 90],
  ['e_maybe', -16, 90],
  // An orthogonal connector with a routed bend leaves straight towards it: 20 right of centre, on the lower-right edge.
  ['e_bent', 20, 87.5],
  // No port and no bend: the centre-to-target line crosses the outline.
  ['e_audit', 69.8412698, 43.6507937],
];

describe('decision diamond connectors (QA-01)', () => {
  it.each(EXPECTED)('%s leaves the outline at its expected point', (id, dx, dy) => {
    const start = startOf(id);
    expect(start.x).toBeCloseTo(TOP_VERTEX.x + dx, 4);
    expect(start.y).toBeCloseTo(TOP_VERTEX.y + dy, 4);
    expect(outlineDistance(start)).toBeCloseTo(1, 4);
  });

  it('would leave the outline if the nudge slid along the box side instead (the defect this pins)', () => {
    // The old behaviour: a +16 slide along the bottom side at the vertex's height is 16/80 beyond the outline.
    expect(outlineDistance({ x: 296, y: 140 })).toBeGreaterThan(1.05);
    expect(outlineDistance(startOf('e_no'))).toBeLessThan(1.0001);
  });
});
