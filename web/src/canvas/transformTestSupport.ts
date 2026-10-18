/**
 * Test support: applies a transform list to a point, reading both SVG attribute syntax (`translate(10 20) rotate(30 5
 * 5)`) and CSS syntax (`translate(10px, 20px) rotate(30deg)`), so a test can check that an HTML overlay and the SVG
 * it sits on put the same point in the same place without comparing either string to a copy of the other.
 */

import type { Point } from '../model/types.ts';

type Step = (p: Point) => Point;

function parse(transform: string): Step[] {
  const steps: Step[] = [];
  for (const match of transform.matchAll(/(translate|scale|rotate)\(([^)]*)\)/g)) {
    const name = match[1];
    const args = (match[2] ?? '')
      .split(/[\s,]+/)
      .filter((arg) => arg.length > 0)
      .map((arg) => Number.parseFloat(arg));
    const [a = 0, b, c] = args;
    if (name === 'translate') {
      const dy = b ?? 0;
      steps.push((p) => ({ x: p.x + a, y: p.y + dy }));
    } else if (name === 'scale') {
      const sy = b ?? a;
      steps.push((p) => ({ x: p.x * a, y: p.y * sy }));
    } else {
      const cx = b ?? 0;
      const cy = c ?? 0;
      const rad = (a * Math.PI) / 180;
      const cos = Math.cos(rad);
      const sin = Math.sin(rad);
      steps.push((p) => ({ x: cx + (p.x - cx) * cos - (p.y - cy) * sin, y: cy + (p.x - cx) * sin + (p.y - cy) * cos }));
    }
  }
  return steps;
}

const round = (value: number): number => Math.round(value * 1e6) / 1e6 + 0;

/** Applies each transform list in turn, outermost first, as nested SVG groups or a CSS transform list compose. */
export function applyTransform(transforms: string | readonly string[], point: Point): Point {
  const steps = (typeof transforms === 'string' ? [transforms] : transforms).flatMap(parse);
  // The rightmost function applies to the point first.
  const result = steps.reduceRight((p, step) => step(p), point);
  return { x: round(result.x), y: round(result.y) };
}
