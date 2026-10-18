/**
 * Selection state transitions and what a drag of the selection moves. Selection is view state held by the canvas,
 * never written to the model (checklist item 1).
 *
 * A plain press replaces the selection with the pressed element, except that pressing an element already in a
 * multi-selection keeps the whole selection so a drag moves every selected node; releasing that press without a drag
 * then collapses the selection to the pressed element. A Shift-press toggles the element in or out and leaves the rest.
 */

import type { Geometry, Id } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';

export function selectOnPress(current: ReadonlySet<Id>, id: Id, shift: boolean): ReadonlySet<Id> {
  if (shift) {
    const next = new Set<Id>(current);
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    return next;
  }
  if (current.has(id)) {
    return current;
  }
  return new Set<Id>([id]);
}

/** The selection after a press on `id` is released: a plain click inside a multi-selection narrows it to `id`. */
export function selectOnRelease(current: ReadonlySet<Id>, id: Id, shift: boolean, dragged: boolean): ReadonlySet<Id> {
  if (shift || dragged || current.size <= 1 || !current.has(id)) {
    return current;
  }
  return new Set<Id>([id]);
}

/**
 * The start geometry of every node a drag of the selection moves: each selected node plus every node drawn inside a
 * selected container, since geometry is absolute and a container that left its children behind would clip them away.
 * Edges are skipped (they follow their endpoints), and a node the document left unplaced starts from its provisional
 * spot, so the drag gives it real geometry where the user dropped it.
 */
export function dragStarts(index: DocumentIndex, selection: ReadonlySet<Id>): Map<Id, Geometry> {
  const starts = new Map<Id, Geometry>();
  const visit = (id: Id): void => {
    const node = index.placed.get(id);
    if (node === undefined || starts.has(id)) {
      return;
    }
    starts.set(id, node.geometry);
    for (const child of index.childrenOf.get(id) ?? []) {
      visit(child.id);
    }
  };
  for (const id of selection) {
    visit(id);
  }
  return starts;
}

/**
 * The single-node selection Tab (step 1) or Shift+Tab (step -1) moves to, walking `order` (node ids in document order;
 * checklist item 21). A multi-selection walks on from its last node in document order, or back from its first, so the
 * walk never revisits what was selected; a selection holding no node in `order` starts at the first node, or the last
 * for Shift+Tab. The walk does not wrap: stepping past either end, or having no nodes at all, returns undefined, so
 * Tab can move focus out of the canvas rather than trapping it there (WCAG 2.1.2, checklist item 28).
 */
export function stepSelection(order: readonly Id[], current: ReadonlySet<Id>, step: 1 | -1): ReadonlySet<Id> | undefined {
  let anchor = -1;
  order.forEach((id, position) => {
    if (current.has(id) && (anchor === -1 || step === 1)) {
      anchor = position;
    }
  });
  const id = order[anchor === -1 ? (step === 1 ? 0 : order.length - 1) : anchor + step];
  return id === undefined ? undefined : new Set<Id>([id]);
}
