/**
 * The Yjs shape of a Draughtsman document (spec section 4: "a Y.Map per node/edge").
 *
 *   doc.getMap('header')   id, schemaVersion, title, type, theme?, flythrough?   plain values
 *   doc.getMap('nodes')    node id -> Y.Map of that node's JSON fields, plus `order`
 *   doc.getMap('edges')    edge id -> Y.Map of that edge's JSON fields, plus `order`
 *   doc.getMap('meta')     createdAt, updatedAt, createdBy, aiProvenance?
 *
 * Inside an element map every value is plain JSON: geometry, style, props, ports and routing are objects and
 * arrays, not nested Y types, so an edit replaces the whole field. That is the right granularity for the
 * single-editor MVP, keeps the materialised JSON identical to what the SPA holds, and can be refined per field
 * later under a schemaVersion bump. `order` is the element's index in the JSON array (also its render z-order);
 * it lives only in the Yjs form and is dropped from the JSON.
 *
 * The SPA must build its Y.Doc to exactly this shape (same map names, same field names) or the server-side
 * snapshot will not match what the browser shows. web/ pins the same yjs version for the same reason.
 */
import * as Y from 'yjs';
import type {
  AiProvenance,
  Document,
  DocumentType,
  Edge,
  EdgeStyle,
  Endpoint,
  Flythrough,
  Geometry,
  Node,
  NodeStyle,
  Port,
  Props,
  Routing,
} from '../model/types.ts';
import { ErrorCodes, RpcError, isObject } from '../rpc.ts';

export const HEADER_MAP = 'header';
export const NODES_MAP = 'nodes';
export const EDGES_MAP = 'edges';
export const META_MAP = 'meta';
export const ORDER_FIELD = 'order';

type ElementMap = Y.Map<unknown>;

/** Writes a JSON document into a Y.Doc in one transaction. The Y.Doc is expected to be empty. */
export function documentToYDoc(document: Document, doc: Y.Doc = new Y.Doc()): Y.Doc {
  doc.transact(() => {
    const header = doc.getMap<unknown>(HEADER_MAP);
    header.set('id', document.id);
    header.set('schemaVersion', document.schemaVersion);
    header.set('title', document.title);
    header.set('type', document.type);
    setOptional(header, 'theme', document.theme);
    setOptional(header, 'flythrough', document.flythrough);

    const nodes = doc.getMap<ElementMap>(NODES_MAP);
    document.nodes.forEach((node, index) => {
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
      map.set(ORDER_FIELD, index);
    });

    const edges = doc.getMap<ElementMap>(EDGES_MAP);
    document.edges.forEach((edge, index) => {
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
      map.set(ORDER_FIELD, index);
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
 * Materialises a Y.Doc as the JSON document, with members in the canonical order the C# serialiser and the
 * schema use, so the same document serialises to the same bytes on either side of the boundary.
 * Throws an invalidDocument RpcError when the header, the meta or any element is incomplete: this is where
 * CRDT state becomes a document that is returned and stored, so a partial element must not pass as a whole one.
 */
export function yDocToDocument(doc: Y.Doc): Document {
  const header = doc.getMap<unknown>(HEADER_MAP);
  const meta = doc.getMap<unknown>(META_MAP);

  const theme = header.get('theme');
  const flythrough = header.get('flythrough');
  const document: Document = {
    id: requireString(header, 'id', 'header'),
    schemaVersion: requireSchemaVersion(header),
    title: requireString(header, 'title', 'header'),
    type: requireString(header, 'type', 'header') as DocumentType,
    ...(theme === undefined || theme === null ? {} : { theme: clone(theme) as Record<string, string> }),
    nodes: [],
    edges: [],
    meta: {
      createdAt: requireString(meta, 'createdAt', 'meta'),
      updatedAt: requireString(meta, 'updatedAt', 'meta'),
      createdBy: requireString(meta, 'createdBy', 'meta'),
    },
    // Last, where the C# serialiser and the schema put it, so the bytes agree on either side of the boundary.
    ...(flythrough === undefined || flythrough === null ? {} : { flythrough: clone(flythrough) as Flythrough }),
  };

  const provenance = meta.get('aiProvenance');
  if (provenance !== undefined && provenance !== null) {
    document.meta.aiProvenance = clone(provenance) as AiProvenance[];
  }

  document.nodes = orderedElements(doc.getMap<ElementMap>(NODES_MAP), NODES_MAP).map(([id, map]) => nodeFromMap(id, map));
  document.edges = orderedElements(doc.getMap<ElementMap>(EDGES_MAP), EDGES_MAP).map(([id, map]) => edgeFromMap(id, map));
  return document;
}

function nodeFromMap(key: string, map: ElementMap): Node {
  const where = `nodes.${key}`;
  const node: Node = {
    id: requireKeyId(map, key, where),
    kind: requireString(map, 'kind', where),
    label: requireString(map, 'label', where),
    shape: requireString(map, 'shape', where),
  };
  // A node that has not been laid out has no geometry; leave it absent rather than inventing a box, which
  // would be indistinguishable from a real one once stored.
  assignIfPresent(node, 'geometry', map.get('geometry') as Geometry | undefined);
  assignIfPresent(node, 'style', map.get('style') as NodeStyle | undefined);
  assignIfPresent(node, 'props', map.get('props') as Props | undefined);
  assignIfPresent(node, 'parent', map.get('parent') as string | undefined);
  assignIfPresent(node, 'ports', map.get('ports') as Port[] | undefined);
  return node;
}

function edgeFromMap(key: string, map: ElementMap): Edge {
  const where = `edges.${key}`;
  // Canonical member order is id, kind, label, from, to, routing, style, props; label is inserted here.
  const id = requireKeyId(map, key, where);
  const kind = requireString(map, 'kind', where);
  const label = optionalString(map, 'label');
  const from = requireEndpoint(map, 'from', where);
  const to = requireEndpoint(map, 'to', where);
  const edge: Edge = label === undefined ? { id, kind, from, to } : { id, kind, label, from, to };
  assignIfPresent(edge, 'routing', map.get('routing') as Routing | undefined);
  assignIfPresent(edge, 'style', map.get('style') as EdgeStyle | undefined);
  assignIfPresent(edge, 'props', map.get('props') as Props | undefined);
  return edge;
}

/** Elements sorted by their `order` field, then by id so elements without one still come out deterministically. */
function orderedElements(map: Y.Map<ElementMap>, where: string): Array<[string, ElementMap]> {
  const entries: Array<[string, ElementMap, number]> = [];
  map.forEach((value, key) => {
    if (!(value instanceof Y.Map)) {
      throw new RpcError(ErrorCodes.invalidDocument, `Yjs state holds ${where}.${key} as a plain value, not a Y.Map; not a Draughtsman document`);
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
  if (value !== undefined && value !== null) {
    target[key] = clone(value) as T[K];
  }
}

function requireString(map: Y.Map<unknown>, key: string, where: string): string {
  const value = map.get(key);
  if (typeof value !== 'string') {
    throw new RpcError(ErrorCodes.invalidDocument, `Yjs state has no string ${where}.${key}; not a Draughtsman document`);
  }
  return value;
}

/** The map key is the element's id; an in-map `id` is written to match it and may not say anything else. */
function requireKeyId(map: ElementMap, key: string, where: string): string {
  const declared = optionalString(map, 'id');
  if (declared !== undefined && declared !== key) {
    throw new RpcError(ErrorCodes.invalidDocument, `Yjs state has ${where} carrying id ${declared}; the map key is the id`);
  }
  return key;
}

function requireEndpoint(map: ElementMap, key: string, where: string): Endpoint {
  const value = map.get(key);
  if (!isObject(value) || typeof value['node'] !== 'string') {
    throw new RpcError(ErrorCodes.invalidDocument, `Yjs state has no string ${where}.${key}.node; not a Draughtsman document`);
  }
  const endpoint: Endpoint = { node: value['node'] };
  if (typeof value['port'] === 'string') {
    endpoint.port = value['port'];
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
    throw new RpcError(ErrorCodes.invalidDocument, `Unsupported schemaVersion ${String(value)} in Yjs state`);
  }
  return value;
}

/** Values read back from a Y.Map are the stored objects; hand out copies so callers cannot mutate the state. */
function clone<T>(value: T): T {
  return value === undefined ? value : (JSON.parse(JSON.stringify(value)) as T);
}

/**
 * The cheap checks the sidecar's own algorithms need: ids to key elements by, endpoints to join, and the schema
 * version this build understands. The full model is enforced by the .NET side before anything is sent.
 */
export function assertDocumentShape(value: unknown, what: string): asserts value is Document {
  if (!isObject(value)) {
    throw new RpcError(ErrorCodes.invalidParams, `${what} must be a document object`);
  }
  if (typeof value['id'] !== 'string') {
    throw new RpcError(ErrorCodes.invalidDocument, `${what}.id must be a string`);
  }
  if (value['schemaVersion'] !== 1) {
    throw new RpcError(ErrorCodes.invalidDocument, `${what}.schemaVersion must be 1`);
  }
  if (!Array.isArray(value['nodes']) || !Array.isArray(value['edges'])) {
    throw new RpcError(ErrorCodes.invalidDocument, `${what}.nodes and ${what}.edges must be arrays`);
  }
  for (const [index, element] of (value['nodes'] as unknown[]).entries()) {
    if (!isObject(element) || typeof element['id'] !== 'string') {
      throw new RpcError(ErrorCodes.invalidDocument, `${what}.nodes[${index}] must have a string id`);
    }
  }
  for (const [index, element] of (value['edges'] as unknown[]).entries()) {
    if (!isObject(element) || typeof element['id'] !== 'string') {
      throw new RpcError(ErrorCodes.invalidDocument, `${what}.edges[${index}] must have a string id`);
    }
    for (const end of ['from', 'to'] as const) {
      const endpoint = element[end];
      if (!isObject(endpoint) || typeof endpoint['node'] !== 'string') {
        throw new RpcError(ErrorCodes.invalidDocument, `${what}.edges[${index}].${end}.node must be a string`);
      }
    }
  }
}
