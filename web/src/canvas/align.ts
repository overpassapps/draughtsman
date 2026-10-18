/**
 * Align and distribute geometry (checklist item 24). Pure functions: given the selected nodes' rectangles keyed by id
 * and a command, they return the new top-left position of every node that moves. There is no selection, menu, undo or
 * model writing here; the caller owns those and applies the returned positions. Sizes never change, and ids whose
 * position would not change are left out of the result, so an empty map means "nothing to do".
 *
 * Rectangles are unrotated bounds in canvas units, as in snapping.ts: a node's rotation is ignored, so a rotated node
 * aligns and distributes by the box it would occupy at rotation 0.
 *
 * Align: `left`, `right`, `top` and `bottom` move every node to the selection's extreme edge in that direction (the
 * outermost node stays put); `centre` and `middle` move every node's centre onto the centre of the selection's
 * bounding box. Needs at least two nodes.
 *
 * Distribute: `horizontal` and `vertical` order the nodes by their centre on that axis (ties use string comparison of ids),
 * keep the first and last of that order where they are, and place the rest between them so that every gap between
 * neighbours - from one node's far edge to the next node's near edge - is the same. Nodes of different sizes therefore
 * get equal gaps, not equal centre spacing. The perpendicular coordinate is unchanged. Needs at least three nodes.
 * When the computed edge gap is negative, use equal centre spacing between the outer two centres instead, keeping
 * the outer nodes fixed and preserving centre order. A non-negative gap uses equal edge-to-edge spacing.
 *
 * No rounding. The checklist asks for gaps "equal to the pixel", and rounding cannot deliver that: when the free space
 * does not divide by the number of gaps, integer gaps must differ by a pixel somewhere. So the gap is the exact
 * fractional value, and each position is computed directly from the fixed first node (never by accumulating the
 * previous result) so that floating-point error cannot build up along a long row. The same holds for centre and
 * middle, whose target may be a half unit. Snapping to the grid afterwards, if wanted, is the caller's choice.
 */

import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';

export type AlignCommand = 'left' | 'centre' | 'right' | 'top' | 'middle' | 'bottom';

export type DistributeCommand = 'horizontal' | 'vertical';

/** Fewest nodes each kind of command acts on; below this the result is empty. */
export const MIN_ALIGN_NODES = 2;
export const MIN_DISTRIBUTE_NODES = 3;

/** New top-left positions keyed by node id, only for the nodes that move. */
export type Moves = Map<string, Point>;

/** Moves every node so that the chosen edges or centres coincide exactly. */
export function alignNodes(rects: ReadonlyMap<string, Rect>, command: AlignCommand): Moves {
  const moves: Moves = new Map();
  if (rects.size < MIN_ALIGN_NODES) {
    return moves;
  }

  const all = [...rects.values()];
  const minX = Math.min(...all.map((r) => r.x));
  const maxX = Math.max(...all.map((r) => r.x + r.w));
  const minY = Math.min(...all.map((r) => r.y));
  const maxY = Math.max(...all.map((r) => r.y + r.h));
  const centreX = (minX + maxX) / 2;
  const centreY = (minY + maxY) / 2;

  for (const [id, r] of rects) {
    let x = r.x;
    let y = r.y;
    switch (command) {
      case 'left':
        x = minX;
        break;
      case 'centre':
        x = centreX - r.w / 2;
        break;
      case 'right':
        x = maxX - r.w;
        break;
      case 'top':
        y = minY;
        break;
      case 'middle':
        y = centreY - r.h / 2;
        break;
      case 'bottom':
        y = maxY - r.h;
        break;
    }
    if (x !== r.x || y !== r.y) {
      moves.set(id, { x, y });
    }
  }
  return moves;
}

/** Distributes nodes by edge gaps or, for overfull selections, centre spacing, keeping the outer two fixed. */
export function distributeNodes(rects: ReadonlyMap<string, Rect>, command: DistributeCommand): Moves {
  const moves: Moves = new Map();
  if (rects.size < MIN_DISTRIBUTE_NODES) {
    return moves;
  }

  const horizontal = command === 'horizontal';
  const low = (r: Rect): number => (horizontal ? r.x : r.y);
  const size = (r: Rect): number => (horizontal ? r.w : r.h);

  const centre = (r: Rect): number => low(r) + size(r) / 2;
  const ordered = [...rects].sort(([aId, a], [bId, b]) =>
    centre(a) - centre(b) || (aId < bId ? -1 : aId > bId ? 1 : 0),
  );
  const first = ordered[0]?.[1];
  const last = ordered[ordered.length - 1]?.[1];
  if (!first || !last) {
    return moves;
  }

  const span = low(last) + size(last) - low(first);
  const totalSize = ordered.reduce((sum, [, r]) => sum + size(r), 0);
  const gap = (span - totalSize) / (ordered.length - 1);
  const centreSpacing = (centre(last) - centre(first)) / (ordered.length - 1);

  let sizeBefore = size(first);
  for (const [i, [id, r]] of ordered.entries()) {
    if (i === 0 || i === ordered.length - 1) {
      continue;
    }
    const position = gap < 0
      ? centre(first) + centreSpacing * i - size(r) / 2
      : low(first) + sizeBefore + gap * i;
    sizeBefore += size(r);
    if (position !== low(r)) {
      moves.set(id, horizontal ? { x: position, y: r.y } : { x: r.x, y: position });
    }
  }
  return moves;
}
