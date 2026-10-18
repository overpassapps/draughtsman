/**
 * The local editing structure: a Y.Doc held in the tab, built to the exact shape the sidecar materialises
 * (sidecar/src/yjs/shape.ts), with the JSON snapshot still the wire format. A document is loaded from JSON into a
 * Y.Doc, every edit mutates the Y.Doc, and the JSON sent to the server is derived back from it, so the two never
 * disagree. This file is the SPA half of the Yjs-shape contract: the map names, field names and the value
 * granularity must match shape.ts and web/ must pin the same `yjs` version as sidecar/package.json, or a server-side
 * snapshot stops matching what the browser shows.
 *
 * Undo is scoped to the local user through `Y.UndoManager` with an origin only the local edits carry, so an edit
 * applied under another origin (an agent's `patch_document`, another client) is invisible to Cmd/Ctrl+Z even when it
 * is the most recent change. Every local mutation runs in a transaction tagged with LOCAL_ORIGIN. Undo steps are
 * bounded by gestures, not by time: the manager never closes a step on its own (captureTimeout is infinite), and the
 * caller closes one with `endUndoStep` when a gesture ends. A drag of many frame-writes is therefore one undo entry
 * however slowly it moves, and two drags are two entries however quickly they follow each other.
 */

import * as Y from 'yjs';
import { columnPortId, columnPortName } from './columnPorts.ts';
import { canonicalFlythrough } from './flythrough.ts';
import type { GroupPlan, UngroupPlan } from './grouping.ts';
import { typeCapabilities } from './typeCapabilities.ts';
import type { AiProvenance, ArrowHead, Document, DocumentType, Edge, EdgeStyle, Endpoint, Flythrough, Geometry, Id, Node, NodeStyle, Point, Port, Props, Routing, RoutingMode } from './types.ts';

export const HEADER_MAP = 'header';
export const NODES_MAP = 'nodes';
export const EDGES_MAP = 'edges';
export const META_MAP = 'meta';
export const ORDER_FIELD = 'order';

/** The origin carried by every local user edit, and the only origin the undo manager tracks. */
export const LOCAL_ORIGIN: unique symbol = Symbol('draughtsman.local-edit');

/**
 * The origin carried by a change another writer saved (an agent's `patch_document`, another tab) when it is merged
 * into the open Y.Doc (model/remoteMerge.ts). The undo manager does not track it, so Cmd/Ctrl+Z never undoes it.
 */
export const REMOTE_ORIGIN: unique symbol = Symbol('draughtsman.remote-edit');

type ElementMap = Y.Map<unknown>;

/** The undo manager for one Y.Doc: tracks only transactions tagged with LOCAL_ORIGIN, merging them until a step ends. */
export function createUndoManager(doc: Y.Doc): Y.UndoManager {
  return new Y.UndoManager(doc, { trackedOrigins: new Set([LOCAL_ORIGIN]), captureTimeout: Number.POSITIVE_INFINITY });
}

/** Closes the current undo step, so the next local edit starts a new one. Call it when a gesture ends. */
export function endUndoStep(undo: Y.UndoManager): void {
  undo.stopCapturing();
}

/** Writes a JSON document into a fresh Y.Doc in one transaction. */
export function documentToYDoc(document: Document, doc: Y.Doc = new Y.Doc()): Y.Doc {
  doc.transact(() => {
    const header = doc.getMap<unknown>(HEADER_MAP);
    header.set('id', document.id);
    header.set('schemaVersion', document.schemaVersion);
    header.set('title', document.title);
    header.set('type', document.type);
    setOptional(header, 'theme', document.theme);
    setOptional(header, 'flythrough', canonicalFlythrough(document.flythrough));

    const nodes = doc.getMap<ElementMap>(NODES_MAP);
    document.nodes.forEach((node, index) => {
      writeNode(nodes, node, index);
    });

    const edges = doc.getMap<ElementMap>(EDGES_MAP);
    document.edges.forEach((edge, index) => {
      writeEdge(edges, edge, index);
    });

    const meta = doc.getMap<unknown>(META_MAP);
    meta.set('createdAt', document.meta.createdAt);
    meta.set('updatedAt', document.meta.updatedAt);
    meta.set('createdBy', document.meta.createdBy);
    setOptional(meta, 'aiProvenance', document.meta.aiProvenance);
  });
  return doc;
}

/**
 * Materialises a Y.Doc as the JSON document, with members in the canonical order the C# serialiser and the schema
 * use, so the same document round-trips to the same bytes. Throws when the header, the meta or any element is
 * incomplete: this is where CRDT state becomes the document that is saved, so a partial element must not pass.
 */
export function yDocToDocument(doc: Y.Doc): Document {
  const header = doc.getMap<unknown>(HEADER_MAP);
  const meta = doc.getMap<unknown>(META_MAP);

  const theme = read<Record<string, string>>(header, 'theme');
  const flythrough = canonicalFlythrough(read<Flythrough>(header, 'flythrough'));
  const document: Document = {
    id: requireString(header, 'id', 'header'),
    schemaVersion: requireSchemaVersion(header),
    title: requireString(header, 'title', 'header'),
    type: requireString(header, 'type', 'header') as DocumentType,
    ...(theme === undefined ? {} : { theme }),
    nodes: [],
    edges: [],
    meta: {
      createdAt: requireString(meta, 'createdAt', 'meta'),
      updatedAt: requireString(meta, 'updatedAt', 'meta'),
      createdBy: requireString(meta, 'createdBy', 'meta'),
    },
    // Last, where the schema and the C# serialiser put it, so the same tour serialises to the same bytes.
    ...(flythrough === undefined ? {} : { flythrough }),
  };

  const provenance = read<AiProvenance[]>(meta, 'aiProvenance');
  if (provenance !== undefined) {
    document.meta.aiProvenance = provenance;
  }

  document.nodes = orderedElements(doc.getMap<ElementMap>(NODES_MAP)).map(([id, map]) => nodeFromMap(id, map));
  document.edges = orderedElements(doc.getMap<ElementMap>(EDGES_MAP)).map(([id, map]) => edgeFromMap(id, map));
  return document;
}

/**
 * Sets the document's title. The rename command (editor/useDocumentEditor.ts `renameTitle`) writes it under
 * REMOTE_ORIGIN, not the local origin: the title is saved through its own guarded PATCH rather than the canvas's
 * undo/save cycle, so it must not become an undo step and must not mark the canvas unsaved.
 */
export function setTitle(doc: Y.Doc, title: string, origin: unknown = LOCAL_ORIGIN): void {
  doc.transact(() => {
    doc.getMap<unknown>(HEADER_MAP).set('title', title);
  }, origin);
}

/** The document's flythrough as the Y.Doc holds it now, without materialising the rest of the document. */
export function readFlythrough(doc: Y.Doc): Flythrough | undefined {
  return canonicalFlythrough(read<Flythrough>(doc.getMap<unknown>(HEADER_MAP), 'flythrough'));
}

/**
 * Sets the document's flythrough, the camera path Present plays (model/flythrough.ts), or removes it with undefined. One
 * whole-value write to the header, like the theme: a path is edited a keyframe at a time but saved, merged and undone
 * as a unit. The value is stored in canonical member order and a tour with no keyframes is stored as absent. Answers
 * whether anything changed, so a caller ends no undo step for an edit that did nothing.
 */
export function setFlythrough(doc: Y.Doc, flythrough: Flythrough | undefined, origin: unknown = LOCAL_ORIGIN): boolean {
  const header = doc.getMap<unknown>(HEADER_MAP);
  const next = canonicalFlythrough(flythrough);
  if (JSON.stringify(read<Flythrough>(header, 'flythrough')) === JSON.stringify(next)) {
    return false;
  }
  doc.transact(() => {
    if (next === undefined) {
      header.delete('flythrough');
    } else {
      header.set('flythrough', next);
    }
  }, origin);
  return true;
}

/**
 * Whether the document's positions are derived from element order (a sequence diagram, model/typeCapabilities.ts), so
 * the writers of stored geometry and routing below refuse it: a position written there would never be read.
 */
export function derivesGeometry(doc: Y.Doc): boolean {
  return typeCapabilities(doc.getMap<unknown>(HEADER_MAP).get('type') as DocumentType | undefined).derivedGeometry;
}

/**
 * Applies a set of absolute node geometries in one transaction under the given origin; the local origin by default,
 * which is what puts the write on the local user's undo stack. A node no longer in the document (removed by another
 * writer while a drag was under way) is skipped: there is nothing left to move.
 */
export function setNodeGeometries(doc: Y.Doc, moves: ReadonlyMap<Id, Geometry>, origin: unknown = LOCAL_ORIGIN): void {
  if (moves.size === 0 || derivesGeometry(doc)) {
    return;
  }
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  doc.transact(
    () => {
      for (const [id, geometry] of moves) {
        nodes.get(id)?.set('geometry', { ...geometry });
      }
    },
    origin,
  );
}

/**
 * Adds nodes after every existing node in document order, in one transaction under the given origin. Throws, writing
 * nothing, when an id is already taken: a new node must never overwrite one that exists.
 */
export function addNodes(doc: Y.Doc, added: readonly Node[], origin: unknown = LOCAL_ORIGIN): void {
  if (added.length === 0) {
    return;
  }
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  for (const node of added) {
    if (nodes.has(node.id) || edges.has(node.id)) {
      throw new Error(`Cannot add node ${node.id}: the id is already in the document`);
    }
  }
  let order = nextOrder(nodes);
  doc.transact(
    () => {
      for (const node of added) {
        writeNode(nodes, node, order);
        order += 1;
      }
    },
    origin,
  );
}

/**
 * Adds nodes and edges after every existing element in document order, in one transaction under the given origin: a
 * paste or a duplicate (checklist items 18, 19 and 21) lands as one change. Throws, writing nothing, when an id is
 * already taken or used twice, or when an edge would point at a node that neither the document nor the addition has.
 */
export function addElements(doc: Y.Doc, addedNodes: readonly Node[], addedEdges: readonly Edge[], origin: unknown = LOCAL_ORIGIN): void {
  if (addedNodes.length === 0 && addedEdges.length === 0) {
    return;
  }
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  const added = new Set<Id>();
  for (const { id } of [...addedNodes, ...addedEdges]) {
    if (nodes.has(id) || edges.has(id) || added.has(id)) {
      throw new Error(`Cannot add ${id}: the id is already in the document`);
    }
    added.add(id);
  }
  for (const edge of addedEdges) {
    for (const end of [edge.from.node, edge.to.node]) {
      if (!nodes.has(end) && !addedNodes.some((node) => node.id === end)) {
        throw new Error(`Cannot add edge ${edge.id}: node ${end} is not in the document`);
      }
    }
  }
  let nodeOrder = nextOrder(nodes);
  let edgeOrder = nextOrder(edges);
  doc.transact(
    () => {
      for (const node of addedNodes) {
        writeNode(nodes, node, nodeOrder);
        nodeOrder += 1;
      }
      for (const edge of addedEdges) {
        writeEdge(edges, edge, edgeOrder);
        edgeOrder += 1;
      }
    },
    origin,
  );
}

/** What deleting a selection removes: node ids and edge ids. */
export interface Removal {
  nodes: ReadonlySet<Id>;
  edges: ReadonlySet<Id>;
}

/**
 * The nodes and edges that deleting `selected` removes (checklist item 21). Every selected node goes, and with it
 * every node inside a removed container (transitively, through `parent`), as a copy of a container carries its
 * children (clipboard.ts); then every selected edge and every edge with an endpoint on a removed node, so nothing is
 * left pointing at a node that is not there. Ids that name nothing are ignored.
 */
export function removalFor(
  nodes: Iterable<Pick<Node, 'id' | 'parent'>>,
  edges: Iterable<Pick<Edge, 'id' | 'from' | 'to'>>,
  selected: ReadonlySet<Id>,
): Removal {
  const children = new Map<Id, Id[]>();
  const nodeIds = new Set<Id>();
  for (const node of nodes) {
    nodeIds.add(node.id);
    if (node.parent !== undefined) {
      children.set(node.parent, [...(children.get(node.parent) ?? []), node.id]);
    }
  }
  const removedNodes = new Set<Id>();
  const pending = [...selected].filter((id) => nodeIds.has(id));
  for (let id = pending.pop(); id !== undefined; id = pending.pop()) {
    if (!removedNodes.has(id)) {
      removedNodes.add(id);
      pending.push(...(children.get(id) ?? []));
    }
  }
  const removedEdges = new Set<Id>();
  for (const edge of edges) {
    if (selected.has(edge.id) || removedNodes.has(edge.from.node) || removedNodes.has(edge.to.node)) {
      removedEdges.add(edge.id);
    }
  }
  return { nodes: removedNodes, edges: removedEdges };
}

/**
 * Deletes the selection from the Y.Doc in one transaction under the given origin, as `removalFor` decides, and returns
 * what it removed. Reads the Y.Doc itself rather than a rendered snapshot, so an element another writer added since
 * the last render is still accounted for.
 */
export function removeElements(doc: Y.Doc, selected: ReadonlySet<Id>, origin: unknown = LOCAL_ORIGIN): Removal {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  const nodeRefs: Pick<Node, 'id' | 'parent'>[] = [];
  nodes.forEach((map, id) => {
    const parent = optionalString(map, 'parent');
    nodeRefs.push(parent === undefined ? { id } : { id, parent });
  });
  const edgeRefs: Pick<Edge, 'id' | 'from' | 'to'>[] = [];
  edges.forEach((map, id) => {
    edgeRefs.push({ id, from: requireEndpoint(map, 'from', `edges.${id}`), to: requireEndpoint(map, 'to', `edges.${id}`) });
  });
  const removal = removalFor(nodeRefs, edgeRefs, selected);
  if (removal.nodes.size === 0 && removal.edges.size === 0) {
    return removal;
  }
  doc.transact(
    () => {
      for (const id of removal.edges) {
        edges.delete(id);
      }
      for (const id of removal.nodes) {
        nodes.delete(id);
      }
    },
    origin,
  );
  return removal;
}

/**
 * Writes a node's label in one transaction under the given origin, the local origin by default. Returns whether it
 * wrote: a label equal to the stored one, or a node no longer in the document, writes nothing, so committing an
 * unchanged label leaves the model and the undo stack exactly as they were.
 */
export function setNodeLabel(doc: Y.Doc, id: Id, label: string, origin: unknown = LOCAL_ORIGIN): boolean {
  const node = doc.getMap<ElementMap>(NODES_MAP).get(id);
  if (node === undefined || node.get('label') === label) {
    return false;
  }
  doc.transact(() => {
    node.set('label', label);
  }, origin);
  return true;
}

/**
 * Writes render order (checklist item 25): the `order` field of every node and edge becomes its index in `document`'s
 * arrays, in one transaction under the given origin, the local origin by default. Only fields that change are written,
 * and an id no longer in the Y.Doc is skipped. Returns whether it wrote, so an order that is already in place leaves
 * the model and the undo stack exactly as they were. Pass the whole arrays, as model/arrange.ts returns them: an
 * element left out keeps its old `order`, which could then tie with one written here.
 */
export function setElementOrder(doc: Y.Doc, document: Pick<Document, 'nodes' | 'edges'>, origin: unknown = LOCAL_ORIGIN): boolean {
  const writes: [ElementMap, number][] = [];
  const collect = (map: Y.Map<ElementMap>, elements: readonly { id: Id }[]): void => {
    elements.forEach((element, index) => {
      const target = map.get(element.id);
      if (target !== undefined && target.get(ORDER_FIELD) !== index) {
        writes.push([target, index]);
      }
    });
  };
  collect(doc.getMap<ElementMap>(NODES_MAP), document.nodes);
  collect(doc.getMap<ElementMap>(EDGES_MAP), document.edges);
  if (writes.length === 0) {
    return false;
  }
  doc.transact(() => {
    for (const [target, order] of writes) {
      target.set(ORDER_FIELD, order);
    }
  }, origin);
  return true;
}

/**
 * Writes an edge's label in one transaction under the given origin, the local origin by default (checklist item 11).
 * An empty label removes the field, since `edge.label` is optional and an edge with no label has none. Returns
 * whether it wrote: a label equal to the stored one (an empty one where there is none), or an edge no longer in the
 * document, writes nothing.
 */
export function setEdgeLabel(doc: Y.Doc, id: Id, label: string, origin: unknown = LOCAL_ORIGIN): boolean {
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(id);
  if (edge === undefined || (optionalString(edge, 'label') ?? '') === label) {
    return false;
  }
  doc.transact(() => {
    if (label.length === 0) {
      edge.delete('label');
    } else {
      edge.set('label', label);
    }
  }, origin);
  return true;
}

/**
 * Writes an edge's kind in one transaction under the given origin, the local origin by default (checklist item 12).
 * Only `kind` changes: the edge's arrowheads and stroke follow from the theme's entry for the new kind, and its own
 * sparse `style`, if it has one, stays exactly as it was. Returns whether it wrote; an unchanged kind, or an edge no
 * longer in the document, writes nothing.
 */
export function setEdgeKind(doc: Y.Doc, id: Id, kind: string, origin: unknown = LOCAL_ORIGIN): boolean {
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(id);
  if (edge === undefined || edge.get('kind') === kind) {
    return false;
  }
  doc.transact(() => {
    edge.set('kind', kind);
  }, origin);
  return true;
}

/**
 * Re-points every edge end attached to a renamed column of an entity (`col:<old>` to `col:<new>`, columnPorts.ts) so
 * the connector stays on the row it was drawn to: the port is derived from the name, so renaming the column would
 * otherwise leave the edge attached to a row that no longer exists. `renames` maps old names to new; only ends on
 * `nodeId` change and only their `port`. Returns whether it wrote. Runs in the caller's undo step (local origin).
 */
export function renameColumnPorts(doc: Y.Doc, nodeId: Id, renames: ReadonlyMap<string, string>, origin: unknown = LOCAL_ORIGIN): boolean {
  if (renames.size === 0) {
    return false;
  }
  const changes: { edge: ElementMap; end: 'from' | 'to'; value: Endpoint }[] = [];
  doc.getMap<ElementMap>(EDGES_MAP).forEach((edge) => {
    for (const end of ['from', 'to'] as const) {
      const value = edge.get(end) as Endpoint | undefined;
      const renamed = value?.node === nodeId ? renames.get(columnPortName(value.port) ?? '') : undefined;
      const port = renamed === undefined ? undefined : columnPortId(renamed);
      if (value !== undefined && port !== undefined) {
        changes.push({ edge, end, value: { ...value, port } });
      }
    }
  });
  if (changes.length === 0) {
    return false;
  }
  doc.transact(() => {
    for (const { edge, end, value } of changes) {
      edge.set(end, value);
    }
  }, origin);
  return true;
}

/**
 * Writes an edge's arrowhead ends in one transaction under the given origin, the local origin by default. `undefined`
 * for either end removes that field, so the edge falls back to the diagram theme's entry for its kind; a chosen value
 * is stored as an explicit override, leaving every other style field (stroke, dash, width) untouched. Returns whether
 * it wrote; ends already as requested, or an edge no longer in the document, write nothing.
 */
export function setEdgeEnds(doc: Y.Doc, id: Id, start: ArrowHead | undefined, end: ArrowHead | undefined, origin: unknown = LOCAL_ORIGIN): boolean {
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(id);
  if (edge === undefined) {
    return false;
  }
  const existing = read<EdgeStyle>(edge, 'style') ?? {};
  const withStart: EdgeStyle = start === undefined ? omit(existing, 'arrowStart') : { ...existing, arrowStart: start };
  const result: EdgeStyle = end === undefined ? omit(withStart, 'arrowEnd') : { ...withStart, arrowEnd: end };
  if (stylesEqual(existing, result)) {
    return false;
  }
  doc.transact(() => {
    if (Object.keys(result).length === 0) {
      edge.delete('style');
    } else {
      edge.set('style', result);
    }
  }, origin);
  return true;
}

/**
 * Writes an edge's routing mode in one transaction under the given origin, keeping any waypoints it already pins (the
 * "reset route" action, not a mode change, is what clears them). Returns whether it wrote: an unchanged mode, or an
 * edge no longer in the document, writes nothing.
 */
export function setEdgeRoutingMode(doc: Y.Doc, id: Id, mode: RoutingMode, origin: unknown = LOCAL_ORIGIN): boolean {
  if (derivesGeometry(doc)) {
    return false;
  }
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(id);
  const routing = edge?.get('routing');
  const current = isObject(routing) ? routing.mode : undefined;
  if (edge === undefined || current === mode) {
    return false;
  }
  doc.transact(() => {
    if (isObject(routing)) {
      edge.set('routing', { ...routing, mode });
    } else {
      edge.set('routing', { mode });
    }
  }, origin);
  return true;
}

function omit<T extends object, K extends keyof T>(value: T, key: K): Omit<T, K> {
  const entries = Object.entries(value).filter(([entryKey]) => entryKey !== key);
  return Object.fromEntries(entries) as Omit<T, K>;
}

function stylesEqual(a: EdgeStyle | undefined, b: EdgeStyle): boolean {
  const left = a ?? {};
  const keys = new Set([...Object.keys(left), ...Object.keys(b)]);
  for (const key of keys) {
    if ((left as Record<string, unknown>)[key] !== (b as Record<string, unknown>)[key]) {
      return false;
    }
  }
  return true;
}

/**
 * Replaces each element's sparse `style` in one transaction under the given origin, the local origin by default. A
 * style whose every field is unset (null/undefined) removes the element's `style` field, so it inherits the diagram
 * theme again; the caller composes the new style from the element's current one plus the property being changed, so a
 * single property edit across a multi-selection is one undo step. Elements no longer in the document are skipped.
 */
export function setNodeStyles(doc: Y.Doc, styles: ReadonlyMap<Id, NodeStyle | null>, origin: unknown = LOCAL_ORIGIN): void {
  setStyles(doc, NODES_MAP, styles, (style) => styleIsEmpty(style), origin);
}

/** The edge counterpart of setNodeStyles (see there). */
export function setEdgeStyles(doc: Y.Doc, styles: ReadonlyMap<Id, EdgeStyle | null>, origin: unknown = LOCAL_ORIGIN): void {
  setStyles(doc, EDGES_MAP, styles, (style) => styleIsEmpty(style), origin);
}

/**
 * Replaces (or clears, with null) nodes' free-form `props` in one transaction under the given origin, the local
 * origin by default. Only nodes whose props actually change are written; an empty object is stored as no props at
 * all. Returns whether it wrote, so replacing an image with the same asset leaves the model and undo stack alone.
 */
export function setNodeProps(doc: Y.Doc, props: ReadonlyMap<Id, Props | null>, origin: unknown = LOCAL_ORIGIN): boolean {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const writes: [ElementMap, Props | null][] = [];
  for (const [id, next] of props) {
    const node = nodes.get(id);
    if (node !== undefined && !propsEqual(read<Props>(node, 'props'), next)) {
      writes.push([node, next]);
    }
  }

  if (writes.length === 0) {
    return false;
  }

  doc.transact(() => {
    for (const [node, next] of writes) {
      if (next === null || Object.keys(next).length === 0) {
        node.delete('props');
      } else {
        node.set('props', { ...next });
      }
    }
  }, origin);
  return true;
}

/**
 * Replaces (or clears, with null) edges' free-form `props`, the way `setNodeProps` does for nodes: one transaction under the
 * given origin, only edges whose props change are written, an empty object is stored as no props. A sequence message's
 * `activate`, `deactivate`, `in` and `operand` live here (model/sequenceFragments.ts). Returns whether it wrote.
 */
export function setEdgeProps(doc: Y.Doc, props: ReadonlyMap<Id, Props | null>, origin: unknown = LOCAL_ORIGIN): boolean {
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  const writes: [ElementMap, Props | null][] = [];
  for (const [id, next] of props) {
    const edge = edges.get(id);
    if (edge !== undefined && !propsEqual(read<Props>(edge, 'props'), next)) {
      writes.push([edge, next]);
    }
  }

  if (writes.length === 0) {
    return false;
  }

  doc.transact(() => {
    for (const [edge, next] of writes) {
      if (next === null || Object.keys(next).length === 0) {
        edge.delete('props');
      } else {
        edge.set('props', { ...next });
      }
    }
  }, origin);
  return true;
}

function propsEqual(a: Props | undefined, b: Props | null): boolean {
  if (a === undefined || b === null) {
    return a === undefined && b === null;
  }

  return JSON.stringify(a) === JSON.stringify(b);
}

function setStyles<TStyle>(doc: Y.Doc, mapName: typeof NODES_MAP | typeof EDGES_MAP, styles: ReadonlyMap<Id, TStyle | null>, empty: (style: TStyle) => boolean, origin: unknown): void {
  if (styles.size === 0) {
    return;
  }
  const elements = doc.getMap<ElementMap>(mapName);
  doc.transact(() => {
    for (const [id, style] of styles) {
      const element = elements.get(id);
      if (element === undefined) {
        continue;
      }
      if (style === null || empty(style)) {
        element.delete('style');
      } else {
        element.set('style', { ...style });
      }
    }
  }, origin);
}

function styleIsEmpty(style: object): boolean {
  return Object.values(style).every((value) => value === undefined || value === null || value === false);
}

/**
 * Clears an edge's pinned waypoints in one transaction under the given origin, leaving its routing mode, so an
 * orthogonal connector returns to the router's derived route and a straight or curved one to the line through its
 * endpoint alone (the "reset route" action). Returns whether it wrote: an edge with no waypoints, or none at all,
 * writes nothing.
 */
export function clearEdgeWaypoints(doc: Y.Doc, id: Id, origin: unknown = LOCAL_ORIGIN): boolean {
  if (derivesGeometry(doc)) {
    return false;
  }
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(id);
  const routing = edge?.get('routing');
  if (edge === undefined || !isObject(routing) || !Array.isArray(routing.waypoints)) {
    return false;
  }
  doc.transact(() => {
    const next: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(routing)) {
      if (key !== 'waypoints') {
        next[key] = value;
      }
    }
    edge.set('routing', next);
  }, origin);
  return true;
}

/**
 * Replaces the waypoints of edges in one transaction under the given origin, keeping every other routing field, as a
 * container drag moves the edges inside it (checklist item 26). An edge no longer in the document, or one with no
 * routing to carry waypoints, is skipped.
 */
export function setEdgeWaypoints(doc: Y.Doc, waypoints: ReadonlyMap<Id, readonly Point[]>, origin: unknown = LOCAL_ORIGIN): void {
  if (waypoints.size === 0 || derivesGeometry(doc)) {
    return;
  }
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  doc.transact(
    () => {
      for (const [id, points] of waypoints) {
        const edge = edges.get(id);
        const routing = edge?.get('routing');
        if (edge !== undefined && isObject(routing)) {
          edge.set('routing', { ...routing, waypoints: points.map((point) => ({ x: point.x, y: point.y })) });
        }
      }
    },
    origin,
  );
}

/**
 * Sets or clears (null) node parents in one transaction under the given origin (checklist items 22 and 27). Geometry
 * is absolute, so nothing else changes. Only parents that differ are written, and a node no longer in the document is
 * skipped; returns whether anything was written.
 */
export function setNodeParents(doc: Y.Doc, parents: ReadonlyMap<Id, Id | null>, origin: unknown = LOCAL_ORIGIN): boolean {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const writes = [...parents].filter(([id, parent]) => {
    const node = nodes.get(id);
    return node !== undefined && optionalString(node, 'parent') !== (parent ?? undefined);
  });
  if (writes.length === 0) {
    return false;
  }
  doc.transact(() => {
    for (const [id, parent] of writes) {
      const node = nodes.get(id);
      if (parent === null) {
        node?.delete('parent');
      } else {
        node?.set('parent', parent);
      }
    }
  }, origin);
  return true;
}

/**
 * Writes a group (model/grouping.ts): adds the container, parents the grouped nodes to it, grows the container's parent
 * when the plan says so, and sets render order.
 */
export function applyGroup(doc: Y.Doc, plan: GroupPlan, origin: unknown = LOCAL_ORIGIN): void {
  doc.transact(() => {
    addNodes(doc, [plan.container], origin);
    setNodeParents(doc, new Map(plan.children.map((id) => [id, plan.container.id])), origin);
    setNodeGeometries(doc, plan.resized, origin);
    setElementOrder(doc, { nodes: plan.nodes, edges: [] }, origin);
  }, origin);
}

/** Writes an ungroup (model/grouping.ts): re-parents the children, removes the containers, sets order. */
export function applyUngroup(doc: Y.Doc, plan: UngroupPlan, origin: unknown = LOCAL_ORIGIN): void {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  doc.transact(() => {
    setNodeParents(doc, plan.parents, origin);
    for (const id of plan.containers) {
      nodes.delete(id);
    }
    setElementOrder(doc, { nodes: plan.nodes, edges: [] }, origin);
  }, origin);
}

/** Shifts a node's geometry by a world-space delta, keeping size and rotation. */
export function translateGeometry(geometry: Geometry, dx: number, dy: number): Geometry {
  return { ...geometry, x: geometry.x + dx, y: geometry.y + dy };
}

/** The absolute geometry of every node after shifting its start position by the drag delta. */
export function translateGeometries(starts: ReadonlyMap<Id, Geometry>, dx: number, dy: number): Map<Id, Geometry> {
  const moves = new Map<Id, Geometry>();
  for (const [id, geometry] of starts) {
    moves.set(id, translateGeometry(geometry, dx, dy));
  }
  return moves;
}

export function writeNode(nodes: Y.Map<ElementMap>, node: Node, order: number): void {
  const map = new Y.Map<unknown>();
  nodes.set(node.id, map);
  map.set('id', node.id);
  map.set('kind', node.kind);
  map.set('label', node.label);
  map.set('shape', node.shape);
  setOptional(map, 'geometry', node.geometry);
  setOptional(map, 'style', node.style);
  setOptional(map, 'props', node.props);
  setOptional(map, 'parent', node.parent);
  setOptional(map, 'ports', node.ports);
  map.set(ORDER_FIELD, order);
}

export function writeEdge(edges: Y.Map<ElementMap>, edge: Edge, order: number): void {
  const map = new Y.Map<unknown>();
  edges.set(edge.id, map);
  map.set('id', edge.id);
  map.set('kind', edge.kind);
  setOptional(map, 'label', edge.label);
  map.set('from', edge.from);
  map.set('to', edge.to);
  setOptional(map, 'routing', edge.routing);
  setOptional(map, 'style', edge.style);
  setOptional(map, 'props', edge.props);
  map.set(ORDER_FIELD, order);
}

/** One past the highest `order` among the elements, so an addition sorts after everything already there. */
export function nextOrder(map: Y.Map<ElementMap>): number {
  let highest = -1;
  map.forEach((value) => {
    const order = value.get(ORDER_FIELD);
    if (typeof order === 'number' && order > highest) {
      highest = order;
    }
  });
  return highest + 1;
}

function nodeFromMap(key: string, map: ElementMap): Node {
  const where = `nodes.${key}`;
  const node: Node = {
    id: requireKeyId(map, key, where),
    kind: requireString(map, 'kind', where),
    label: requireString(map, 'label', where),
    shape: requireString(map, 'shape', where),
  };
  assignIfPresent(node, 'geometry', read<Geometry>(map, 'geometry'));
  assignIfPresent(node, 'style', read<NodeStyle>(map, 'style'));
  assignIfPresent(node, 'props', read<Props>(map, 'props'));
  assignIfPresent(node, 'parent', read<string>(map, 'parent'));
  assignIfPresent(node, 'ports', read<Port[]>(map, 'ports'));
  return node;
}

function edgeFromMap(key: string, map: ElementMap): Edge {
  const where = `edges.${key}`;
  const id = requireKeyId(map, key, where);
  const kind = requireString(map, 'kind', where);
  const label = optionalString(map, 'label');
  const from = requireEndpoint(map, 'from', where);
  const to = requireEndpoint(map, 'to', where);
  const edge: Edge = label === undefined ? { id, kind, from, to } : { id, kind, label, from, to };
  assignIfPresent(edge, 'routing', read<Routing>(map, 'routing'));
  assignIfPresent(edge, 'style', read<EdgeStyle>(map, 'style'));
  assignIfPresent(edge, 'props', read<Props>(map, 'props'));
  return edge;
}

/** Elements sorted by their `order` field, then by id so elements without one still come out deterministically. */
function orderedElements(map: Y.Map<ElementMap>): [string, ElementMap][] {
  const entries: [string, ElementMap, number][] = [];
  map.forEach((value, key) => {
    if (!(value instanceof Y.Map)) {
      throw new Error(`Yjs state holds ${key} as a plain value, not a Y.Map; not a Draughtsman document`);
    }
    const order = value.get(ORDER_FIELD);
    entries.push([key, value, typeof order === 'number' ? order : Number.POSITIVE_INFINITY]);
  });
  entries.sort((a, b) => (a[2] === b[2] ? compareOrdinal(a[0], b[0]) : a[2] - b[2]));
  return entries.map(([key, value]) => [key, value]);
}

function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function setOptional(map: Y.Map<unknown>, key: string, value: unknown): void {
  if (value !== undefined) {
    map.set(key, value);
  }
}

function assignIfPresent<T extends object, K extends keyof T>(target: T, key: K, value: T[K] | undefined): void {
  if (value !== undefined) {
    target[key] = value;
  }
}

// eslint-disable-next-line @typescript-eslint/no-unnecessary-type-parameters -- the caller names the field's JSON type
function read<T>(map: Y.Map<unknown>, key: string): T | undefined {
  const value = map.get(key);
  return value === undefined || value === null ? undefined : (clone(value) as T);
}

function requireString(map: Y.Map<unknown>, key: string, where: string): string {
  const value = map.get(key);
  if (typeof value !== 'string') {
    throw new Error(`Yjs state has no string ${where}.${key}; not a Draughtsman document`);
  }
  return value;
}

/** The map key is the element's id; an in-map `id` is written to match it and may not say anything else. */
function requireKeyId(map: ElementMap, key: string, where: string): string {
  const declared = optionalString(map, 'id');
  if (declared !== undefined && declared !== key) {
    throw new Error(`Yjs state has ${where} carrying id ${declared}; the map key is the id`);
  }
  return key;
}

function requireEndpoint(map: ElementMap, key: string, where: string): Endpoint {
  const value = map.get(key);
  if (!isObject(value) || typeof value.node !== 'string') {
    throw new Error(`Yjs state has no string ${where}.${key}.node; not a Draughtsman document`);
  }
  const endpoint: Endpoint = { node: value.node };
  if (typeof value.port === 'string') {
    endpoint.port = value.port;
  }
  return endpoint;
}

function optionalString(map: Y.Map<unknown>, key: string): string | undefined {
  const value = map.get(key);
  return typeof value === 'string' ? value : undefined;
}

function requireSchemaVersion(header: Y.Map<unknown>): Document['schemaVersion'] {
  const value = header.get('schemaVersion');
  if (value !== 1) {
    throw new Error(`Unsupported schemaVersion ${String(value)} in Yjs state`);
  }
  return value;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Values read back from a Y.Map are the stored objects; hand out copies so callers cannot mutate the state. */
function clone<T>(value: T): T {
  return value === undefined ? value : (JSON.parse(JSON.stringify(value)) as T);
}