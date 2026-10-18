/**
 * Segment dragging for connectors (routing decision D4: a route stays derived until the user drags a segment, which
 * pins it with explicit waypoints). Pure functions over canvas points, unit-tested like routing.ts.
 *
 * Orthogonal: a press on a middle segment of a selected orthogonal connector — one with a segment on each side, so the
 * segments leaving and entering the endpoint nodes are never picked — moves that segment perpendicular to its own
 * direction on drag. The interior points of the current route become the waypoints written back (pinning it).
 *
 * Curved: a press on the curve moves the nearest waypoint, or inserts a new one at the press when none is within
 * reach, and a drag carries that waypoint with the pointer. The waypoints are the anchors the smooth curve is drawn
 * through (curve.ts).
 */

import type { Point } from '../model/types.ts';
import { curveMidpoint } from './curve.ts';

/** The interior segments of a drawn route: those with a segment on both sides. Indices into `points`. */
function middleSegments(points: readonly Point[]): { index: number }[] {
  const out: { index: number }[] = [];
  // A middle segment is points[i]..points[i+1] for 1 <= i <= points.length - 3, so points.length must be at least 4.
  for (let i = 1; i <= points.length - 3; i++) {
    out.push({ index: i });
  }
  return out;
}

/** Squared distance from `at` to the segment `a`..`b`. */
function distanceToSegment(at: Point, a: Point, b: Point): number {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const lengthSquared = dx * dx + dy * dy;
  if (lengthSquared <= 0) {
    return (at.x - a.x) * (at.x - a.x) + (at.y - a.y) * (at.y - a.y);
  }
  const t = Math.max(0, Math.min(1, ((at.x - a.x) * dx + (at.y - a.y) * dy) / lengthSquared));
  const ox = at.x - (a.x + dx * t);
  const oy = at.y - (a.y + dy * t);
  return ox * ox + oy * oy;
}

/** The perpendicular delta to a segment's own direction: the part of `delta` normal to the `a`..`b` line. */
function perpendicularDelta(a: Point, b: Point, delta: Point): Point {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const length = Math.hypot(dx, dy);
  if (length <= 0) {
    return delta;
  }
  // Unit normal: (-dy, dx) / length.
  const nx = -dy / length;
  const ny = dx / length;
  const amount = delta.x * nx + delta.y * ny;
  return { x: nx * amount, y: ny * amount };
}

/**
 * The index of the middle segment of an orthogonal route the pointer is on, or undefined when the pointer is out of
 * reach of every middle segment. `reach` is the hit radius in world units (a screen size divided by the zoom).
 */
export function middleSegmentAt(points: readonly Point[], at: Point, reach: number): number | undefined {
  let best: { index: number; distance: number } | undefined;
  for (const { index } of middleSegments(points)) {
    const a = points[index];
    const b = points[index + 1];
    if (a === undefined || b === undefined) {
      continue;
    }
    const distance = distanceToSegment(at, a, b);
    if (distance <= reach * reach && (best === undefined || distance < best.distance)) {
      best = { index, distance };
    }
  }
  return best?.index;
}

/**
 * The interior waypoints of an orthogonal route after its middle segment `segmentIndex` (between points[i] and
 * points[i+1]) moves perpendicular to itself by `delta`. Both endpoints of the segment move, and the route's interior
 * points — points[1] through points[length-2] — are the waypoints written back, pinning the route.
 */
export function moveOrthSegment(points: readonly Point[], segmentIndex: number, delta: Point): Point[] {
  const a = points[segmentIndex];
  const b = points[segmentIndex + 1];
  if (a === undefined || b === undefined) {
    return points.slice(1, -1);
  }
  const shift = perpendicularDelta(a, b, delta);
  return points.slice(1, -1).map((point, i) => {
    // Interior index i corresponds to points[i+1]; the segment's two endpoints are segmentIndex and segmentIndex+1.
    const routeIndex = i + 1;
    if (routeIndex === segmentIndex || routeIndex === segmentIndex + 1) {
      return { x: point.x + shift.x, y: point.y + shift.y };
    }
    return point;
  });
}

/**
 * A drag handle on a selected connector: where it sits and what dragging it moves. Orthogonal handles sit at the
 * midpoint of a draggable middle segment (`segmentIndex`); curved handles sit at a waypoint (`waypointIndex` into the
 * edge's stored waypoints) or at the curve's arc-length midpoint, where a drag inserts a new waypoint (index -1).
 */
export type SegmentHandle =
  | { kind: 'orthogonal'; at: Point; segmentIndex: number }
  | { kind: 'curved'; at: Point; waypointIndex: number };

/** The midpoint of a segment. */
function midpoint(a: Point, b: Point): Point {
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

/**
 * The drag handles of a selected connector, drawn by SelectionOverlay.tsx and hit-tested by DiagramCanvas: one at the
 * midpoint of each draggable middle segment of an orthogonal route, or, on a curve, one at each waypoint plus one at
 * the curve's arc-length midpoint. The handles are only an affordance — a press anywhere on the segment or waypoint
 * still starts the same drag — so this never changes what segmentDrag's move functions do.
 */
export function segmentHandles(points: readonly Point[], mode: string | undefined): SegmentHandle[] {
  const out: SegmentHandle[] = [];
  if (mode === 'orthogonal') {
    for (const { index } of middleSegments(points)) {
      const a = points[index];
      const b = points[index + 1];
      if (a !== undefined && b !== undefined) {
        out.push({ kind: 'orthogonal', at: midpoint(a, b), segmentIndex: index });
      }
    }
    return out;
  }
  if (mode === 'curved') {
    for (let i = 1; i < points.length - 1; i++) {
      const at = points[i];
      if (at !== undefined) {
        out.push({ kind: 'curved', at: { x: at.x, y: at.y }, waypointIndex: i - 1 });
      }
    }
    out.push({ kind: 'curved', at: curveMidpoint(points), waypointIndex: -1 });
    return out;
  }
  return out;
}

/** The index of the waypoint the pointer is on, or undefined when none is within `reach`. */
export function curveWaypointAt(waypoints: readonly Point[], at: Point, reach: number): number | undefined {
  let best: { index: number; distance: number } | undefined;
  for (let i = 0; i < waypoints.length; i++) {
    const point = waypoints[i];
    if (point === undefined) {
      continue;
    }
    const distance = (point.x - at.x) * (point.x - at.x) + (point.y - at.y) * (point.y - at.y);
    if (distance <= reach * reach && (best === undefined || distance < best.distance)) {
      best = { index: i, distance };
    }
  }
  return best?.index;
}

/**
 * The waypoints after a curved drag: `waypointIndex` names an existing waypoint to move to `at`, or -1 to append a
 * new waypoint at `at`. The list returned is the edge's stored `waypoints` (its anchors), endpoints excluded.
 */
export function moveCurvedWaypoint(waypoints: readonly Point[], waypointIndex: number, at: Point): Point[] {
  if (waypointIndex < 0) {
    return [...waypoints, { x: at.x, y: at.y }];
  }
  return waypoints.map((point, i) => (i === waypointIndex ? { x: at.x, y: at.y } : point));
}