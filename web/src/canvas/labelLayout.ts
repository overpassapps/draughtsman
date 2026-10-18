/**
 * Keeping two connectors' labels from overlapping (M2 gap-audit Finding 4), and keeping a label off a node's own
 * box that is not one of its connector's endpoints (M2 final-check Finding C: a resize could push an "internal
 * call" label to sit inside the neighbouring node it landed next to). Each label sits at its own edge's midpoint
 * (labelEditing.ts `edgeLabelAt`, exactly as before this existed - a curved label at its curve's arc-length
 * midpoint, every other kind at its polyline midpoint) unless that plate would overlap one already placed or a
 * node's bounds. A colliding label slides along its own middle segment - the polyline segment its base point falls
 * on, the same segment a curved label's arc-length midpoint sits within - away from that point until it clears
 * every earlier label, every node box, and the border of its own endpoints, or the segment runs out. Nothing here writes back to the model: edge.label
 * stays a plain string with no stored position, and an edge whose label was never nudged keeps exactly its base
 * point.
 *
 * Pure geometry, so DiagramSvg (and the server SvgRenderer's C# port) can call it once per render with every visible
 * labelled edge, in document order: an earlier edge's label never moves to make room for a later one, so placement
 * is deterministic and independent of unrelated edges.
 */

import type { Id, Point } from '../model/types.ts';
import { distance } from './geometry.ts';

export interface LabelBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface LabelInput {
  id: string;
  /** This edge's default label centre: `edgeLabelAt(points, curved)`. */
  base: Point;
  /** The connector's drawn polyline (straight/orthogonal points, or the curve's waypoints for `curved`). */
  points: readonly Point[];
  width: number;
  height: number;
  /** This edge's own endpoint node ids, never treated as an obstacle for its own label. */
  excludeNodeIds: readonly Id[];
}

/** How far one nudge step moves the label along its segment, in canvas units. */
const STEP = 4;
/** How many steps are tried each direction before the label is left at its base point. */
const MAX_STEPS = 60;

export function boxAt(centre: Point, width: number, height: number): LabelBox {
  return { x: centre.x - width / 2, y: centre.y - height / 2, w: width, h: height };
}

export function boxesOverlap(a: LabelBox, b: LabelBox): boolean {
  return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

/** True when `inner` lies wholly within `outer`. */
function boxWithin(inner: LabelBox, outer: LabelBox): boolean {
  return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}

/** True when a plate sits across `box`'s outline: over part of it but neither clear of it nor wholly inside it. */
function straddles(plate: LabelBox, box: LabelBox): boolean {
  return boxesOverlap(plate, box) && !boxWithin(plate, box);
}

interface MidSegment {
  start: Point;
  end: Point;
  /** `base`'s arc-length distance from `start`, clamped onto the segment. */
  offset: number;
}

/** The consecutive pair of `points` closest to `base` (the segment its default position falls on or nearest to). */
function segmentNear(points: readonly Point[], base: Point): MidSegment | undefined {
  const first = points[0];
  if (first === undefined) {
    return undefined;
  }
  if (points.length === 1) {
    return { start: first, end: first, offset: 0 };
  }
  let best: MidSegment | undefined;
  let bestDistance = Infinity;
  for (let i = 0; i < points.length - 1; i++) {
    const start = points[i];
    const end = points[i + 1];
    if (start === undefined || end === undefined) {
      continue;
    }
    const length = distance(start, end);
    const t = length === 0 ? 0 : Math.min(Math.max(((base.x - start.x) * (end.x - start.x) + (base.y - start.y) * (end.y - start.y)) / (length * length), 0), 1);
    const projected = { x: start.x + (end.x - start.x) * t, y: start.y + (end.y - start.y) * t };
    const gap = distance(base, projected);
    if (gap < bestDistance) {
      bestDistance = gap;
      best = { start, end, offset: t * length };
    }
  }
  return best;
}

function pointAtOffset(segment: MidSegment, offset: number): Point {
  const length = distance(segment.start, segment.end);
  if (length === 0) {
    return segment.start;
  }
  const t = Math.min(Math.max(offset, 0), length) / length;
  return { x: segment.start.x + (segment.end.x - segment.start.x) * t, y: segment.start.y + (segment.end.y - segment.start.y) * t };
}

/**
 * Each labelled edge's plate centre, in `labels` order, later ones nudged clear of earlier ones and of every node in
 * `nodeBoxes` other than the label's own two endpoints, as described above. An edge with no points at all is left
 * out of the result, same as it would draw nothing to label.
 */
export function placeLabels(labels: readonly LabelInput[], nodeBoxes: ReadonlyMap<Id, LabelBox> = new Map()): Map<string, Point> {
  const placed = new Map<string, Point>();
  const boxes: LabelBox[] = [];
  for (const label of labels) {
    const nodeObstacles = [...nodeBoxes].filter(([nodeId]) => !label.excludeNodeIds.includes(nodeId)).map(([, obstacleBox]) => obstacleBox);
    // A plate may sit on its own endpoints (a short connector has nowhere else) but never across one's border: the
    // plate would cut the outline of a table it is attached to (QA: a foreign key's label on the table's header line).
    const ownBorders = label.excludeNodeIds.flatMap((nodeId) => nodeBoxes.get(nodeId) ?? []);
    const clear = (candidate: LabelBox): boolean =>
      !boxes.some((other) => boxesOverlap(candidate, other)) && !nodeObstacles.some((other) => boxesOverlap(candidate, other)) && !ownBorders.some((other) => straddles(candidate, other));
    let centre = label.base;
    let box = boxAt(centre, label.width, label.height);
    if (!clear(box)) {
      const segment = segmentNear(label.points, label.base);
      if (segment !== undefined) {
        outer: for (let step = 1; step <= MAX_STEPS; step++) {
          for (const candidateOffset of [segment.offset + step * STEP, segment.offset - step * STEP]) {
            const point = pointAtOffset(segment, candidateOffset);
            const candidateBox = boxAt(point, label.width, label.height);
            if (clear(candidateBox)) {
              centre = point;
              box = candidateBox;
              break outer;
            }
          }
        }
      }
    }
    boxes.push(box);
    placed.set(label.id, centre);
  }
  return placed;
}
