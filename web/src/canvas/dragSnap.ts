/**
 * Feeds a drag or resize frame through snapping.ts (the M2 editor checklist, items 5, 6 and 7). Pure: the gesture's
 * start geometry, the unsnapped frame and the snap context go in; the snapped geometry and the guides to draw come
 * out. DiagramCanvas owns the gesture and hands the result to onMoveNodes, so snapping only changes what each frame
 * writes and never how many undo steps a gesture takes.
 *
 * A move snaps the bounding box of everything it drags, so a multi-selection lines up as one block and keeps its
 * internal arrangement. A resize snaps only the edges under the handle. Two resizes are left unsnapped on purpose: a
 * Shift resize, where snapping one edge would break the aspect ratio the user asked to keep, and a rotated node, whose
 * edges are not the axis-aligned lines every snap target is.
 */

import type { Geometry, Id, Point } from '../model/types.ts';
import { translateGeometries } from '../model/editor.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { rotatedBounds, unionRects, type Rect } from './geometry.ts';
import { MIN_NODE_SIZE } from './resize.ts';
import { MOVE_EDGES, snapRect, SNAP_PIXELS, type Guide } from './snapping.ts';

export interface SnapContext {
  /** The rectangles a gesture may snap to: every placed node the gesture does not move. */
  others: readonly Rect[];
  /** Grid spacing in canvas units, from the diagram theme's `grid.size`. */
  gridSize: number;
  gridEnabled: boolean;
  /** The viewport zoom; the 6 px magnet is screen pixels, so it shrinks in canvas units as the view zooms in. */
  zoom: number;
}

export interface SnappedMove {
  moves: Map<Id, Geometry>;
  guides: Guide[];
}

export interface SnappedResize {
  geometry: Geometry;
  guides: Guide[];
}

/**
 * The snap targets for a gesture that moves or resizes `moving`: every other node's rotated bounds. A node awaiting
 * layout is skipped, because its provisional spot is not a position the user placed and snapping to it would commit a
 * real position relative to a made-up one.
 */
export function snapTargets(index: DocumentIndex, moving: ReadonlySet<Id>): Rect[] {
  const others: Rect[] = [];
  for (const node of index.placed.values()) {
    if (!moving.has(node.id) && !index.unplaced.has(node.id)) {
      others.push(rotatedBounds(node.geometry));
    }
  }
  return others;
}

function threshold(context: SnapContext): number {
  return SNAP_PIXELS / context.zoom;
}

/** The geometry of every dragged node for a pointer delta of (dx, dy), with the dragged block's bounds snapped. */
export function snapMove(origins: ReadonlyMap<Id, Geometry>, dx: number, dy: number, context: SnapContext): SnappedMove {
  const start = unionRects([...origins.values()].map(rotatedBounds));
  if (start === undefined) {
    return { moves: new Map(), guides: [] };
  }
  const moving = { ...start, x: start.x + dx, y: start.y + dy };
  const snapped = snapRect({ moving, edges: MOVE_EDGES, others: context.others, gridSize: context.gridSize, gridEnabled: context.gridEnabled, threshold: threshold(context) });
  return { moves: translateGeometries(origins, dx + snapped.rect.x - moving.x, dy + snapped.rect.y - moving.y), guides: snapped.guides };
}

/**
 * A resize frame (`next`, as resize.ts computed it from `start` and the handle at fractions `handle`) with the edges
 * under the handle snapped. An axis whose snap would take the node under its minimum size keeps its unsnapped value.
 */
export function snapResize(start: Geometry, handle: Point, next: Geometry, keepAspect: boolean, context: SnapContext): SnappedResize {
  if (keepAspect || (next.rotation ?? 0) % 360 !== 0) {
    return { geometry: next, guides: [] };
  }
  const edges = { left: handle.x === 0, right: handle.x === 1, top: handle.y === 0, bottom: handle.y === 1 };
  const snapped = snapRect({ moving: { x: next.x, y: next.y, w: next.w, h: next.h }, edges, others: context.others, gridSize: context.gridSize, gridEnabled: context.gridEnabled, threshold: threshold(context) });
  const xOk = snapped.rect.w >= Math.min(MIN_NODE_SIZE, start.w);
  const yOk = snapped.rect.h >= Math.min(MIN_NODE_SIZE, start.h);
  const geometry: Geometry = {
    ...next,
    x: xOk ? snapped.rect.x : next.x,
    w: xOk ? snapped.rect.w : next.w,
    y: yOk ? snapped.rect.y : next.y,
    h: yOk ? snapped.rect.h : next.h,
  };
  // A resize only ever draws edge, centre and grid guides (equal spacing is a move concept in snapping.ts), and each of
  // those runs along the snapped line: a vertical one is an x snap, a horizontal one a y snap.
  const guides = snapped.guides.filter((guide) => (guide.from.x === guide.to.x ? xOk : yOk));
  return { geometry, guides };
}
