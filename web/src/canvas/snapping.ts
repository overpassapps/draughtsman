/**
 * Snapping and smart-guide geometry. Pure functions: given the rectangle being moved or resized, the other nodes'
 * rectangles, the grid, and a snap threshold, they return the snapped rectangle and the guide lines to draw. There is
 * no drag handling, rendering or model writing here; the caller owns those and feeds this module a frame.
 *
 * The snap threshold is in canvas units (CSS pixels at zoom 1). Checklist items 6 and 7 specify a 6 px magnet, which
 * is screen pixels; convert before calling by dividing by the viewport zoom: `threshold = SNAP_PIXELS / zoom`.
 * At zoom 1 the two coincide.
 *
 * Precedence: object snaps (edge, centre and equal-spacing) are considered first on each axis, and the nearest one
 * within the threshold wins; only when no object snap is within the threshold does the grid snap apply, and then it
 * always rounds the nearest moving edge to a grid line so the node jumps between lines. The grid gets
 * the remainder on purpose — a live magnet to a neighbour always beats the grid, because the grid line is still there
 * to see and the neighbour is information the user is nudging towards. ("Should it be the other way round?" asked:
 * no. A grid line carries no intent, so it should lose to any node the user could be reaching for.)
 */

import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';

/** The 6 px magnet cheat-sheet items 6 and 7 name; paired with the threshold-in-canvas-units rule above. */
export const SNAP_PIXELS = 6;

export type GuideKind = 'grid' | 'edge' | 'centre' | 'equal-spacing';

/** A guide line to draw. `from`/`to` are the segment it spans, in canvas units. */
export interface Guide {
  kind: GuideKind;
  from: Point;
  to: Point;
}

/** Which edges of the moving rectangle are being dragged. For a move all four are true; for a resize only the edge(s)
 * under the handle are. */
export interface MovingEdges {
  left: boolean;
  right: boolean;
  top: boolean;
  bottom: boolean;
}

/** A whole-node move: every edge moves, so the rectangle translates rather than resizes. */
export const MOVE_EDGES: MovingEdges = { left: true, right: true, top: true, bottom: true };

export interface SnapParams {
  /** The rectangle being moved or resized, at its current (unsnapped) position. */
  moving: Rect;
  /** Which edges are moving. `MOVE_EDGES` for a drag; a single edge for a resize handle. */
  edges: MovingEdges;
  /** The other nodes' rectangles, excluding the moving node itself. */
  others: readonly Rect[];
  /** Grid spacing in canvas units (the diagram theme's `grid.size`). Non-positive disables the grid. */
  gridSize: number;
  /** Whether the grid is enabled for snapping. */
  gridEnabled: boolean;
  /** Snap magnet in canvas units; pass `SNAP_PIXELS / zoom`. */
  threshold: number;
}

export interface SnapResult {
  /** The moved/resized rectangle with each axis snapped independently. */
  rect: Rect;
  /** The guides to draw for this frame, in canvas units. */
  guides: Guide[];
}

type Axis = 'x' | 'y';

interface AxisKey {
  low: number;
  high: number;
  size: number;
}

function axisOf(axis: Axis, r: Rect): AxisKey {
  return axis === 'x' ? { low: r.x, high: r.x + r.w, size: r.w } : { low: r.y, high: r.y + r.h, size: r.h };
}

/** The perpendicular span of a rectangle; two nodes are "aligned" when their perpendicular spans overlap. */
function crossSpan(axis: Axis, r: Rect): { lo: number; hi: number } {
  return axis === 'x' ? { lo: r.y, hi: r.y + r.h } : { lo: r.x, hi: r.x + r.w };
}

function spansOverlap(a: { lo: number; hi: number }, b: { lo: number; hi: number }): boolean {
  return a.lo < b.hi && b.lo < a.hi;
}

/** A snap onto another node's edge or centre. `d` is the axis delta the matched moving edge must travel. */
interface AlignCandidate {
  d: number;
  kind: 'edge' | 'centre';
  other: Rect;
  otherIndex: number;
  /** Absolute target position on the axis (the other node's edge or centre). */
  target: number;
  /** Which part of the moving node matched (the centre only for a move), so a resize knows which position to write. */
  ownEdge: 'low' | 'centre' | 'high';
}

const OWN_EDGE_ORDER = { low: 0, centre: 1, high: 2 } as const;

/** True when `a` sorts before `b`: nearest first, then deterministic ties by kind, order and slot. */
function betterAlign(a: AlignCandidate, b: AlignCandidate): boolean {
  const da = Math.abs(a.d);
  const db = Math.abs(b.d);
  if (da !== db) return da < db;
  if (a.kind !== b.kind) return a.kind === 'edge';
  if (a.otherIndex !== b.otherIndex) return a.otherIndex < b.otherIndex;
  return OWN_EDGE_ORDER[a.ownEdge] < OWN_EDGE_ORDER[b.ownEdge];
}

/** A snap onto equal spacing: the moving node's gap to a neighbour matching a gap between two other aligned nodes. */
interface SpacingCandidate {
  d: number;
  neighbour: Rect;
  neighbourIndex: number;
  /** Whether the neighbour sits after the moving node on the axis (so the moving node slides back towards it). */
  onHighSide: boolean;
  gap: number;
  refGap: number;
}

function betterSpacing(a: SpacingCandidate, b: SpacingCandidate): boolean {
  const da = Math.abs(a.d);
  const db = Math.abs(b.d);
  if (da !== db) return da < db;
  if (a.neighbourIndex !== b.neighbourIndex) return a.neighbourIndex < b.neighbourIndex;
  return !a.onHighSide;
}

interface GridCandidate {
  /** Absolute grid line position on the axis. */
  line: number;
  d: number;
  ownEdge: 'low' | 'high';
}

/** Index of the first entry in the ascending `values` that is at least `min`. */
function lowerBound(values: readonly number[], min: number): number {
  let lo = 0;
  let hi = values.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if ((values[mid] ?? Infinity) < min) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

function betterGrid(a: GridCandidate, b: GridCandidate): boolean {
  const da = Math.abs(a.d);
  const db = Math.abs(b.d);
  if (da !== db) return da < db;
  return a.ownEdge === 'low';
}

/** One axis' snap: the new position and size, and how to draw its guide once both axes are resolved. */
interface AxisSnap {
  low: number;
  size: number;
  guide?: (snapped: Rect) => Guide;
}

/**
 * Snaps one axis of the moving rectangle and returns the new axis position/size plus the guide that axis produced.
 * Object snaps (edge, centre, equal-spacing) are gathered first and win over the grid; each axis is independent, so
 * an x snap never affects y.
 */
function snapAxis(
  axis: Axis,
  moving: Rect,
  lowMoving: boolean,
  highMoving: boolean,
  others: readonly Rect[],
  gridSize: number,
  gridEnabled: boolean,
  threshold: number,
): AxisSnap {
  const own = axisOf(axis, moving);
  const translate = lowMoving && highMoving;
  const movingEdges: { which: 'low' | 'high'; pos: number }[] = [];
  if (lowMoving) movingEdges.push({ which: 'low', pos: own.low });
  if (highMoving) movingEdges.push({ which: 'high', pos: own.high });
  const alignSources: { which: 'low' | 'centre' | 'high'; pos: number }[] = [...movingEdges];
  if (translate) alignSources.push({ which: 'centre', pos: own.low + own.size / 2 });

  let bestAlign: AlignCandidate | null = null;
  for (const [i, o] of others.entries()) {
    const oa = axisOf(axis, o);
    const targets: { kind: 'edge' | 'centre'; pos: number }[] = [
      { kind: 'edge', pos: oa.low },
      { kind: 'centre', pos: oa.low + oa.size / 2 },
      { kind: 'edge', pos: oa.high },
    ];
    for (const t of targets) {
      for (const e of alignSources) {
        const d = t.pos - e.pos;
        if (Math.abs(d) <= threshold) {
          const kind = t.kind === 'centre' || e.which === 'centre' ? 'centre' : 'edge';
          const candidate: AlignCandidate = { d, kind, other: o, otherIndex: i, target: t.pos, ownEdge: e.which };
          if (!bestAlign || betterAlign(candidate, bestAlign)) bestAlign = candidate;
        }
      }
    }
  }

  let bestSpacing: SpacingCandidate | null = null;
  // Equal spacing is a move concept: a resize edge that only shifts one side is not nudging a whole node towards a
  // gap, so it is skipped for resizes.
  if (translate) {
    // The moving node's neighbours are only the nearest aligned node on each side; a node further along the row has
    // another node between them and is not a neighbour.
    const ownCross = crossSpan(axis, moving);
    let lowNeighbour: { node: Rect; index: number; gap: number } | null = null;
    let highNeighbour: { node: Rect; index: number; gap: number } | null = null;
    for (const [n, neighbour] of others.entries()) {
      const na = axisOf(axis, neighbour);
      if (!spansOverlap(ownCross, crossSpan(axis, neighbour))) continue;
      if (na.high <= own.low) {
        const gap = own.low - na.high;
        if (!lowNeighbour || gap < lowNeighbour.gap) lowNeighbour = { node: neighbour, index: n, gap };
      } else if (na.low >= own.high) {
        const gap = na.low - own.high;
        if (!highNeighbour || gap < highNeighbour.gap) highNeighbour = { node: neighbour, index: n, gap };
      }
    }
    const neighbours = [
      ...(lowNeighbour ? [{ ...lowNeighbour, onHighSide: false }] : []),
      ...(highNeighbour ? [{ ...highNeighbour, onHighSide: true }] : []),
    ];

    if (neighbours.length > 0) {
      // Reference gaps: the whitespace between each other node and its nearest aligned node on the high side. Nodes
      // are sorted by axis position so each scan starts at the first node past this one and stops at the first
      // aligned hit, or once the gap is too wide to match any neighbour gap, which keeps a 2000 node drag in a frame.
      const maxGap = Math.max(...neighbours.map((nb) => nb.gap)) + threshold;
      const sorted = others.slice().sort((p, q) => axisOf(axis, p).low - axisOf(axis, q).low);
      const lows = sorted.map((r) => axisOf(axis, r).low);
      const referenceGaps: number[] = [];
      for (const p of sorted) {
        const pHigh = axisOf(axis, p).high;
        const pCross = crossSpan(axis, p);
        for (let j = lowerBound(lows, pHigh); j < sorted.length; j++) {
          const q = sorted[j];
          if (!q) break;
          const gap = axisOf(axis, q).low - pHigh;
          if (gap > maxGap) break;
          if (!spansOverlap(pCross, crossSpan(axis, q))) continue;
          if (gap > 0) referenceGaps.push(gap);
          break;
        }
      }
      for (const nb of neighbours) {
        for (const refGap of referenceGaps) {
          const d = nb.onHighSide ? nb.gap - refGap : refGap - nb.gap;
          if (Math.abs(d) > threshold) continue;
          const candidate: SpacingCandidate = { d, neighbour: nb.node, neighbourIndex: nb.index, onHighSide: nb.onHighSide, gap: nb.gap, refGap };
          if (!bestSpacing || betterSpacing(candidate, bestSpacing)) bestSpacing = candidate;
        }
      }
    }
  }

  if (bestAlign && bestSpacing) {
    const da = Math.abs(bestAlign.d);
    const db = Math.abs(bestSpacing.d);
    const useAlign = da <= db; // ties favour alignment over spacing
    if (useAlign) bestSpacing = null;
    else bestAlign = null;
  }

  if (bestAlign) {
    const { other, target, kind } = bestAlign;
    const applied = applyEdgeTarget(own, translate, lowMoving, highMoving, bestAlign.ownEdge, target);
    return { ...applied, guide: (snapped) => alignGuide(axis, snapped, other, target, kind) };
  }

  if (bestSpacing) {
    const { neighbour, onHighSide } = bestSpacing;
    return { low: own.low + bestSpacing.d, size: own.size, guide: (snapped) => spacingGuide(axis, snapped, neighbour, onHighSide) };
  }

  if (gridEnabled && gridSize > 0) {
    let bestGrid: GridCandidate | null = null;
    for (const e of movingEdges) {
      let line = Math.round(e.pos / gridSize) * gridSize;
      if (!translate && e.which === 'high' && line <= own.low) line = (Math.floor(own.low / gridSize) + 1) * gridSize;
      if (!translate && e.which === 'low' && line >= own.high) line = (Math.ceil(own.high / gridSize) - 1) * gridSize;
      const candidate: GridCandidate = { line, d: line - e.pos, ownEdge: e.which };
      if (!bestGrid || betterGrid(candidate, bestGrid)) bestGrid = candidate;
    }
    if (bestGrid) {
      const { line } = bestGrid;
      const applied = applyEdgeTarget(own, translate, lowMoving, highMoving, bestGrid.ownEdge, line);
      return { ...applied, guide: (snapped) => gridGuide(axis, snapped, line) };
    }
  }

  return { low: own.low, size: own.size };
}

/** Applies a snap that pins a particular moving edge to an absolute `target` position on the axis. */
function applyEdgeTarget(
  own: AxisKey,
  translate: boolean,
  lowMoving: boolean,
  highMoving: boolean,
  matchedEdge: 'low' | 'centre' | 'high',
  target: number,
): { low: number; size: number } {
  if (translate) {
    const from = matchedEdge === 'low' ? own.low : matchedEdge === 'high' ? own.high : own.low + own.size / 2;
    const d = target - from;
    return { low: own.low + d, size: own.size };
  }
  if (lowMoving) {
    return { low: target, size: own.high - target };
  }
  if (highMoving) {
    return { low: own.low, size: target - own.low };
  }
  return { low: own.low, size: own.size };
}

/** A guide line along the shared edge or centre, spanning both the moving node and the node it snapped to. */
function alignGuide(axis: Axis, moving: Rect, other: Rect, target: number, kind: 'edge' | 'centre'): Guide {
  if (axis === 'x') {
    const top = Math.min(moving.y, other.y);
    const bottom = Math.max(moving.y + moving.h, other.y + other.h);
    return { kind, from: { x: target, y: top }, to: { x: target, y: bottom } };
  }
  const left = Math.min(moving.x, other.x);
  const right = Math.max(moving.x + moving.w, other.x + other.w);
  return { kind, from: { x: left, y: target }, to: { x: right, y: target } };
}

/** A short line across the gap between the moved node and the neighbour it matched, at their shared midpoint. */
function spacingGuide(axis: Axis, moving: Rect, neighbour: Rect, onHighSide: boolean): Guide {
  const ownCross = crossSpan(axis, moving);
  const neighbourCross = crossSpan(axis, neighbour);
  const mid = (Math.max(ownCross.lo, neighbourCross.lo) + Math.min(ownCross.hi, neighbourCross.hi)) / 2;
  const own = axisOf(axis, moving);
  const neighbourAxis = axisOf(axis, neighbour);
  if (axis === 'x') {
    return onHighSide
      ? { kind: 'equal-spacing', from: { x: own.high, y: mid }, to: { x: neighbourAxis.low, y: mid } }
      : { kind: 'equal-spacing', from: { x: neighbourAxis.high, y: mid }, to: { x: own.low, y: mid } };
  }
  return onHighSide
    ? { kind: 'equal-spacing', from: { x: mid, y: own.high }, to: { x: mid, y: neighbourAxis.low } }
    : { kind: 'equal-spacing', from: { x: mid, y: neighbourAxis.high }, to: { x: mid, y: own.low } };
}

/** A guide along the grid line an edge snapped to, spanning the moving node's own extent. */
function gridGuide(axis: Axis, moving: Rect, line: number): Guide {
  if (axis === 'x') {
    return { kind: 'grid', from: { x: line, y: moving.y }, to: { x: line, y: moving.y + moving.h } };
  }
  return { kind: 'grid', from: { x: moving.x, y: line }, to: { x: moving.x + moving.w, y: line } };
}

/** Snaps the moving rectangle to the grid and the other nodes, returning the snapped rectangle and the guides. */
export function snapRect(params: SnapParams): SnapResult {
  const { moving, edges, others, gridSize, gridEnabled, threshold } = params;
  const x = snapAxis('x', moving, edges.left, edges.right, others, gridSize, gridEnabled, threshold);
  const y = snapAxis('y', moving, edges.top, edges.bottom, others, gridSize, gridEnabled, threshold);
  const rect: Rect = { x: x.low, y: y.low, w: x.size, h: y.size };
  const guides: Guide[] = [];
  if (x.guide) guides.push(x.guide(rect));
  if (y.guide) guides.push(y.guide(rect));
  return { rect, guides };
}