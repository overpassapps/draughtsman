/**
 * Group, ungroup and fit to contents (checklist items 22 and 26) as pure plans over a document. They read a document
 * and return what to change; the editor (model/editor.ts, editor/useDocumentEditor.ts) writes it to the Y.Doc as one
 * undo step. Geometry is absolute (model/containers.ts), so none of them ever moves a child: grouping and ungrouping
 * change `parent` and render order only, and fitting changes the container's own geometry only.
 *
 * Padding: a fitted or newly grouped container leaves CONTAINER_PADDING_STEPS grid steps (the diagram theme's
 * `grid.size`) on all four sides. Two steps, because a container draws its label inside its top-left corner
 * (NodeView.tsx), and one 20-unit step would let that label sit on a child tucked into the corner; two grid units is
 * also the export dialog's default padding (design/atlas/export-dialog.md). Neither the theme nor the atlas names a
 * container padding of its own.
 *
 * Nesting, one level: fitting resizes the containers asked for, deepest first so a selected outer container fits a
 * selected inner one's new box. When a fitted or new container then crosses an edge of its own (unselected) parent,
 * that parent grows on the crossed sides only, to leave the same padding, so the renderer's clip never cuts the
 * container off; it never shrinks and its children do not move. The growth stops there: a grandparent the grown
 * parent now crosses is not grown in turn.
 */

import { fitToContents } from './containers.ts';
import type { Document, Geometry, Id, Node } from './types.ts';

/** Grid steps of padding a fitted or grouped container leaves on every side of its children. */
export const CONTAINER_PADDING_STEPS = 2;

/** The padding, in canvas units, for a diagram theme's grid size. */
export function containerPadding(gridSize: number): number {
  return CONTAINER_PADDING_STEPS * Math.max(0, gridSize);
}

/** What a new group is made of. */
export const GROUP_TEMPLATE = { kind: 'container', shape: 'rectangle', label: 'Container' } as const;

/** A container id in the project's shape (types.ts: "c_vpc"). */
export type ContainerIdGenerator = () => Id;
export const randomContainerId: ContainerIdGenerator = () => `c_${Math.random().toString(36).slice(2, 10)}`;
const MAX_ID_ATTEMPTS = 1000;

export interface GroupPlan {
  /** The new container, fitted around the grouped nodes, with the parent they had in common (if any). */
  container: Node;
  /** The grouped nodes: the selected placed nodes that have no selected ancestor. Each gets the container as parent. */
  children: Id[];
  /** Every node in its new render order: the container sits where the topmost grouped node was. */
  nodes: Node[];
  /** The container's parent grown to hold it (see Nesting above), when the new container crosses its edge. */
  resized: Map<Id, Geometry>;
}

/**
 * Groups the selected nodes into a new container (checklist item 22). Only the selection's top-most nodes are grouped:
 * a node whose ancestor is also selected travels inside that ancestor. Edges in the selection are ignored, and so are
 * nodes without geometry, which are waiting for layout and have no box to fit around. The container takes the
 * grouped nodes' nearest common ancestor as its parent (none when they have none in common), so the group stays inside
 * whatever held all of them, and it is sized by `fitToContents` with `padding` on every side. Render order: the
 * container is inserted just before the topmost (last in document order) grouped node, so the group sits at the level
 * its highest member was drawn at. Returns undefined when nothing selected can be grouped.
 */
export function group(document: Document, selection: ReadonlySet<Id>, padding: number, generateId: ContainerIdGenerator = randomContainerId): GroupPlan | undefined {
  const byId = new Map(document.nodes.map((node) => [node.id, node]));
  const children = document.nodes
    .filter((node) => selection.has(node.id) && node.geometry !== undefined && !ancestorIds(byId, node).some((id) => selection.has(id) && byId.get(id)?.geometry !== undefined))
    .map((node) => node.id);
  const last = children.at(-1);
  if (last === undefined) {
    return undefined;
  }
  const id = unusedId(document, generateId);
  const parent = commonAncestor(byId, children);
  const draft: Node = { id, kind: GROUP_TEMPLATE.kind, label: GROUP_TEMPLATE.label, shape: GROUP_TEMPLATE.shape, ...(parent === undefined ? {} : { parent }) };
  const grouped = new Set(children);
  const nodes: Node[] = [];
  for (const node of document.nodes) {
    if (node.id === last) {
      nodes.push(draft);
    }
    nodes.push(grouped.has(node.id) ? { ...node, parent: id } : node);
  }
  const geometry = fitToContents({ ...document, nodes }, id, padding);
  if (geometry === undefined) {
    return undefined;
  }
  const container: Node = { ...draft, geometry };
  const placed = nodes.map((node) => (node.id === id ? container : node));
  const grown = grownParent(new Map(placed.map((node) => [node.id, node])), container, padding);
  return { container, children, nodes: placed, resized: new Map(grown ? [grown] : []) };
}

export interface UngroupPlan {
  /** The containers removed. */
  containers: Set<Id>;
  /** Each released child and its new parent: the nearest ancestor of its container that is not also removed. */
  parents: Map<Id, Id | null>;
  /** The remaining nodes in their new render order: each container's children take its place in the stack. */
  nodes: Node[];
  /** The selected containers left in place because an edge is attached to them, which removing them would delete. */
  refused: Id[];
}

/**
 * Ungroups every selected container-kind node (checklist item 22): its children move up to the container's own parent
 * (or the top level), keeping their absolute geometry, and the container is removed. Ungrouping never deletes a
 * connection: a selected container with an edge attached to it stays, children and all, and is listed in `refused`, while
 * the other selected containers still ungroup; edges between the children are untouched. Only kind `container` ungroups: a node of another kind that
 * happens to hold children is a real element of the diagram, and ungrouping must never delete one. Selected containers
 * nested in each other release their children to the nearest ancestor that stays. The released children are drawn
 * where their container was, in their existing relative order, so nothing jumps above or below its neighbours.
 * Returns undefined when no container is selected.
 */
export function ungroup(document: Document, selection: ReadonlySet<Id>): UngroupPlan | undefined {
  const selected = document.nodes.filter((node) => selection.has(node.id) && node.kind === GROUP_TEMPLATE.kind).map((node) => node.id);
  if (selected.length === 0) {
    return undefined;
  }
  const refused = selected.filter((id) => document.edges.some((edge) => edge.from.node === id || edge.to.node === id));
  const containers = new Set(selected.filter((id) => !refused.includes(id)));
  const byId = new Map(document.nodes.map((node) => [node.id, node]));
  const parents = new Map<Id, Id | null>();
  const released = new Map<Id, Node[]>();
  for (const node of document.nodes) {
    if (node.parent === undefined || !containers.has(node.parent) || containers.has(node.id)) {
      continue;
    }
    parents.set(node.id, ancestorIds(byId, node).find((id) => !containers.has(id)) ?? null);
    released.set(node.parent, [...(released.get(node.parent) ?? []), node]);
  }

  // A released child is drawn at its container's slot, in its existing order among its siblings, and not at its own.
  const nodes: Node[] = [];
  for (const node of document.nodes) {
    if (containers.has(node.id)) {
      nodes.push(...(released.get(node.id) ?? []).map((child) => withParent(child, parents.get(child.id) ?? null)));
    } else if (!parents.has(node.id)) {
      nodes.push(node);
    }
  }
  return { containers, parents, nodes, refused };
}

/**
 * The new geometry of every selected node that holds placed children, fitted around them with `padding` on every side
 * (checklist item 26), deepest first so an outer selected container fits around an inner one's new box. A container
 * already fitted, or one with no placed children, is left out.
 */
export function fitContainers(document: Document, selection: ReadonlySet<Id>, padding: number): Map<Id, Geometry> {
  const byId = new Map(document.nodes.map((node) => [node.id, node]));
  const targets = document.nodes
    .filter((node) => selection.has(node.id))
    .map((node) => ({ id: node.id, depth: ancestorIds(byId, node).length }))
    .sort((a, b) => b.depth - a.depth);
  const fitted = new Map<Id, Geometry>();
  let working = document;
  const place = (id: Id, geometry: Geometry): void => {
    fitted.set(id, geometry);
    working = { ...working, nodes: working.nodes.map((node) => (node.id === id ? { ...node, geometry } : node)) };
  };
  for (const { id } of targets) {
    const geometry = fitToContents(working, id, padding);
    if (geometry !== undefined) {
      place(id, geometry);
    }
  }
  // One level of growth, for parents that were not fitted themselves.
  for (const { id } of targets) {
    const node = working.nodes.find((candidate) => candidate.id === id);
    const grown = fitted.has(id) && node !== undefined && !selection.has(node.parent ?? '') ? grownParent(new Map(working.nodes.map((n) => [n.id, n])), node, padding) : undefined;
    if (grown !== undefined) {
      place(...grown);
    }
  }
  return fitted;
}

/** Whether any selected node holds a placed child, so "Fit to contents" has something to fit around. */
export function canFitContents(document: Document, selection: ReadonlySet<Id>): boolean {
  return document.nodes.some((node) => node.parent !== undefined && node.parent !== node.id && node.geometry !== undefined && selection.has(node.parent));
}

/**
 * The parent's geometry grown so `child`'s box sits inside it with `padding` on every side the child crosses, or
 * undefined when the child is inside its parent (or either has no geometry). Sides the child does not cross stay put.
 */
function grownParent(byId: ReadonlyMap<Id, Node>, child: Node, padding: number): [Id, Geometry] | undefined {
  const parent = child.parent === undefined || child.parent === child.id ? undefined : byId.get(child.parent);
  const box = child.geometry;
  const outer = parent?.geometry;
  if (parent === undefined || box === undefined || outer === undefined) {
    return undefined;
  }
  const left = box.x < outer.x ? box.x - padding : outer.x;
  const top = box.y < outer.y ? box.y - padding : outer.y;
  const right = box.x + box.w > outer.x + outer.w ? box.x + box.w + padding : outer.x + outer.w;
  const bottom = box.y + box.h > outer.y + outer.h ? box.y + box.h + padding : outer.y + outer.h;
  if (left === outer.x && top === outer.y && right === outer.x + outer.w && bottom === outer.y + outer.h) {
    return undefined;
  }
  return [parent.id, { ...outer, x: left, y: top, w: right - left, h: bottom - top }];
}

function withParent(node: Node, parent: Id | null): Node {
  if (parent !== null) {
    return { ...node, parent };
  }
  const next = { ...node };
  delete next.parent;
  return next;
}

/** The node's ancestor ids nearest first, stopping at a dangling, self or cyclic parent (as containers.ts does). */
function ancestorIds(byId: ReadonlyMap<Id, Node>, node: Node): Id[] {
  const chain: Id[] = [];
  const seen = new Set<Id>([node.id]);
  for (let parent = node.parent === undefined ? undefined : byId.get(node.parent); parent && !seen.has(parent.id); parent = parent.parent === undefined ? undefined : byId.get(parent.parent)) {
    seen.add(parent.id);
    chain.push(parent.id);
  }
  return chain;
}

/** The deepest node that is an ancestor of every given node, or undefined when they share none. */
function commonAncestor(byId: ReadonlyMap<Id, Node>, ids: readonly Id[]): Id | undefined {
  const chains = ids.map((id) => {
    const node = byId.get(id);
    return node === undefined ? [] : ancestorIds(byId, node);
  });
  const [first, ...rest] = chains;
  return first?.find((candidate) => rest.every((chain) => chain.includes(candidate)));
}

function unusedId(document: Document, generateId: ContainerIdGenerator): Id {
  const taken = new Set<Id>([...document.nodes.map((node) => node.id), ...document.edges.map((edge) => edge.id)]);
  for (let attempt = 0; attempt < MAX_ID_ATTEMPTS; attempt++) {
    const id = generateId();
    if (!taken.has(id)) {
      return id;
    }
  }
  throw new Error(`No unused container id after ${String(MAX_ID_ATTEMPTS)} attempts; the id generator is stuck.`);
}
