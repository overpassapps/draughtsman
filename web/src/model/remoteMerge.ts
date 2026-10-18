/**
 * Merges a document another writer saved (an agent's `patch_document`, another tab's save) into the open Y.Doc without
 * losing the local user's unsaved edits (checklist item 30). It is a three-way merge, field by field, over the
 * element maps editor.ts builds:
 *
 * - `base` is the snapshot the local edits started from: the document as loaded, or as last saved or merged.
 * - `remote` is the snapshot now stored on the server.
 * - the Y.Doc is the local state, the base plus whatever the user has done since.
 *
 * A field (a header field, or one field of a node or edge) the remote changed and the local user did not takes the
 * remote value; one the local user changed and the remote did not keeps the local value; one both changed to the same
 * value is left alone; one both changed differently keeps the local value and is reported as a conflict. Elements are
 * handled the same way: one the remote added is added, one it removed is removed unless the user edited it (or an
 * element the remote does not know about still refers to it), and one the user removed stays removed, reported as a
 * conflict only when the remote edited it. Render order follows the remote when it reordered and the user did not.
 * Meta is the server's (the user never edits it), so it is taken whole.
 *
 * Everything is written in one transaction under the given origin, REMOTE_ORIGIN by default, which the undo manager
 * does not track: Cmd/Ctrl+Z never undoes another writer's change. Nothing here throws on a dangling edge or parent:
 * the server accepts those with a warning (docs/api.md), so the merge must too.
 */

import * as Y from 'yjs';
import { NODES_MAP, EDGES_MAP, HEADER_MAP, META_MAP, ORDER_FIELD, REMOTE_ORIGIN, LOCAL_ORIGIN, nextOrder, writeEdge, writeNode, removeElements } from './editor.ts';
import type { Document, Edge, Id, Node } from './types.ts';

type ElementMap = Y.Map<unknown>;
type ElementKind = 'node' | 'edge';

/** The fields of an element that merge one by one; `id` is the map key and `order` is merged as a sequence. */
const NODE_FIELDS = ['kind', 'label', 'shape', 'geometry', 'style', 'props', 'parent', 'ports'] as const;
const EDGE_FIELDS = ['kind', 'label', 'from', 'to', 'routing', 'style', 'props'] as const;
const HEADER_FIELDS = ['title', 'theme', 'flythrough'] as const;

/**
 * Where the local user's unsaved edit and the other writer's change could not both stand. The local side is what the
 * Y.Doc shows; `takeRemote` applies the remote side instead.
 *
 * - `both-changed`: both sides changed `fields` of the element (or, for `document`, the header fields) differently.
 * - `deleted-remotely`: the other writer removed an element the local user edited, or that a local edit still refers to.
 * - `deleted-locally`: the local user removed an element the other writer edited.
 * - `order`: both sides changed the render order.
 */
export interface MergeConflict {
  kind: ElementKind | 'document';
  id: Id;
  reason: 'both-changed' | 'deleted-remotely' | 'deleted-locally' | 'order';
  fields: string[];
}

export interface MergeResult {
  /** Whether the merge wrote anything to the Y.Doc. */
  changed: boolean;
  conflicts: MergeConflict[];
}

/** Merges `remote` into the Y.Doc, as a change from `base` (see the module comment). */
export function mergeRemote(doc: Y.Doc, base: Document, remote: Document, origin: unknown = REMOTE_ORIGIN): MergeResult {
  const conflicts: MergeConflict[] = [];
  let changed = false;
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);

  doc.transact(() => {
    const header = doc.getMap<unknown>(HEADER_MAP);
    const headerFields: string[] = [];
    for (const field of HEADER_FIELDS) {
      const outcome = mergeField(header, field, base[field], remote[field]);
      changed ||= outcome === 'applied';
      if (outcome === 'conflict') {
        headerFields.push(field);
      }
    }
    if (headerFields.length > 0) {
      conflicts.push({ kind: 'document', id: remote.id, reason: 'both-changed', fields: headerFields });
    }

    const meta = doc.getMap<unknown>(META_MAP);
    for (const field of ['createdAt', 'updatedAt', 'createdBy', 'aiProvenance'] as const) {
      changed = writeField(meta, field, remote.meta[field]) || changed;
    }

    const nodeResult = mergeElements('node', nodes, base.nodes, remote.nodes, NODE_FIELDS);
    const edgeResult = mergeElements('edge', edges, base.edges, remote.edges, EDGE_FIELDS);
    conflicts.push(...nodeResult.conflicts, ...edgeResult.conflicts);
    changed = nodeResult.changed || edgeResult.changed || changed;

    // Removals last, once every addition and field change is in, so a reference check sees the merged state.
    changed = removeRemotelyDeleted(nodes, edges, base, remote, conflicts) || changed;

    const nodeOrder = mergeOrder(nodes, base.nodes, remote.nodes);
    const edgeOrder = mergeOrder(edges, base.edges, remote.edges);
    changed = nodeOrder.changed || edgeOrder.changed || changed;
    if (nodeOrder.conflict || edgeOrder.conflict) {
      conflicts.push({ kind: 'document', id: remote.id, reason: 'order', fields: [] });
    }
  }, origin);

  return { changed, conflicts };
}

/**
 * Resolves conflicts in favour of the other writer: each conflicted field, element or order takes its value in
 * `remote`. A user decision, so it runs under the local origin by default and is one undo step for the caller to bound.
 */
export function takeRemote(doc: Y.Doc, remote: Document, conflicts: readonly MergeConflict[], origin: unknown = LOCAL_ORIGIN): void {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  const header = doc.getMap<unknown>(HEADER_MAP);
  doc.transact(() => {
    for (const conflict of conflicts) {
      if (conflict.kind === 'document') {
        if (conflict.reason === 'order') {
          writeOrder(nodes, remote.nodes);
          writeOrder(edges, remote.edges);
        } else {
          for (const field of conflict.fields) {
            writeField(header, field, (remote as unknown as Record<string, unknown>)[field]);
          }
        }
        continue;
      }
      const map = conflict.kind === 'node' ? nodes : edges;
      const theirs = (conflict.kind === 'node' ? remote.nodes : remote.edges).find((element) => element.id === conflict.id);
      const mine = map.get(conflict.id);
      if (theirs === undefined) {
        // The other writer removed it; removing it here takes its attached edges and contents with it, as a delete does.
        if (mine !== undefined) {
          removeElements(doc, new Set([conflict.id]), origin);
        }
      } else if (mine === undefined) {
        const edge = conflict.kind === 'edge' ? (theirs as Edge) : undefined;
        if (edge === undefined) {
          writeNode(nodes, theirs as Node, nextOrder(nodes));
        } else if (nodes.has(edge.from.node) && nodes.has(edge.to.node)) {
          writeEdge(edges, edge, nextOrder(edges));
        }
      } else {
        for (const field of conflict.fields) {
          writeField(mine, field, (theirs as unknown as Record<string, unknown>)[field]);
        }
      }
    }
  }, origin);
}

type FieldOutcome = 'unchanged' | 'applied' | 'conflict';

/** The three-way rule for one field; writes the remote value when only the remote changed it. */
function mergeField(map: Y.Map<unknown>, field: string, base: unknown, remote: unknown): FieldOutcome {
  if (jsonEqual(remote, base)) {
    return 'unchanged';
  }
  const local = readField(map, field);
  if (jsonEqual(local, base)) {
    writeField(map, field, remote);
    return 'applied';
  }
  return jsonEqual(local, remote) ? 'unchanged' : 'conflict';
}

function mergeElements(
  kind: ElementKind,
  map: Y.Map<ElementMap>,
  baseElements: readonly (Node | Edge)[],
  remoteElements: readonly (Node | Edge)[],
  fields: readonly string[],
): MergeResult {
  const conflicts: MergeConflict[] = [];
  let changed = false;
  const baseById = new Map(baseElements.map((element) => [element.id, element]));
  let order = nextOrder(map);
  for (const theirs of remoteElements) {
    const record = theirs as unknown as Record<string, unknown>;
    const ancestor = baseById.get(theirs.id) as unknown as Record<string, unknown> | undefined;
    const mine = map.get(theirs.id);
    if (ancestor === undefined) {
      if (mine === undefined) {
        // Added by the other writer: after everything local, in the remote's own order.
        if (kind === 'node') {
          writeNode(map, theirs as Node, order);
        } else {
          writeEdge(map, theirs as Edge, order);
        }
        order += 1;
        changed = true;
      } else {
        // Both sides added an element with this id; fields where they differ are conflicts, with no common ancestor.
        const differing = fields.filter((field) => !jsonEqual(readField(mine, field), record[field]));
        if (differing.length > 0) {
          conflicts.push({ kind, id: theirs.id, reason: 'both-changed', fields: differing });
        }
      }
      continue;
    }
    if (mine === undefined) {
      // Removed locally. That stands, unless the other writer edited what the user removed.
      if (fields.some((field) => !jsonEqual(record[field], ancestor[field]))) {
        conflicts.push({ kind, id: theirs.id, reason: 'deleted-locally', fields: [] });
      }
      continue;
    }
    const conflicted: string[] = [];
    for (const field of fields) {
      const outcome = mergeField(mine, field, ancestor[field], record[field]);
      changed ||= outcome === 'applied';
      if (outcome === 'conflict') {
        conflicted.push(field);
      }
    }
    if (conflicted.length > 0) {
      conflicts.push({ kind, id: theirs.id, reason: 'both-changed', fields: conflicted });
    }
  }
  return { changed, conflicts };
}

/**
 * Removes what the other writer removed, unless the local user edited it or a local element the remote does not have
 * in that form still refers to it (an edge to it, a child in it): removing it then would leave the user's own edit
 * pointing at nothing, so it stays and is reported.
 */
function removeRemotelyDeleted(nodes: Y.Map<ElementMap>, edges: Y.Map<ElementMap>, base: Document, remote: Document, conflicts: MergeConflict[]): boolean {
  const remoteNodes = new Map(remote.nodes.map((node) => [node.id, node]));
  const remoteEdges = new Map(remote.edges.map((edge) => [edge.id, edge]));
  const kept = (kind: ElementKind, id: Id): void => {
    conflicts.push({ kind, id, reason: 'deleted-remotely', fields: [] });
  };
  let changed = false;

  const edgeRemovals: Id[] = [];
  for (const edge of base.edges) {
    const mine = edges.get(edge.id);
    if (remoteEdges.has(edge.id) || mine === undefined) {
      continue;
    }
    if (elementEquals(mine, edge, EDGE_FIELDS)) {
      edgeRemovals.push(edge.id);
    } else {
      kept('edge', edge.id);
    }
  }
  for (const id of edgeRemovals) {
    edges.delete(id);
    changed = true;
  }

  const nodeRemovals = new Set<Id>();
  for (const node of base.nodes) {
    const mine = nodes.get(node.id);
    if (remoteNodes.has(node.id) || mine === undefined) {
      continue;
    }
    if (elementEquals(mine, node, NODE_FIELDS)) {
      nodeRemovals.add(node.id);
    } else {
      kept('node', node.id);
    }
  }

  // A node stays while a surviving element the remote does not have in its local form refers to it. Keeping one can
  // keep its own parent, so repeat until nothing changes.
  const referenced = (): Id | undefined => {
    for (const id of nodeRemovals) {
      for (const [edgeId, edge] of edges) {
        const theirs = remoteEdges.get(edgeId);
        if ((theirs === undefined || !elementEquals(edge, theirs, EDGE_FIELDS)) && refersTo(edge, id)) {
          return id;
        }
      }
      for (const [nodeId, node] of nodes) {
        const theirs = remoteNodes.get(nodeId);
        if (!nodeRemovals.has(nodeId) && (theirs === undefined || !elementEquals(node, theirs, NODE_FIELDS)) && node.get('parent') === id) {
          return id;
        }
      }
    }
    return undefined;
  };
  for (let id = referenced(); id !== undefined; id = referenced()) {
    nodeRemovals.delete(id);
    kept('node', id);
  }
  for (const id of nodeRemovals) {
    nodes.delete(id);
    changed = true;
  }
  return changed;
}

function refersTo(edge: ElementMap, node: Id): boolean {
  const from = edge.get('from') as { node?: unknown } | undefined;
  const to = edge.get('to') as { node?: unknown } | undefined;
  return from?.node === node || to?.node === node;
}

/**
 * Render order as a sequence rather than a field: the `order` numbers themselves drift (a local add takes one past the
 * highest, a save does not renumber), so what is compared is the relative order of the elements every side still has.
 * When the remote reordered them and the user did not, the remote sequence is written; when both did, the local one
 * stays and the caller reports a conflict.
 */
function mergeOrder(map: Y.Map<ElementMap>, baseElements: readonly { id: Id }[], remoteElements: readonly { id: Id }[]): { changed: boolean; conflict: boolean } {
  const baseIds = new Set(baseElements.map((element) => element.id));
  const remoteIds = new Set(remoteElements.map((element) => element.id));
  const common = (id: Id): boolean => baseIds.has(id) && remoteIds.has(id) && map.has(id);
  const baseSequence = baseElements.map((element) => element.id).filter(common);
  const remoteSequence = remoteElements.map((element) => element.id).filter(common);
  const remoteReordered = !sameSequence(baseSequence, remoteSequence);
  const localSequence = localOrder(map).filter(common);
  const localReordered = !sameSequence(localSequence, baseSequence);
  if (remoteReordered) {
    return localReordered ? { changed: false, conflict: !sameSequence(localSequence, remoteSequence) } : { changed: writeOrder(map, remoteElements), conflict: false };
  }
  // The other writer only inserted: an element it put between two we share belongs where it put it, not after everything
  // local (mergeElements appends it). Where the order is the meaning (a sequence diagram's time, `patch_document`'s
  // addEdge before/after) that is the difference between the message in the right place and in the wrong one. The
  // user's own reorder, if any, stands; an insert at the tail stays after everything local, as it always did.
  return { changed: localReordered ? false : placeInserted(map, baseIds, remoteElements), conflict: false };
}

/**
 * Moves each element the other writer inserted (in the map, not in the base) to just before the next element after it in
 * the remote's order that the base also had, when there is one; an insert with none after it (the tail) is left where
 * it was appended. Consecutive inserts keep their remote order. Renumbers `order`; true when anything changed.
 */
function placeInserted(map: Y.Map<ElementMap>, baseIds: ReadonlySet<Id>, remoteElements: readonly { id: Id }[]): boolean {
  const sequence = localOrder(map);
  let moved = false;
  for (const [index, element] of remoteElements.entries()) {
    if (baseIds.has(element.id) || !map.has(element.id)) {
      continue;
    }
    const successor = remoteElements.slice(index + 1).find((later) => baseIds.has(later.id) && map.has(later.id));
    if (successor === undefined) {
      continue;
    }
    sequence.splice(sequence.indexOf(element.id), 1);
    sequence.splice(sequence.indexOf(successor.id), 0, element.id);
    moved = true;
  }
  return moved && renumber(map, sequence);
}

/** Renumbers `order`: the remote's elements in the remote's order, then any element only the Y.Doc has, in local order. */
function writeOrder(map: Y.Map<ElementMap>, remoteElements: readonly { id: Id }[]): boolean {
  const sequence = remoteElements.map((element) => element.id).filter((id) => map.has(id));
  const placed = new Set(sequence);
  sequence.push(...localOrder(map).filter((id) => !placed.has(id)));
  return renumber(map, sequence);
}

/** Numbers each element's `order` by its place in `sequence`, writing only the ones that differ; true when any did. */
function renumber(map: Y.Map<ElementMap>, sequence: readonly Id[]): boolean {
  let changed = false;
  sequence.forEach((id, index) => {
    const element = map.get(id);
    if (element !== undefined && element.get(ORDER_FIELD) !== index) {
      element.set(ORDER_FIELD, index);
      changed = true;
    }
  });
  return changed;
}

function localOrder(map: Y.Map<ElementMap>): Id[] {
  const entries: [Id, number][] = [];
  map.forEach((element, id) => {
    const order = element.get(ORDER_FIELD);
    entries.push([id, typeof order === 'number' ? order : Number.POSITIVE_INFINITY]);
  });
  entries.sort((a, b) => (a[1] === b[1] ? (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0) : a[1] - b[1]));
  return entries.map(([id]) => id);
}

function sameSequence(a: readonly Id[], b: readonly Id[]): boolean {
  return a.length === b.length && a.every((id, index) => id === b[index]);
}

function elementEquals(map: ElementMap, element: Node | Edge, fields: readonly string[]): boolean {
  const record = element as unknown as Record<string, unknown>;
  return fields.every((field) => jsonEqual(readField(map, field), record[field]));
}

/** A field as the JSON document would carry it: null and absent are both absent. */
function readField(map: Y.Map<unknown>, field: string): unknown {
  const value = map.get(field);
  return value === null ? undefined : value;
}

/** Writes a copy of the value, or removes the field for undefined. Returns whether the stored value changed. */
function writeField(map: Y.Map<unknown>, field: string, value: unknown): boolean {
  if (jsonEqual(readField(map, field), value)) {
    return false;
  }
  if (value === undefined) {
    map.delete(field);
  } else {
    map.set(field, JSON.parse(JSON.stringify(value)) as unknown);
  }
  return true;
}

/** Structural equality of JSON values, ignoring member order and treating an undefined member as absent. */
export function jsonEqual(a: unknown, b: unknown): boolean {
  if (a === b) {
    return true;
  }
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) {
    return false;
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((item, index) => jsonEqual(item, b[index]));
  }
  const left = a as Record<string, unknown>;
  const right = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  for (const key of keys) {
    if (!jsonEqual(left[key], right[key])) {
      return false;
    }
  }
  return true;
}
