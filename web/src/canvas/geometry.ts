/** Axis-aligned rectangles and points in canvas units. Pure functions shared by the renderer, viewport and culling. */

import type { Geometry, Point } from '../model/types.ts';

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Whether a rectangle covers any area: a node clipped entirely away by its container collapses to none. */
export function hasArea(rect: Rect): boolean {
  return rect.w > 0 && rect.h > 0;
}

export function rectsIntersect(a: Rect, b: Rect): boolean {
  return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

/** The overlap of two rectangles, or a zero-size rectangle when they do not overlap. */
export function intersectRects(a: Rect, b: Rect): Rect {
  const x = Math.max(a.x, b.x);
  const y = Math.max(a.y, b.y);
  const right = Math.min(a.x + a.w, b.x + b.w);
  const bottom = Math.min(a.y + a.h, b.y + b.h);
  if (right <= x || bottom <= y) {
    return { x, y, w: 0, h: 0 };
  }
  return { x, y, w: right - x, h: bottom - y };
}

export function unionRects(rects: Iterable<Rect>): Rect | undefined {
  let result: Rect | undefined;
  for (const rect of rects) {
    if (!result) {
      result = { ...rect };
      continue;
    }
    const right = Math.max(result.x + result.w, rect.x + rect.w);
    const bottom = Math.max(result.y + result.h, rect.y + rect.h);
    result.x = Math.min(result.x, rect.x);
    result.y = Math.min(result.y, rect.y);
    result.w = right - result.x;
    result.h = bottom - result.y;
  }
  return result;
}

/** Grows (or shrinks, for negative amounts) a rectangle by the same amount on every side. */
export function padRect(rect: Rect, amount: number): Rect {
  return { x: rect.x - amount, y: rect.y - amount, w: rect.w + amount * 2, h: rect.h + amount * 2 };
}

/** Bounding box of a set of points, or undefined for none. */
export function boundsOfPoints(points: Point[]): Rect | undefined {
  if (points.length === 0) {
    return undefined;
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of points) {
    minX = Math.min(minX, p.x);
    minY = Math.min(minY, p.y);
    maxX = Math.max(maxX, p.x);
    maxY = Math.max(maxY, p.y);
  }
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}

export function rectCentre(rect: Rect): Point {
  return { x: rect.x + rect.w / 2, y: rect.y + rect.h / 2 };
}

/** The axis-aligned box a node covers after its rotation about its centre. */
export function rotatedBounds(geometry: Geometry): Rect {
  const rotation = geometry.rotation ?? 0;
  if (rotation % 360 === 0) {
    return { x: geometry.x, y: geometry.y, w: geometry.w, h: geometry.h };
  }
  const radians = (rotation * Math.PI) / 180;
  const cos = Math.abs(Math.cos(radians));
  const sin = Math.abs(Math.sin(radians));
  const w = geometry.w * cos + geometry.h * sin;
  const h = geometry.w * sin + geometry.h * cos;
  const centre = rectCentre(geometry);
  return { x: centre.x - w / 2, y: centre.y - h / 2, w, h };
}

/**
 * Where the segment from `inside` (a point within `rect`) towards `outside` leaves the rectangle.
 * Returns `inside` unchanged when the target is inside too, so a degenerate edge still draws.
 */
export function rectBoundaryPoint(rect: Rect, inside: Point, outside: Point): Point {
  const dx = outside.x - inside.x;
  const dy = outside.y - inside.y;
  if (dx === 0 && dy === 0) {
    return inside;
  }
  let t = Infinity;
  if (dx > 0) {
    t = Math.min(t, (rect.x + rect.w - inside.x) / dx);
  } else if (dx < 0) {
    t = Math.min(t, (rect.x - inside.x) / dx);
  }
  if (dy > 0) {
    t = Math.min(t, (rect.y + rect.h - inside.y) / dy);
  } else if (dy < 0) {
    t = Math.min(t, (rect.y - inside.y) / dy);
  }
  if (!Number.isFinite(t) || t < 0) {
    return inside;
  }
  t = Math.min(t, 1);
  return { x: inside.x + dx * t, y: inside.y + dy * t };
}

/** Whether `point` lies strictly inside `rect` (a point exactly on the boundary counts as outside). */
export function rectContainsPoint(rect: Rect, point: Point): boolean {
  return point.x > rect.x && point.x < rect.x + rect.w && point.y > rect.y && point.y < rect.y + rect.h;
}

export function distance(a: Point, b: Point): number {
  return Math.hypot(b.x - a.x, b.y - a.y);
}

/** Radius of the decorative loop a self-referencing edge draws above its node, clamped to half the node width. */
const SELF_LOOP_RADIUS = 20;

/** Number of segments the self-loop arc is sampled into; enough that the polyline reads as a curve. */
const SELF_LOOP_SEGMENTS = 16;

/**
 * The points of a self-referencing edge: a semicircular arc standing on `anchor`, sampled left to right over the
 * top so the final segment runs back down into the node and the arrowhead points inward. `anchor` defaults to the
 * middle of the node's top edge, so the loop stands clear of the shape; an endpoint that names a port passes that
 * port's position instead, and the loop leaves and returns there. The loop is a fixed decorative shape the renderer
 * owns; ELK leaves self-loops unrouted, so no layout produces these points.
 */
export function selfLoopPoints(geometry: Geometry, anchor?: Point): Point[] {
  const base = anchor ?? { x: geometry.x + geometry.w / 2, y: geometry.y };
  const radius = Math.min(geometry.w / 2, SELF_LOOP_RADIUS);
  const points: Point[] = [];
  for (let i = 0; i <= SELF_LOOP_SEGMENTS; i++) {
    const angle = Math.PI - (i / SELF_LOOP_SEGMENTS) * Math.PI;
    points.push({ x: base.x + radius * Math.cos(angle), y: base.y - radius * Math.sin(angle) });
  }
  return points;
}

/** The point a given fraction (0 to 1) of the way along a polyline, by arc length. */
export function pointAlongPolyline(points: Point[], fraction: number): Point {
  const first = points[0];
  if (!first) {
    return { x: 0, y: 0 };
  }
  let total = 0;
  let previous = first;
  for (const point of points) {
    total += distance(previous, point);
    previous = point;
  }
  if (total === 0) {
    return first;
  }
  let remaining = total * Math.min(Math.max(fraction, 0), 1);
  previous = first;
  for (const point of points) {
    const length = distance(previous, point);
    if (remaining <= length) {
      const t = length === 0 ? 0 : remaining / length;
      return { x: previous.x + (point.x - previous.x) * t, y: previous.y + (point.y - previous.y) * t };
    }
    remaining -= length;
    previous = point;
  }
  return previous;
}
