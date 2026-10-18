/**
 * Container geometry (checklist items 26 and 27). Pure functions over plain model values: they read a document and
 * return the changes to make, never mutating their inputs. There is no drag handling, rendering, clipping, undo, Yjs
 * writing or edge re-routing here; the caller owns those and applies what these functions return.
 *
 * Coordinate model: a node's geometry is ABSOLUTE canvas coordinates, including for a node inside a container
 * (web/src/model/types.ts, docs/schema/document.schema.json, and the renderer, which draws every node at its own
 * geometry and only uses `parent` for nesting and clipping). Every function here is written for that, so:
 * - `absoluteRect` is the stored box whatever the parent; there is no relative rect to convert to or from.
 * - Reparenting never changes geometry: setting or clearing `parent` alone keeps the absolute rect identical, at any
 *   nesting depth.
 * - Fitting a container to its contents changes the container's geometry only; its children's positions are already
 *   absolute and stay where they are, so there are no compensating child updates.
 * - Moving a container does NOT move its children for free: `moveWithContents` returns the new geometry for the
 *   container and every descendant, each shifted by exactly the same delta.
 *
 * What counts as a container: a node of kind `container`, or a placed node with at least one placed child. That is
 * the rule the renderer uses (documentIndex.ts nests a child only when both have geometry, and DiagramSvg draws a node
 * with nested children as a container).
 *
 * Broken parent chains: a `parent` that names no node, names the node itself, or loops back is treated as ending the
 * chain there (as documentIndex.ts does), so no walk here can loop forever on a malformed document.
 *
 * Rectangles are in canvas units. A container's rotation is ignored and preserved: its children clip to its
 * unrotated box, so containment and fitting use that box.
 */

import { rotatedBounds, unionRects, type Rect } from '../canvas/geometry.ts';
import { STATE_COMPOSITE_KIND } from './stateShapes.ts';
import { isSwimlaneKind } from './swimlaneKinds.ts';
import type { Document, Geometry, Id, Node, Point } from './types.ts';

/** The ids of every node below `nodeId` through `parent`, transitively; never includes `nodeId` itself. */
export function descendantsOf(document: Document, nodeId: Id): Set<Id> {
  const children = childrenIndex(document);
  const found = new Set<Id>();
  const pending = [...(children.get(nodeId) ?? [])];
  for (let id = pending.pop(); id !== undefined; id = pending.pop()) {
    if (id === nodeId || found.has(id)) {
      continue;
    }
    found.add(id);
    pending.push(...(children.get(id) ?? []));
  }
  return found;
}

/** Whether the renderer draws `node` as a container: kind `container`, or placed with at least one placed child. */
export function isContainer(document: Document, node: Node): boolean {
  return isContainerGiven(placedParents(document), node);
}

/**
 * The node's absolute rectangle in canvas units (unrotated), or undefined when the node does not exist or has no
 * geometry yet. Geometry is stored absolute, so this is the stored box whatever the node's parent.
 */
export function absoluteRect(document: Document, nodeId: Id): Rect | undefined {
  const geometry = findNode(document, nodeId)?.geometry;
  return geometry ? { x: geometry.x, y: geometry.y, w: geometry.w, h: geometry.h } : undefined;
}

/**
 * The container's new geometry so that the union of its direct children's bounds sits inside it with exactly
 * `padding` on all four sides. Children's bounds are their rotated axis-aligned boxes, so a rotated child fits as it is
 * drawn. Only direct children count: a nested container stands for its own contents. Children without geometry are
 * waiting for layout and are ignored. The container keeps its rotation and every other geometry field; a container
 * without geometry gets one. Children do not move (geometry is absolute).
 *
 * Returns undefined, meaning no change, when the container does not exist, has no placed children, or already fits.
 * Throws a RangeError for a negative or non-finite padding.
 */
export function fitToContents(document: Document, containerId: Id, padding: number): Geometry | undefined {
  if (!Number.isFinite(padding) || padding < 0) {
    throw new RangeError(`Padding must be a finite, non-negative number; got ${String(padding)}.`);
  }
  const container = findNode(document, containerId);
  if (!container) {
    return undefined;
  }
  const bounds = unionRects(
    document.nodes
      .filter((node) => node.parent === containerId && node.id !== containerId)
      .flatMap((node) => (node.geometry ? [rotatedBounds(node.geometry)] : [])),
  );
  if (!bounds) {
    return undefined;
  }
  const next: Geometry = {
    ...container.geometry,
    x: bounds.x - padding,
    y: bounds.y - padding,
    w: bounds.w + padding * 2,
    h: bounds.h + padding * 2,
  };
  const current = container.geometry;
  if (current?.x === next.x && current.y === next.y && current.w === next.w && current.h === next.h) {
    return undefined;
  }
  return next;
}

/** What moving a node with its contents changes: new geometry per node and new waypoints per edge. */
export interface ContentsMove {
  nodes: Map<Id, Geometry>;
  /** Edges with both ends inside the moved set, whose waypoints move with them. */
  waypoints: Map<Id, Point[]>;
}

/**
 * Moves `nodeId` and every descendant by `delta`. Each moved node's geometry is shifted by exactly the delta and keeps
 * its size and rotation; a node without geometry is waiting for layout and is left out. An edge with both endpoints in
 * the moved set has its waypoints shifted too, so it keeps its shape; an edge with one end outside keeps its
 * waypoints (re-routing it is the router's job). Edges without waypoints follow their endpoints with no change.
 * A zero delta or an unknown node returns empty maps.
 */
export function moveWithContents(document: Document, nodeId: Id, delta: Point): ContentsMove {
  const result: ContentsMove = { nodes: new Map(), waypoints: new Map() };
  if (!findNode(document, nodeId) || (delta.x === 0 && delta.y === 0)) {
    return result;
  }
  const moved = descendantsOf(document, nodeId);
  moved.add(nodeId);
  for (const node of document.nodes) {
    if (moved.has(node.id) && node.geometry) {
      result.nodes.set(node.id, { ...node.geometry, x: node.geometry.x + delta.x, y: node.geometry.y + delta.y });
    }
  }
  for (const edge of document.edges) {
    const waypoints = edge.routing?.waypoints;
    if (waypoints && waypoints.length > 0 && moved.has(edge.from.node) && moved.has(edge.to.node)) {
      result.waypoints.set(
        edge.id,
        waypoints.map((point) => ({ x: point.x + delta.x, y: point.y + delta.y })),
      );
    }
  }
  return result;
}

/** Why `reparent` made no change. */
export type ReparentRefusal =
  | 'unknown-node'
  | 'unknown-parent'
  /** The new parent is the node itself. */
  | 'self'
  /** The new parent is one of the node's descendants, which would make the node its own ancestor. */
  | 'cycle'
  /** The node already has that parent (or already has none). */
  | 'unchanged';

export type ReparentResult =
  | { changed: true; nodeId: Id; parent: Id | null }
  | { changed: false; reason: ReparentRefusal };

/**
 * The change that sets `nodeId`'s parent to `newParentId`, or clears it for null. The node's geometry is not part of
 * the change: geometry is absolute, so the node's absolute rect is identical before and after at any nesting depth.
 * Its descendants go with it unchanged. Any existing node may become a parent (the renderer then draws it as a
 * container); dropTarget is what restricts a drag to containers.
 */
export function reparent(document: Document, nodeId: Id, newParentId: Id | null): ReparentResult {
  const node = findNode(document, nodeId);
  if (!node) {
    return { changed: false, reason: 'unknown-node' };
  }
  if (newParentId === null) {
    return node.parent === undefined ? { changed: false, reason: 'unchanged' } : { changed: true, nodeId, parent: null };
  }
  if (newParentId === nodeId) {
    return { changed: false, reason: 'self' };
  }
  if (!findNode(document, newParentId)) {
    return { changed: false, reason: 'unknown-parent' };
  }
  if (descendantsOf(document, nodeId).has(newParentId)) {
    return { changed: false, reason: 'cycle' };
  }
  if (node.parent === newParentId) {
    return { changed: false, reason: 'unchanged' };
  }
  return { changed: true, nodeId, parent: newParentId };
}

/**
 * The container a dragged node drops into at release, or null for the top level.
 *
 * Containment rule: a container is a candidate when `draggedRect` lies fully inside its visible box, edges inclusive
 * (a rect touching the border from inside is inside). The visible box is the container's unrotated box clipped to each
 * of its ancestors' boxes, because the renderer clips children to their container, so the part of a nested container
 * outside its parent cannot be dropped into. Excluded: the dragged node, its descendants (which move with it),
 * containers without geometry, and any container whose visible box is empty.
 *
 * Innermost wins: the candidate with the deepest ancestor chain. Candidates at the same depth overlap without nesting;
 * the smaller visible area wins, then the later one in document order, which is drawn on top.
 */
export function dropTarget(document: Document, nodeId: Id, draggedRect: Rect): Id | null {
  const nodesById = new Map(document.nodes.map((node) => [node.id, node]));
  const excluded = descendantsOf(document, nodeId);
  excluded.add(nodeId);
  const parents = placedParents(document);

  let best: { id: Id; depth: number; area: number } | undefined;
  for (const node of document.nodes) {
    if (excluded.has(node.id) || !node.geometry || !isContainerGiven(parents, node)) {
      continue;
    }
    const chain = ancestorsOf(nodesById, node);
    const visible = visibleBox(node.geometry, chain);
    if (!visible || !containsRect(visible, draggedRect)) {
      continue;
    }
    const depth = chain.length;
    const area = visible.w * visible.h;
    if (!best || depth > best.depth || (depth === best.depth && area <= best.area)) {
      best = { id: node.id, depth, area };
    }
  }
  return best?.id ?? null;
}

function findNode(document: Document, nodeId: Id): Node | undefined {
  return document.nodes.find((node) => node.id === nodeId);
}

/** The ids named as `parent` by some placed node other than themselves. */
function placedParents(document: Document): Set<Id> {
  const parents = new Set<Id>();
  for (const node of document.nodes) {
    if (node.parent !== undefined && node.parent !== node.id && node.geometry) {
      parents.add(node.parent);
    }
  }
  return parents;
}

function isContainerGiven(placedParentIds: ReadonlySet<Id>, node: Node): boolean {
  return node.kind === 'container' || node.kind === STATE_COMPOSITE_KIND || isSwimlaneKind(node.kind) || (node.geometry !== undefined && placedParentIds.has(node.id));
}

function childrenIndex(document: Document): Map<Id, Id[]> {
  const children = new Map<Id, Id[]>();
  for (const node of document.nodes) {
    if (node.parent !== undefined) {
      const siblings = children.get(node.parent);
      if (siblings) {
        siblings.push(node.id);
      } else {
        children.set(node.parent, [node.id]);
      }
    }
  }
  return children;
}

/** The node's ancestors nearest first, stopping at a dangling, self or cyclic parent. */
function ancestorsOf(nodesById: ReadonlyMap<Id, Node>, node: Node): Node[] {
  const chain: Node[] = [];
  const seen = new Set<Id>([node.id]);
  for (let parent = lookupParent(nodesById, node); parent && !seen.has(parent.id); parent = lookupParent(nodesById, parent)) {
    seen.add(parent.id);
    chain.push(parent);
  }
  return chain;
}

function lookupParent(nodesById: ReadonlyMap<Id, Node>, node: Node): Node | undefined {
  return node.parent === undefined ? undefined : nodesById.get(node.parent);
}

/** The box clipped to every placed ancestor's box, or undefined when nothing of it is left. */
function visibleBox(geometry: Geometry, ancestors: readonly Node[]): Rect | undefined {
  let left = geometry.x;
  let top = geometry.y;
  let right = geometry.x + geometry.w;
  let bottom = geometry.y + geometry.h;
  for (const ancestor of ancestors) {
    const box = ancestor.geometry;
    if (!box) {
      continue;
    }
    left = Math.max(left, box.x);
    top = Math.max(top, box.y);
    right = Math.min(right, box.x + box.w);
    bottom = Math.min(bottom, box.y + box.h);
  }
  return right > left && bottom > top ? { x: left, y: top, w: right - left, h: bottom - top } : undefined;
}

function containsRect(outer: Rect, inner: Rect): boolean {
  return (
    inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h
  );
}
