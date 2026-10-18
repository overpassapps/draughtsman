/**
 * Copy and paste as a model transform. Pure functions: `copy` turns a document and a selection into a self-contained
 * clipboard payload, and `paste` turns a target document and a payload into the nodes and edges to add. There is no
 * keyboard handling, system clipboard access, undo or Yjs writing here; the caller owns those and feeds this module
 * plain model values. Checklist items 18 and 19 are built on it.
 *
 * What a copy contains:
 * - Every selected node, plus every descendant of a selected container (transitively, through `parent`). Copying a
 *   container copies what sits inside it, because a container pasted empty would silently lose the part of the
 *   diagram the user could see they had selected, and moving a container already carries its children.
 * - Every edge whose `from` and `to` nodes are both copied, whether or not the edge itself was selected. A selected
 *   edge with an endpoint left behind is dropped: a pasted edge must never point at a node that is not there.
 * - A copied node whose parent was not copied keeps its `parent`, still naming the container in the source document,
 *   and the payload records that document's id as `source`. It is the one reference allowed to leave the payload.
 * Nodes and edges keep their document order, not selection order, so a container still precedes its children.
 *
 * What a paste returns: a fresh id for every node and edge, unused by the target document, the payload and the rest
 * of the paste, with every internal reference (edge endpoints, `parent`) remapped to the new ids. A `parent` that
 * points outside the payload is kept only when pasting into the document the payload was copied from (its `source`)
 * and that container is still there, so a paste or duplicate of a container's child in the same document lands in the
 * same container, as it does in Lucid and Figma; into any other document, or once the container is gone, the node
 * loses its `parent`. This parent retention, and the `source` field in the payload that supports it, are deliberate:
 * without them a duplicate of a container's child would land outside its container. Port ids are scoped to their
 * node, so an endpoint's `port` keeps its id and still names the same port on the new node. Every position is shifted
 * by the offset (node geometry and edge waypoints alike), so relative layout is preserved exactly; a node without
 * geometry is still waiting for layout and stays without. The caller chooses the offset (zero included); the editor
 * steps each repeated paste one grid step further on.
 *
 * The payload is plain JSON (it goes through the system clipboard), and neither function mutates its inputs, so the
 * same payload pasted twice into a document that has taken the first paste yields two disjoint id sets.
 */

import type { Document, Edge, Id, Node, Point } from './types.ts';

/** Marks JSON on the system clipboard as a Draughtsman selection. */
export const CLIPBOARD_FORMAT = 'draughtsman/selection';

export const CLIPBOARD_VERSION = 1;

export interface ClipboardPayload {
  format: typeof CLIPBOARD_FORMAT;
  version: typeof CLIPBOARD_VERSION;
  /** The id of the document the selection was copied from. */
  source?: Id;
  nodes: Node[];
  edges: Edge[];
}

/** The nodes and edges a paste adds, already carrying their new ids. */
export interface PasteResult {
  nodes: Node[];
  edges: Edge[];
}

export type ElementKind = 'node' | 'edge';

/**
 * Proposes an id for a new element. It need not know the document: `paste` asks again while the proposal collides
 * with an id already taken. Inject a deterministic one in tests.
 */
export type IdGenerator = (kind: ElementKind) => Id;

/** The schema's id pattern (docs/schema/document.schema.json `$defs.id`). */
const ID_PATTERN = /^[A-Za-z_][A-Za-z0-9_.-]*$/;

/** How many colliding proposals `paste` tolerates for one element before deciding the generator is stuck. */
const MAX_ID_ATTEMPTS = 1000;

/** Short random ids in the project's shape: n_ for nodes, e_ for edges (types.ts: "n_api, e_12"). */
export const randomIdGenerator: IdGenerator = (kind) =>
  `${kind === 'node' ? 'n' : 'e'}_${Math.random().toString(36).slice(2, 10)}`;

function clone<T>(value: T): T {
  return structuredClone(value);
}

/** The payload for `selectedIds` in `document`. Ids that name nothing in the document are ignored. */
export function copy(document: Document, selectedIds: Iterable<Id>): ClipboardPayload {
  const selected = new Set(selectedIds);

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

  const copied = new Set<Id>();
  const pending = document.nodes.filter((node) => selected.has(node.id)).map((node) => node.id);
  for (let id = pending.pop(); id !== undefined; id = pending.pop()) {
    if (copied.has(id)) {
      continue;
    }
    copied.add(id);
    pending.push(...(children.get(id) ?? []));
  }

  const nodes = document.nodes.filter((node) => copied.has(node.id)).map((node) => clone(node));
  const edges = document.edges
    .filter((edge) => copied.has(edge.from.node) && copied.has(edge.to.node))
    .map((edge) => clone(edge));

  return { format: CLIPBOARD_FORMAT, version: CLIPBOARD_VERSION, source: document.id, nodes, edges };
}

function shift(point: Point, offset: Point): Point {
  return { x: point.x + offset.x, y: point.y + offset.y };
}

/**
 * The nodes and edges to add to `target` for `payload`, moved by `offset` in canvas units. Throws when the generator
 * proposes an id outside the schema's pattern or cannot find a free one.
 */
export function paste(
  target: Document,
  payload: ClipboardPayload,
  offset: Point,
  generateId: IdGenerator = randomIdGenerator,
): PasteResult {
  const taken = new Set<Id>();
  for (const node of target.nodes) {
    taken.add(node.id);
  }
  const containers = new Set<Id>(payload.source === target.id ? target.nodes.map((node) => node.id) : []);
  for (const edge of target.edges) {
    taken.add(edge.id);
  }
  for (const node of payload.nodes) {
    taken.add(node.id);
  }
  for (const edge of payload.edges) {
    taken.add(edge.id);
  }

  const freshId = (kind: ElementKind): Id => {
    for (let attempt = 0; attempt < MAX_ID_ATTEMPTS; attempt++) {
      const id = generateId(kind);
      if (!ID_PATTERN.test(id)) {
        throw new Error(`Generated ${kind} id "${id}" does not match the schema's id pattern.`);
      }
      if (!taken.has(id)) {
        taken.add(id);
        return id;
      }
    }
    throw new Error(`Could not generate a free ${kind} id in ${String(MAX_ID_ATTEMPTS)} attempts.`);
  };

  const nodeIds = new Map<Id, Id>();
  const renamed = payload.nodes.map((source) => {
    const id = freshId('node');
    nodeIds.set(source.id, id);
    return { source, id };
  });

  const nodes = renamed.map(({ source, id }) => {
    const node = clone(source);
    node.id = id;
    if (node.parent !== undefined) {
      const parent = nodeIds.get(node.parent);
      if (parent !== undefined) {
        node.parent = parent;
      } else if (!containers.has(node.parent)) {
        delete node.parent;
      }
    }
    if (node.geometry) {
      node.geometry.x += offset.x;
      node.geometry.y += offset.y;
    }
    return node;
  });

  const edges: Edge[] = [];
  for (const source of payload.edges) {
    const from = nodeIds.get(source.from.node);
    const to = nodeIds.get(source.to.node);
    if (from === undefined || to === undefined) {
      continue;
    }
    const edge = clone(source);
    edge.id = freshId('edge');
    edge.from.node = from;
    edge.to.node = to;
    if (edge.routing?.waypoints) {
      edge.routing.waypoints = edge.routing.waypoints.map((point) => shift(point, offset));
    }
    edges.push(edge);
  }

  return { nodes, edges };
}
