/**
 * Marquee selection (the M2 editor checklist, item 2). Pure: the two corners of the drag and the document index go
 * in, the ids of the nodes it catches come out. The canvas owns the gesture, draws the box and replaces the selection.
 *
 * A node is caught when its bounds (turned with its rotation, as culling sees them) intersect the marquee; with Alt
 * held only nodes the marquee fully encloses are caught. Edges are never caught: they follow their endpoints. A node
 * awaiting layout is caught at its provisional spot, since that is where the user sees it and can click it.
 */

import type { Id, Point } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { rectsIntersect, rotatedBounds, type Rect } from './geometry.ts';

/** The rectangle between two corners of a drag, whichever way it was dragged. */
export function marqueeRect(a: Point, b: Point): Rect {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(b.x - a.x), h: Math.abs(b.y - a.y) };
}

function encloses(outer: Rect, inner: Rect): boolean {
  return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}

/** The nodes the marquee catches: every node it intersects, or with `enclosedOnly` (Alt) every node it fully encloses. */
export function nodesInMarquee(index: DocumentIndex, marquee: Rect, enclosedOnly: boolean): Set<Id> {
  const caught = new Set<Id>();
  for (const node of index.placed.values()) {
    const bounds = rotatedBounds(node.geometry);
    if (enclosedOnly ? encloses(marquee, bounds) : rectsIntersect(marquee, bounds)) {
      caught.add(node.id);
    }
  }
  return caught;
}

/** The selection a released marquee leaves: its catch alone, or with Shift added to the selection it started from. */
export function marqueeSelection(base: ReadonlySet<Id>, caught: ReadonlySet<Id>, additive: boolean): ReadonlySet<Id> {
  if (!additive) {
    return caught;
  }
  const next = new Set<Id>(base);
  for (const id of caught) {
    next.add(id);
  }
  return next;
}
