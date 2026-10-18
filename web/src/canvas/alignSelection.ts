/**
 * Align and distribute applied to a selection (checklist item 24): the editor-side half of align.ts, which works on
 * bare rectangles. This picks the selection's nodes out of the document index, hands their rectangles to align.ts, and
 * turns the positions it returns into the absolute geometry of every node that moves, ready for one Y.Doc write.
 *
 * What takes part: every selected id the index draws as a node, provisional placement included, so a node still
 * waiting for layout aligns from where the user sees it (as a drag moves it from there). Edges and ids that name
 * nothing are ignored. Every node is drawn (documentIndex.ts gives an unplaced one a provisional spot), so the number
 * taking part is the number of selected node ids, which is what the editor counts against MIN_ALIGN_NODES and
 * MIN_DISTRIBUTE_NODES to enable the commands.
 *
 * Containers carry their contents, as a drag does (selection.ts `dragStarts`): geometry is absolute, so a container
 * that moved without its children would leave them outside its clip. Each selected node's move is applied to the
 * children the index draws inside it, transitively, stopping at a child that is itself selected, since that child
 * aligns to its own target; that child's contents then follow it. A moved node's own position is align.ts's value
 * exactly, never start-plus-delta, so the edges and centres coincide exactly as the checklist asks.
 */

import type { Geometry, Id } from '../model/types.ts';
import { translateGeometry } from '../model/editor.ts';
import { alignNodes, distributeNodes, type AlignCommand, type DistributeCommand, type Moves } from './align.ts';
import type { DocumentIndex } from './documentIndex.ts';
import type { Rect } from './geometry.ts';

/** The geometry of every node that moves when the selection is aligned; empty when nothing moves. */
export function alignSelection(index: DocumentIndex, selection: ReadonlySet<Id>, command: AlignCommand): Map<Id, Geometry> {
  return withContents(index, selection, alignNodes(selectedRects(index, selection), command));
}

/** The geometry of every node that moves when the selection is distributed; empty when nothing moves. */
export function distributeSelection(index: DocumentIndex, selection: ReadonlySet<Id>, command: DistributeCommand): Map<Id, Geometry> {
  return withContents(index, selection, distributeNodes(selectedRects(index, selection), command));
}

/** Unrotated boxes of the selected drawn nodes, as align.ts takes them. */
function selectedRects(index: DocumentIndex, selection: ReadonlySet<Id>): Map<Id, Rect> {
  const rects = new Map<Id, Rect>();
  for (const id of selection) {
    const node = index.placed.get(id);
    if (node !== undefined) {
      const { x, y, w, h } = node.geometry;
      rects.set(id, { x, y, w, h });
    }
  }
  return rects;
}

function withContents(index: DocumentIndex, selection: ReadonlySet<Id>, moves: Moves): Map<Id, Geometry> {
  const result = new Map<Id, Geometry>();
  const carry = (parent: Id, dx: number, dy: number): void => {
    for (const child of index.childrenOf.get(parent) ?? []) {
      if (!selection.has(child.id) && !result.has(child.id)) {
        result.set(child.id, translateGeometry(child.geometry, dx, dy));
        carry(child.id, dx, dy);
      }
    }
  };
  for (const [id, target] of moves) {
    const node = index.placed.get(id);
    if (node === undefined) {
      continue;
    }
    result.set(id, { ...node.geometry, x: target.x, y: target.y });
    carry(id, target.x - node.geometry.x, target.y - node.geometry.y);
  }
  return result;
}
