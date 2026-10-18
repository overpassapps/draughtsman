/**
 * Virtualisation: which elements to mount for a given view. A pure function of the document index and the visible
 * world rectangle, so the culling rule is testable without React. Elements intersecting the viewport grown by the
 * margin (a fraction of its width and height on each side) are mounted; everything else is not in the DOM at all.
 * The renderer goes through `visibleSetMemo`, which keeps a computed set while it still covers the view, so a mounted
 * element is one intersecting a region at most MAX_REGION_GROWTH times that area, and always one containing the view.
 *
 * Level of detail (checklist item 29), two zoom rules. Below a legible on-screen size the canvas does not mount labels
 * at all: every change of zoom makes the browser re-shape every mounted SVG `<text>` at the new scale, which zoomed out
 * over the 2,000 node benchmark cost more than the whole frame budget, for text too small to read (`labelsLegible`).
 * Further out, below FULL_DETAIL_ZOOM, nodes and edges are drawn simplified (`fullDetail`).
 */

import type { Id } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { hasArea, rectsIntersect, type Rect } from './geometry.ts';
import { LABEL_FONT_SIZE } from './text.ts';

/** Grow the viewport by this fraction of its size on each side before culling. */
export const VIEWPORT_MARGIN = 0.2;

/**
 * The smallest label the canvas draws, in device pixels: Chrome's default minimum logical font size, the size below
 * which the browser itself treats text as unreadable. Counted in device pixels so a high-density screen, where smaller
 * CSS sizes stay readable, keeps its labels further out: with 14px labels the floor is about 43% zoom at a pixel ratio
 * of 1 and about 21% at 2.
 */
export const MIN_LABEL_DEVICE_PX = 6;

/** Whether labels are drawn at this zoom on a screen of this pixel ratio: only while at least MIN_LABEL_DEVICE_PX. */
export function labelsLegible(zoom: number, pixelRatio = 1): boolean {
  return LABEL_FONT_SIZE * zoom * pixelRatio >= MIN_LABEL_DEVICE_PX;
}

/**
 * The zoom below which the canvas draws its simplified level of detail: each node a plain box, each edge a plain line,
 * with no labels, arrowheads, dashes or shape detail. At 25% an arrowhead is about 3 screen pixels and a dash under 2,
 * so nothing that reads is lost. Unlike the label floor it is not scaled by the pixel ratio, since what it saves is
 * per-element work in the browser (paint properties, paint and compositing of every mounted element on every zoom
 * frame), which a denser screen does not reduce.
 */
export const FULL_DETAIL_ZOOM = 0.25;

/** Whether the canvas draws full detail at this zoom: at FULL_DETAIL_ZOOM and above. */
export function fullDetail(zoom: number): boolean {
  return zoom >= FULL_DETAIL_ZOOM;
}

export interface VisibleSet {
  nodes: ReadonlySet<Id>;
  edges: ReadonlySet<Id>;
}

export function expandViewport(viewport: Rect, margin = VIEWPORT_MARGIN): Rect {
  const dx = viewport.w * margin;
  const dy = viewport.h * margin;
  return { x: viewport.x - dx, y: viewport.y - dy, w: viewport.w + dx * 2, h: viewport.h + dy * 2 };
}

/** The ids of every node and edge whose bounds intersect the (margin-expanded) viewport. */
export function visibleElements(index: DocumentIndex, viewport: Rect, margin = VIEWPORT_MARGIN): VisibleSet {
  const region = expandViewport(viewport, margin);
  const nodes = new Set<Id>();
  for (const [id, bounds] of index.nodeBounds) {
    if (hasArea(bounds) && rectsIntersect(bounds, region)) {
      nodes.add(id);
    }
  }
  const edges = new Set<Id>();
  for (const [id, bounds] of index.edgeBounds) {
    if (rectsIntersect(bounds, region)) {
      edges.add(id);
    }
  }
  return { nodes, edges };
}

function sameIds(a: ReadonlySet<Id>, b: ReadonlySet<Id>): boolean {
  if (a.size !== b.size) {
    return false;
  }
  for (const id of a) {
    if (!b.has(id)) {
      return false;
    }
  }
  return true;
}

/**
 * `next`, or `previous` when both hold the same ids. A pan or zoom that mounts nothing new then hands the renderer the
 * very set it drew last frame, so the memoised element tree is reused and React reconciles none of it.
 */
export function stableVisibleSet(previous: VisibleSet | undefined, next: VisibleSet): VisibleSet {
  return previous !== undefined && sameIds(previous.nodes, next.nodes) && sameIds(previous.edges, next.edges) ? previous : next;
}

/**
 * How much larger than a fresh one (the view grown by the margin) a remembered set's region may be and still be reused:
 * a zoom in keeps the set it had until the view has shrunk by about 1.4x, then mounts only what the closer view needs.
 */
export const MAX_REGION_GROWTH = 2;

function contains(outer: Rect, inner: Rect): boolean {
  return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}

/**
 * A per-renderer memo over `visibleElements` with hysteresis. It remembers the set it last computed and the region it
 * covered (the view grown by the margin at the time) and hands that same set back while the index and margin are
 * unchanged, the region still covers the whole view, and the region is at most MAX_REGION_GROWTH times the area a fresh
 * set would cover. So a pan mounts nothing new until it has travelled the margin, and a zoom in mounts nothing new
 * until it has shrunk the view by about 1.4x; a new set keeps the old identity when it holds the same ids
 * (`stableVisibleSet`). Far zoomed out, where a gesture changes the mounted set on nearly every frame, this is what
 * keeps it from rebuilding the whole element tree every frame.
 *
 * Every element intersecting the view is always mounted, whatever is remembered, since the region covers the view; a
 * render React throws away can leave it remembering a set that was never drawn without drawing anything wrong.
 */
export function visibleSetMemo(): (index: DocumentIndex, view: Rect, margin?: number) => VisibleSet {
  let last: { index: DocumentIndex; margin: number; region: Rect; set: VisibleSet } | undefined;
  return (index, view, margin = VIEWPORT_MARGIN) => {
    const region = expandViewport(view, margin);
    if (last?.index === index && last.margin === margin && contains(last.region, view) && last.region.w * last.region.h <= region.w * region.h * MAX_REGION_GROWTH) {
      return last.set;
    }
    const set = stableVisibleSet(last?.set, visibleElements(index, view, margin));
    last = { index, margin, region, set };
    return set;
  };
}
