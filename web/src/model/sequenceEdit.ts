/**
 * The edits a sequence diagram's editor makes (design/atlas/sequence-editor.md). A sequence document stores no
 * position: time is the order of `edges[]` and left-to-right is the order of `nodes[]` (model/sequenceScene.ts), so
 * every edit here is a change of order, and where a pointer lands is turned into an order by reading the derived
 * scene. Pure functions over the scene and document, plus the one Yjs write that has no other home, an insert at an
 * index.
 *
 * Slots. A message's row is its `rowBand`; the pointer's y picks the slot among the messages that stay where they are
 * (`skip` names the one being dragged, which is not among them): it goes before the first whose row midpoint is below
 * the pointer, else last. A participant's slot is the same over the lifelines' x. `slotLineY` and `slotLineX` give
 * the insertion line the canvas draws for a slot, in the layout as it stands.
 */

import * as Y from 'yjs';
import { randomIdGenerator, type IdGenerator } from './clipboard.ts';
import { createNode, type NodeTemplate } from './create.ts';
import { EDGES_MAP, LOCAL_ORIGIN, NODES_MAP, ORDER_FIELD, writeEdge, yDocToDocument } from './editor.ts';
import { SEQUENCE_METRICS, type SequenceMessage, type SequenceScene } from './sequenceScene.ts';
import type { Document, Edge, Id, Node, Point } from './types.ts';

/** The kind a message drawn with the connector tool is given (the vocabulary's first edge kind). */
export const NEW_MESSAGE_KIND = 'sync';

/** The vertical extent of a message's row, in world units. */
export function rowBand(message: Pick<SequenceMessage, 'y' | 'self'>): { top: number; bottom: number; mid: number } {
  const m = SEQUENCE_METRICS;
  const top = message.self ? message.y - m.selfTop : message.y - m.rowHeight / 2;
  const height = message.self ? m.selfRowHeight : m.rowHeight;
  return { top, bottom: top + height, mid: top + height / 2 };
}

/**
 * The point a message's label is centred on, drawn or being edited: where the scene puts its label plate, or where
 * one would go for a message that has none yet (above its line's middle; a self message's bracket, above its top).
 */
export function messageLabelAnchor(message: SequenceMessage): Point {
  if (message.labelAt !== undefined) {
    return message.labelAt;
  }
  const m = SEQUENCE_METRICS;
  const first = message.points[0] ?? { x: 0, y: message.y };
  const second = message.points[1] ?? first;
  return message.self ? { x: first.x + m.selfWidth, y: message.y - m.labelLift } : { x: (first.x + second.x) / 2, y: message.y - m.labelLift };
}

function stayingMessages(scene: SequenceScene, skip: Id | undefined): SequenceMessage[] {
  return scene.messages.filter((message) => message.id !== skip);
}

/** The slot among the messages other than `skip` that a pointer at world `y` picks: 0 puts it first, `length` last. */
export function messageSlot(scene: SequenceScene, y: number, skip?: Id): number {
  return stayingMessages(scene, skip).filter((message) => rowBand(message).mid < y).length;
}

/** Where the insertion line for `slot` is drawn: the top of the row the message would sit above, or the end of the last row. */
export function slotLineY(scene: SequenceScene, slot: number, skip?: Id): number {
  const others = stayingMessages(scene, skip);
  const next = others[slot];
  if (next !== undefined) {
    return rowBand(next).top;
  }
  const last = others[others.length - 1];
  const first = scene.lifelines[0];
  return last !== undefined ? rowBand(last).bottom : (first?.y0 ?? 0) + SEQUENCE_METRICS.firstRowOffset;
}

/** The slot among the participants other than `skip` that a pointer at world `x` picks: 0 is leftmost. */
export function participantSlot(scene: SequenceScene, x: number, skip?: Id): number {
  return scene.headers.filter((header) => header.id !== skip && header.cx < x).length;
}

/** Where the vertical insertion line for a participant `slot` is drawn: in the gap before the header it would precede. */
export function slotLineX(scene: SequenceScene, slot: number, skip?: Id): number {
  const others = scene.headers.filter((header) => header.id !== skip);
  const next = others[slot];
  if (next !== undefined) {
    return next.x - SEQUENCE_METRICS.headerGap / 2;
  }
  const last = others[others.length - 1];
  return last === undefined ? SEQUENCE_METRICS.margin : last.x + last.w + SEQUENCE_METRICS.headerGap / 2;
}

/**
 * The index in `document.edges` a new message goes at, for a `slot` among the drawn messages (`messageSlot`): in front
 * of the edge that is drawn at that slot, or at the end. An edge that is not drawn (an end that is not a participant)
 * has no row and keeps its place relative to the others.
 */
export function insertionIndex(document: Document, scene: SequenceScene, slot: number, skip?: Id): number {
  const rest = document.edges.filter((edge) => edge.id !== skip);
  const before = stayingMessages(scene, skip)[slot];
  const at = before === undefined ? -1 : rest.findIndex((edge) => edge.id === before.id);
  return at < 0 ? rest.length : at;
}

/** `items` with `id` moved to `index` of the list that remains once it is taken out; undefined when that changes nothing. */
function moved<T extends { id: Id }>(items: readonly T[], id: Id, index: number): T[] | undefined {
  const from = items.findIndex((item) => item.id === id);
  const item = items[from];
  if (item === undefined) {
    return undefined;
  }
  const rest = items.filter((candidate) => candidate.id !== id);
  const at = Math.max(0, Math.min(index, rest.length));
  if (at === from) {
    return undefined;
  }
  return [...rest.slice(0, at), item, ...rest.slice(at)];
}

/** The document's edges with message `id` dropped at world `y`, or undefined when it would not move. */
export function reorderedMessages(document: Document, scene: SequenceScene, id: Id, y: number): Edge[] | undefined {
  const reordered = moved(document.edges, id, insertionIndex(document, scene, messageSlot(scene, y, id), id));
  // Notes and any edge the scene does not draw keep their own place; only the drawn message order is the user's to change.
  return reordered === undefined || sameDrawnOrder(document.edges, reordered, scene.messages) ? undefined : reordered;
}

/** The document's nodes with participant `id` dropped at world `x`, or undefined when it would not move. */
export function reorderedParticipants(document: Document, scene: SequenceScene, id: Id, x: number): Node[] | undefined {
  // A fragment is a frame, not a lifeline: it has no place among the participants to move to.
  if (!scene.headers.some((header) => header.id === id)) {
    return undefined;
  }
  const slot = participantSlot(scene, x, id);
  const rest = document.nodes.filter((node) => node.id !== id);
  const before = scene.headers.filter((header) => header.id !== id)[slot];
  const at = before === undefined ? -1 : rest.findIndex((node) => node.id === before.id);
  const reordered = moved(document.nodes, id, at < 0 ? rest.length : at);
  // Fragments sit among the nodes but are not lifelines: a drop that leaves the drawn order alone (the last participant
  // taken "past the end", which is past a fragment stored after it) is not a move, or it would dirty the document.
  return reordered === undefined || sameDrawnOrder(document.nodes, reordered, scene.headers) ? undefined : reordered;
}

/** True when the elements of `drawn` come in the same order in `before` and `after`. */
function sameDrawnOrder(before: readonly { id: Id }[], after: readonly { id: Id }[], drawn: readonly { id: Id }[]): boolean {
  const ids = new Set(drawn.map((element) => element.id));
  const order = (items: readonly { id: Id }[]): string => items.filter((item) => ids.has(item.id)).map((item) => item.id).join('\n');
  return order(before) === order(after);
}

/** A new participant of `template` for the right-hand end of the diagram; it carries no geometry. */
export function createParticipant(document: Document, template: NodeTemplate, generateId: IdGenerator = randomIdGenerator): Node {
  // createNode owns id generation and collision; the position it computes is never read for this type.
  const { id } = createNode(document, { x: 0, y: 0 }, 1, generateId, template);
  return { id, kind: template.kind, label: template.label, shape: template.shape };
}

/** A new `sync` message between two participants with an id the document does not use. */
export function createMessage(document: Document, from: Id, to: Id, generateId: IdGenerator = randomIdGenerator): Edge {
  const taken = new Set([...document.nodes, ...document.edges].map((element) => element.id));
  let id = generateId('edge');
  for (let attempt = 0; taken.has(id); attempt++) {
    if (attempt > 1000) {
      throw new Error('Could not generate a free edge id in 1000 attempts.');
    }
    id = generateId('edge');
  }
  return { id, kind: NEW_MESSAGE_KIND, from: { node: from }, to: { node: to } };
}

/**
 * Inserts `edge` at `index` of the edge order in one transaction under the given origin, renumbering `order` only on
 * the edges that follow it (the ones whose place changed). Throws, writing nothing, when the id is taken or an end is
 * not a node of the document. An index past either end is clamped.
 */
export function insertEdgeAt(doc: Y.Doc, edge: Edge, index: number, origin: unknown = LOCAL_ORIGIN): void {
  const nodes = doc.getMap<Y.Map<unknown>>(NODES_MAP);
  const edges = doc.getMap<Y.Map<unknown>>(EDGES_MAP);
  if (nodes.has(edge.id) || edges.has(edge.id)) {
    throw new Error(`Cannot add edge ${edge.id}: the id is already in the document`);
  }
  for (const end of [edge.from.node, edge.to.node]) {
    if (!nodes.has(end)) {
      throw new Error(`Cannot add edge ${edge.id}: node ${end} is not in the document`);
    }
  }
  const ids = yDocToDocument(doc).edges.map((existing) => existing.id);
  const at = Math.max(0, Math.min(index, ids.length));
  ids.splice(at, 0, edge.id);
  doc.transact(() => {
    writeEdge(edges, edge, at);
    ids.forEach((id, order) => {
      const map = edges.get(id);
      if (map !== undefined && map.get(ORDER_FIELD) !== order) {
        map.set(ORDER_FIELD, order);
      }
    });
  }, origin);
}
