/**
 * Arrange: bring to front, send to back, bring forward and send backward (checklist item 25). A pure function: given
 * a document, the selected ids and a command, it returns the document with its `nodes` and `edges` arrays reordered.
 * There is no menu, shortcut, undo or Yjs writing here; the caller owns those and writes the returned order.
 *
 * Where render order lives: it is array order. `Document.nodes` and `Document.edges` are drawn first to last, so a
 * later element paints over an earlier one; the Yjs form carries the same order as each element's `order` field
 * (sidecar/src/yjs/shape.ts) and the saved JSON carries it as array position. Changing the arrays is therefore all it
 * takes for the order to persist after reload: no new field, no schema change.
 *
 * Scoping: the renderer draws a container's children inside the container's own group (DiagramSvg `renderNodes`), so
 * a child's order only means anything against its siblings, and a root node's only against other root nodes. Each
 * command therefore works per sibling group: nodes sharing a `parent` form one group, root nodes form another, and
 * a node never moves past a node from a different group. A `parent` that names no node, names the node itself, or
 * closes a cycle counts as no parent, as documentIndex.ts treats it. Unlike the renderer, grouping ignores geometry:
 * a child still waiting for layout keeps its declared scope, which is the one it will be drawn in once placed.
 * Edges are drawn in one layer above every node, so all edges form a single group of their own; a selected edge
 * moves among edges, and a selected node never moves an edge or the other way round.
 *
 * Within a group, each command reorders only that group's members and writes them back into the array slots the
 * group already occupied, so every other element keeps its index. Array positions across groups carry no meaning
 * (the DSL spec: "document order is the truth", with a container before or after its children equally valid), so
 * a container may end up after its own children in the array; that is fine and is not normalised.
 *
 * The commands, per group, with the order read back to front:
 * - `bringToFront`: the selected members move to the front end, after every unselected member.
 * - `sendToBack`: the selected members move to the back end, before every unselected member.
 * - `bringForward`: each selected member moves one step towards the front, past the next unselected member in the
 *   order. Selected members that are adjacent move together as a block past that one member. A selected member with
 *   no unselected member in front of it stays put. There is no overlap rule: "next" means next in the order, whether
 *   or not the two shapes touch on the canvas. This is the conventional behaviour and keeps the command predictable.
 * - `sendBackward`: the mirror of `bringForward`, one step towards the back past the next unselected member.
 * In every command the selected members keep their relative order, and the unselected members keep theirs.
 *
 * Selection handling: ids that are not in the document are ignored, duplicates count once, and the order the ids are
 * given in does not matter. When a command changes nothing (empty or unknown selection, selection already at the
 * front for `bringToFront`, and so on) the input document itself is returned, so `result === document` means "nothing
 * to do" and the caller can skip the write and the undo step. Otherwise the result is a new document object sharing
 * every node and edge object with the input; the input is never mutated.
 */

import type { Document, Edge, Id, Node } from './types.ts';

export type ArrangeCommand = 'bringToFront' | 'sendToBack' | 'bringForward' | 'sendBackward';

/** The group key for root nodes; `parent` ids are prefixed so a node id can never collide with it. */
const ROOT_GROUP = 'root';

export function arrange(document: Document, selection: Iterable<Id>, command: ArrangeCommand): Document {
  const selected = new Set(selection);
  const nodesById = new Map<Id, Node>();
  for (const node of document.nodes) {
    nodesById.set(node.id, node);
  }
  const nodes = reorder(document.nodes, selected, command, (node) => nodeGroup(nodesById, node));
  const edges = reorder(document.edges, selected, command, () => ROOT_GROUP);
  if (nodes === document.nodes && edges === document.edges) {
    return document;
  }
  return { ...document, nodes, edges };
}

/**
 * Applies the command to each group of `elements` and writes every group back into its own slots. Returns the input
 * array itself when no element moves.
 */
function reorder<T extends Node | Edge>(elements: readonly T[], selected: ReadonlySet<Id>, command: ArrangeCommand, groupOf: (element: T) => string): T[] {
  const groups = new Map<string, { slots: number[]; members: T[] }>();
  for (const [slot, element] of elements.entries()) {
    const key = groupOf(element);
    const group = groups.get(key) ?? { slots: [], members: [] };
    group.slots.push(slot);
    group.members.push(element);
    groups.set(key, group);
  }
  let result: T[] | undefined;
  for (const { slots, members } of groups.values()) {
    const arranged = arrangeGroup(members, selected, command);
    for (const [i, element] of arranged.entries()) {
      if (element !== members[i]) {
        result ??= [...elements];
        result[slots[i] ?? i] = element;
      }
    }
  }
  return result ?? (elements as T[]);
}

/** One group's members, back to front, after the command. */
function arrangeGroup<T extends Node | Edge>(members: readonly T[], selected: ReadonlySet<Id>, command: ArrangeCommand): T[] {
  const isSelected = (member: T): boolean => selected.has(member.id);
  switch (command) {
    case 'bringToFront':
      return [...members.filter((m) => !isSelected(m)), ...members.filter(isSelected)];
    case 'sendToBack':
      return [...members.filter(isSelected), ...members.filter((m) => !isSelected(m))];
    case 'bringForward': {
      // Walk from the front so a block of adjacent selected members each step past the same unselected member.
      const out = [...members];
      for (let i = out.length - 2; i >= 0; i--) {
        swapIfCrossing(out, i, i + 1, isSelected);
      }
      return out;
    }
    case 'sendBackward': {
      const out = [...members];
      for (let i = 1; i < out.length; i++) {
        swapIfCrossing(out, i, i - 1, isSelected);
      }
      return out;
    }
  }
}

/** Swaps `from` with its neighbour `to` when `from` is selected and `to` is not. */
function swapIfCrossing<T>(members: T[], from: number, to: number, isSelected: (member: T) => boolean): void {
  const mover = members[from] as T;
  const other = members[to] as T;
  if (isSelected(mover) && !isSelected(other)) {
    members[from] = other;
    members[to] = mover;
  }
}

/** The sibling group a node is drawn in: its parent's, or the root group when the parent does not resolve. */
function nodeGroup(nodesById: ReadonlyMap<Id, Node>, node: Node): string {
  const parent = node.parent !== undefined ? nodesById.get(node.parent) : undefined;
  if (!parent || parent.id === node.id || hasAncestor(nodesById, parent, node.id)) {
    return ROOT_GROUP;
  }
  return `parent:${parent.id}`;
}

/** True when `candidate` is on `start`'s parent chain; stops at a dangling parent or a cycle not through it. */
function hasAncestor(nodesById: ReadonlyMap<Id, Node>, start: Node, candidate: Id): boolean {
  const seen = new Set<Id>();
  let current: Node | undefined = start;
  while (current?.parent !== undefined && !seen.has(current.id)) {
    seen.add(current.id);
    if (current.parent === candidate) {
      return true;
    }
    current = nodesById.get(current.parent);
  }
  return false;
}
