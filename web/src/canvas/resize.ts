/**
 * Resize geometry for the eight selection handles (checklist item 3). Pure: a start geometry, the handle being dragged,
 * the world-space pointer delta since the press and whether Shift is held go in; the node's new absolute geometry
 * comes out. The canvas owns the gesture and the model write.
 *
 * A handle resizes from the opposite anchor: the corner or edge midpoint across the node stays exactly where it was.
 * An edge handle changes one dimension only. With Shift the start aspect ratio is kept: a corner scales by whichever
 * axis the pointer moved further, and an edge handle scales the other dimension with it, symmetric about the anchor's
 * axis. A node never inverts or collapses: each dimension is held at MIN_NODE_SIZE or its start size, whichever is
 * smaller, so a handle dragged past the anchor pins the node at that minimum rather than flipping it.
 *
 * The maths runs in the node's own frame, so a rotated node resizes along its own axes and keeps its rotation; the
 * anchor is held fixed in world space.
 */

import type { Geometry, Point } from '../model/types.ts';
import { STATE_FORK_JOIN_SHAPE } from '../model/stateShapes.ts';

/** Smallest width and height a resize may produce, in canvas units. */
export const MIN_NODE_SIZE = 8;

/** The one axis a shape may resize along: a bar keeps its thickness and only lengthens. */
export type ResizeLock = 'horizontal' | 'vertical';

/**
 * The axis a shape resizes along, or undefined for the usual free resize. The fork/join bar is the only locked shape:
 * its length is meaningful and its thickness is a fixed fraction of the box, so a perpendicular drag does nothing.
 * The axis is in the node's own frame, so a rotated bar locks along its own length whichever way it faces.
 */
export function resizeLockForShape(shape: string): ResizeLock | undefined {
  return shape === STATE_FORK_JOIN_SHAPE ? 'horizontal' : undefined;
}

/**
 * The handle a locked resize actually drags: the perpendicular axis is pinned to the middle, which leaves that
 * dimension and its anchor untouched, so only the bar's length changes. A free resize passes the handle through.
 */
export function lockedHandle(handle: Point, lock: ResizeLock | undefined): Point {
  if (lock === 'horizontal') {
    return { x: handle.x, y: 0.5 };
  }
  if (lock === 'vertical') {
    return { x: 0.5, y: handle.y };
  }
  return handle;
}

/**
 * The eight handles as fractions of the node's width and height: the four corners and the four edge midpoints,
 * clockwise from the top-left. The overlay draws them in this order and a resize names a handle by its index here.
 */
export const HANDLE_ANCHORS: readonly Point[] = [
  { x: 0, y: 0 },
  { x: 0.5, y: 0 },
  { x: 1, y: 0 },
  { x: 1, y: 0.5 },
  { x: 1, y: 1 },
  { x: 0.5, y: 1 },
  { x: 0, y: 1 },
  { x: 0, y: 0.5 },
];

/** The CSS cursor for each handle in HANDLE_ANCHORS, as drawn on an unrotated node. */
export const HANDLE_CURSORS: readonly string[] = ['nwse-resize', 'ns-resize', 'nesw-resize', 'ew-resize', 'nwse-resize', 'ns-resize', 'nesw-resize', 'ew-resize'];

function rotate(point: Point, radians: number): Point {
  const cos = Math.cos(radians);
  const sin = Math.sin(radians);
  return { x: point.x * cos - point.y * sin, y: point.x * sin + point.y * cos };
}

/** How far one dimension moves: +1 when the handle is on the far side, -1 on the near side, 0 for the middle. */
function side(fraction: number): -1 | 0 | 1 {
  return fraction === 0 ? -1 : fraction === 1 ? 1 : 0;
}

/**
 * The geometry after dragging `handle` (a fraction pair from HANDLE_ANCHORS) by `delta` world units from `start`.
 * `keepAspect` is Shift.
 */
export function resizeGeometry(start: Geometry, handle: Point, delta: Point, keepAspect: boolean): Geometry {
  const radians = ((start.rotation ?? 0) * Math.PI) / 180;
  const local = rotate(delta, -radians);
  const sx = side(handle.x);
  const sy = side(handle.y);
  const minW = Math.min(MIN_NODE_SIZE, start.w);
  const minH = Math.min(MIN_NODE_SIZE, start.h);

  let w = Math.max(minW, start.w + sx * local.x);
  let h = Math.max(minH, start.h + sy * local.y);

  if (keepAspect && start.w > 0 && start.h > 0) {
    // The scale the pointer asks for on each axis the handle moves; an edge handle drives the other axis with it.
    const scaleX = w / start.w;
    const scaleY = h / start.h;
    const wanted = sx !== 0 && sy !== 0 ? (Math.abs(scaleX - 1) >= Math.abs(scaleY - 1) ? scaleX : scaleY) : sx !== 0 ? scaleX : scaleY;
    const scale = Math.max(wanted, minW / start.w, minH / start.h);
    w = start.w * scale;
    h = start.h * scale;
  }

  // The anchor is the point opposite the handle, as an offset from the centre in the node's frame; keep it fixed.
  const centre = { x: start.x + start.w / 2, y: start.y + start.h / 2 };
  const anchorFrom = rotate({ x: (0.5 - handle.x) * start.w, y: (0.5 - handle.y) * start.h }, radians);
  const anchorTo = rotate({ x: (0.5 - handle.x) * w, y: (0.5 - handle.y) * h }, radians);
  const next = { x: centre.x + anchorFrom.x - anchorTo.x, y: centre.y + anchorFrom.y - anchorTo.y };
  return { ...start, x: next.x - w / 2, y: next.y - h / 2, w, h };
}
