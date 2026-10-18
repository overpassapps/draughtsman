/**
 * Smooth curves through a connector's endpoints and waypoints (routing mode `curved`). A Catmull-Rom spline through
 * the points is turned into a cubic Bézier path so the curve passes exactly through every point, including each
 * waypoint the user pinned. The end tangent the spline produces is the direction an arrowhead must face, and SVG's
 * `orient="auto"` reads it straight off the path, so nothing here does arrowhead trigonometry.
 *
 * `curveMidpoint` gives the point half way along the curve by arc length, which is where a curved connector's label
 * sits (labelEditing.ts `edgeLabelAt`); the polyline midpoint would fall off a bent curve.
 *
 * Pure functions over canvas points, unit-testable in the same way geometry.ts is.
 */

import type { Point } from '../model/types.ts';

/** The curve's own tension parameter; 0.5 is the standard Catmull-Rom value (centripetal is 0). */
const TENSION = 0.5;
/** How many straight steps each Bézier segment is sampled in when measuring arc length. */
const ARC_SAMPLES = 32;

/** One cubic Bézier: the two anchors and the two control points between them. */
interface Cubic {
  p0: Point;
  c1: Point;
  c2: Point;
  p3: Point;
}

/**
 * The cubic Bézier of every interior segment through `points`. Each segment's anchors are p1 and p2, its control
 * points the Catmull-Rom tangents at them (the average of the two neighbouring chords scaled by TENSION), so the curve
 * passes exactly through every point.
 */
function cubics(points: readonly Point[]): Cubic[] {
  const out: Cubic[] = [];
  for (let i = 0; i < points.length - 1; i++) {
    const p1 = points[i];
    const p2 = points[i + 1];
    if (p1 === undefined || p2 === undefined) {
      continue;
    }
    const p0 = points[i - 1] ?? p1;
    const p3 = points[i + 2] ?? p2;
    out.push({
      p0: p1,
      c1: { x: p1.x + ((p2.x - p0.x) * TENSION) / 2, y: p1.y + ((p2.y - p0.y) * TENSION) / 2 },
      c2: { x: p2.x - ((p3.x - p1.x) * TENSION) / 2, y: p2.y - ((p3.y - p1.y) * TENSION) / 2 },
      p3: p2,
    });
  }
  return out;
}

/**
 * The SVG path `d` string of a smooth curve through `points`, a cubic Bézier per interior segment. Two points draw a
 * straight line (a curve has no turn to make); one or none draw nothing. The path is a `M ... C ...` chain the caller
 * can attach to a `<path>` with `fill="none"`, so markers orient along each end's tangent.
 */
export function smoothCurvePath(points: readonly Point[]): string {
  if (points.length < 2) {
    return '';
  }
  const first = points[0];
  const last = points[1];
  if (first === undefined || last === undefined) {
    return '';
  }
  const parts: string[] = [`M ${fmt(first.x)},${fmt(first.y)}`];
  if (points.length === 2) {
    return `${parts[0] ?? ''} L ${fmt(last.x)},${fmt(last.y)}`;
  }
  for (const segment of cubics(points)) {
    parts.push(`C ${fmt(segment.c1.x)},${fmt(segment.c1.y)} ${fmt(segment.c2.x)},${fmt(segment.c2.y)} ${fmt(segment.p3.x)},${fmt(segment.p3.y)}`);
  }
  return parts.join(' ');
}

/** A point on a cubic Bézier at parameter `t`, its four points weighed by the Bernstein polynomials. */
function cubicAt(segment: Cubic, t: number): Point {
  const mt = 1 - t;
  const a = mt * mt * mt;
  const b = 3 * mt * mt * t;
  const c = 3 * mt * t * t;
  const d = t * t * t;
  return {
    x: a * segment.p0.x + b * segment.c1.x + c * segment.c2.x + d * segment.p3.x,
    y: a * segment.p0.y + b * segment.c1.y + c * segment.c2.y + d * segment.p3.y,
  };
}

/**
 * The point half way along the curve by arc length. A straight two-point connector returns its segment midpoint, so
 * a label on it does not move. Each Bézier segment is sampled in ARC_SAMPLES straight steps, well under a pixel of
 * error at any length the canvas draws.
 */
export function curveMidpoint(points: readonly Point[]): Point {
  const first = points[0];
  if (first === undefined) {
    return { x: 0, y: 0 };
  }
  const segments = cubics(points);
  if (segments.length === 0) {
    return { x: first.x, y: first.y };
  }
  let total = 0;
  const lengths: number[] = [];
  for (const segment of segments) {
    let length = 0;
    let previous = segment.p0;
    for (let i = 1; i <= ARC_SAMPLES; i++) {
      const at = cubicAt(segment, i / ARC_SAMPLES);
      length += Math.hypot(at.x - previous.x, at.y - previous.y);
      previous = at;
    }
    lengths.push(length);
    total += length;
  }
  if (total === 0) {
    return { x: first.x, y: first.y };
  }
  let remaining = total / 2;
  for (let s = 0; s < segments.length; s++) {
    const segment = segments[s];
    const length = lengths[s];
    if (segment === undefined || length === undefined) {
      continue;
    }
    if (remaining > length && s < segments.length - 1) {
      remaining -= length;
      continue;
    }
    let previous = segment.p0;
    let travelled = 0;
    for (let i = 1; i <= ARC_SAMPLES; i++) {
      const at = cubicAt(segment, i / ARC_SAMPLES);
      const step = Math.hypot(at.x - previous.x, at.y - previous.y);
      if (travelled + step >= remaining) {
        const within = step === 0 ? 0 : (remaining - travelled) / step;
        return cubicAt(segment, (i - 1 + within) / ARC_SAMPLES);
      }
      travelled += step;
      previous = at;
    }
    return cubicAt(segment, 1);
  }
  const last = points[points.length - 1];
  return last === undefined ? { x: first.x, y: first.y } : { x: last.x, y: last.y };
}

/** A number formatted to two decimals, the precision the renderer already emits elsewhere. */
function fmt(value: number): string {
  return String(Math.round(value * 100) / 100);
}
