/**
 * What a drag of the selection does to containers, as pure functions of the document and the drag (checklist items 26
 * and 27). DiagramCanvas calls them; the rules themselves live in model/containers.ts.
 *
 * Moving with contents (item 26): a drag already moves every node inside a dragged container (selection.ts
 * `dragStarts`); `waypointStarts` adds the waypoints of every edge with both ends among the moved nodes, so an edge
 * drawn inside a moved container keeps its shape instead of leaving its bends behind. That is `moveWithContents`'s edge
 * rule applied to the whole moved set, so an edge between two separately selected nodes keeps its shape too.
 *
 * Dropping (item 27): each dragged root (a moved node whose parent is not moving with it) lands in the container
 * model/containers.ts's `dropTarget` rule picks for its box at the release position, or the top level (unless it keeps
 * a saved parent that is not a candidate: see `dropParents`), and `reparent` decides whether that is a change. A root's box is its rotated bounds, as the renderer draws it. Every
 * other moved node is left out of the document the target is picked from, so nothing can drop into a container that is
 * itself moving. Since a frame of the drag can only move the roots, `dropPlan` does everything else that rule reads
 * once, at the press, so a frame tests each root's box against the candidate containers and nothing more.
 */

import { reparent } from '../model/containers.ts';
import { isSwimlaneKind } from '../model/swimlaneKinds.ts';
import type { Document, Geometry, Id, Node, Point } from '../model/types.ts';
import { rotatedBounds, type Rect } from './geometry.ts';

/** The waypoints of every edge whose two ends are both among the moved nodes, as they are at the start of the drag. */
export function waypointStarts(document: Document, moved: ReadonlyMap<Id, unknown>): Map<Id, Point[]> {
  const starts = new Map<Id, Point[]>();
  for (const edge of document.edges) {
    const waypoints = edge.routing?.waypoints;
    if (waypoints !== undefined && waypoints.length > 0 && moved.has(edge.from.node) && moved.has(edge.to.node)) {
      starts.set(edge.id, waypoints.map((point) => ({ x: point.x, y: point.y })));
    }
  }
  return starts;
}

/** The waypoints after shifting their start positions by the drag delta. */
export function translateWaypoints(starts: ReadonlyMap<Id, readonly Point[]>, dx: number, dy: number): Map<Id, Point[]> {
  const moved = new Map<Id, Point[]>();
  for (const [id, points] of starts) {
    moved.set(
      id,
      points.map((point) => ({ x: point.x + dx, y: point.y + dy })),
    );
  }
  return moved;
}

/** A root's own share of a drop plan: its start box and parent, and what it may not drop into. */
interface DropRoot {
  start: Geometry;
  /** The parent it makes a container by being its placed child, if any. */
  placedIn: Id | null;
  /** Its saved parent is not a drop candidate (no geometry, say), so only a drop into another container changes it. */
  keepsParent: boolean;
  /** Every descendant in the document, moved or not, even through an unplaced node: dropping into one is a cycle. */
  excluded: ReadonlySet<Id>;
}

/** A container a root may drop into, with what `dropTarget` ranks it by. */
interface DropCandidate {
  id: Id;
  visible: Rect;
  depth: number;
  area: number;
  /** A container whatever is dragged; otherwise it counts only for a root that is its own child. */
  container: boolean;
}

/**
 * Everything `dropTarget` reads that a drag cannot change, built once when the drag starts so each frame only tests
 * each root's box against the candidate containers. Results match calling model/containers.ts's `dropTarget` per root
 * on the document with every other moved node left out, except that no root ever targets one of its own descendants.
 */
export interface DropPlan {
  roots: ReadonlyMap<Id, DropRoot>;
  candidates: readonly DropCandidate[];
}

/** The drop plan for dragging the moved nodes `starts` in `document`. */
export function dropPlan(document: Document, starts: ReadonlyMap<Id, Geometry>): DropPlan {
  const byId = new Map(document.nodes.map((node) => [node.id, node]));
  const staying = document.nodes.filter((node) => !starts.has(node.id));
  const stayingById = new Map(staying.map((node) => [node.id, node]));
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
  const placedParents = new Set<Id>();
  for (const node of staying) {
    if (node.parent !== undefined && node.parent !== node.id && node.geometry !== undefined) {
      placedParents.add(node.parent);
    }
  }

  const roots = new Map<Id, DropRoot>();
  for (const [id, start] of starts) {
    const node = byId.get(id);
    if (node === undefined || (node.parent !== undefined && starts.has(node.parent))) {
      continue;
    }
    const excluded = new Set<Id>();
    const pending = [...(children.get(id) ?? [])];
    for (let next = pending.pop(); next !== undefined; next = pending.pop()) {
      if (next !== id && !excluded.has(next)) {
        excluded.add(next);
        pending.push(...(children.get(next) ?? []));
      }
    }
    roots.set(id, { start, placedIn: node.parent !== undefined && node.parent !== id && node.geometry !== undefined ? node.parent : null, keepsParent: node.parent !== undefined, excluded });
  }

  const candidates: DropCandidate[] = [];
  for (const node of staying) {
    const box = node.geometry;
    if (box === undefined) {
      continue;
    }
    let left = box.x;
    let top = box.y;
    let right = box.x + box.w;
    let bottom = box.y + box.h;
    let depth = 0;
    const seen = new Set<Id>([node.id]);
    for (let parent = node.parent === undefined ? undefined : stayingById.get(node.parent); parent && !seen.has(parent.id); parent = parent.parent === undefined ? undefined : stayingById.get(parent.parent)) {
      seen.add(parent.id);
      depth++;
      if (parent.geometry) {
        left = Math.max(left, parent.geometry.x);
        top = Math.max(top, parent.geometry.y);
        right = Math.min(right, parent.geometry.x + parent.geometry.w);
        bottom = Math.min(bottom, parent.geometry.y + parent.geometry.h);
      }
    }
    if (right > left && bottom > top) {
      candidates.push({
        id: node.id,
        visible: { x: left, y: top, w: right - left, h: bottom - top },
        depth,
        area: (right - left) * (bottom - top),
        container: node.kind === 'container' || isSwimlaneKind(node.kind) || placedParents.has(node.id),
      });
    }
  }
  const candidateIds = new Set(candidates.map((candidate) => candidate.id));
  for (const [id, root] of roots) {
    const parent = byId.get(id)?.parent;
    roots.set(id, { ...root, keepsParent: root.keepsParent && (parent === undefined || !candidateIds.has(parent)) });
  }
  return { roots, candidates };
}

/** The container each dragged root would drop into (null for the top level) if the drag were released at `delta`. */
export function dropTargets(plan: DropPlan, delta: Point): Map<Id, Id | null> {
  const targets = new Map<Id, Id | null>();
  for (const [id, root] of plan.roots) {
    const rect = rotatedBounds({ ...root.start, x: root.start.x + delta.x, y: root.start.y + delta.y });
    let best: DropCandidate | undefined;
    for (const candidate of plan.candidates) {
      if ((!candidate.container && candidate.id !== root.placedIn) || root.excluded.has(candidate.id) || !contains(candidate.visible, rect)) {
        continue;
      }
      if (!best || candidate.depth > best.depth || (candidate.depth === best.depth && candidate.area <= best.area)) {
        best = candidate;
      }
    }
    targets.set(id, best?.id ?? null);
  }
  return targets;
}

/**
 * The parent changes releasing the drag at `delta` makes: only roots whose container differs from the one they had.
 * A root whose saved parent is not a drop candidate keeps it unless it lands in another container (the atlas keeps
 * saved parents). Each change is checked by `reparent` against the document with the changes before it applied, so
 * the set as a whole never forms a cycle; a change that would is left out.
 */
export function dropParents(document: Document, plan: DropPlan, delta: Point): Map<Id, Id | null> {
  const changes = new Map<Id, Id | null>();
  let working = document;
  for (const [id, target] of dropTargets(plan, delta)) {
    if (target === null && plan.roots.get(id)?.keepsParent === true) {
      continue;
    }
    const result = reparent(working, id, target);
    if (result.changed) {
      changes.set(result.nodeId, result.parent);
      working = { ...working, nodes: working.nodes.map((node) => (node.id === id ? withParent(node, target) : node)) };
    }
  }
  return changes;
}

function withParent(node: Node, parent: Id | null): Node {
  if (parent !== null) {
    return { ...node, parent };
  }
  const next = { ...node };
  delete next.parent;
  return next;
}

function contains(outer: Rect, inner: Rect): boolean {
  return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}
