/** The shared op vocabulary is owned by src/Draughtsman.Mcp/Patching/PatchApplier.cs: `PatchOp` mirrors its op names
 * and shapes exactly. `BrowserOp` (setOrder, setHeader, setMeta) is a browser-side extension for fields that
 * vocabulary does not own. Ops apply to existing element maps rather than replacing the Y.Doc.
 * The complete target is validated before writing; Yjs transactions batch writes but cannot roll them back.
 */
import * as Y from 'yjs';
import { EDGES_MAP, HEADER_MAP, LOCAL_ORIGIN, META_MAP, NODES_MAP, ORDER_FIELD, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';
import type { Document, Edge, Flythrough, Node } from './types.ts';

type Fields = Record<string, unknown>;
type Target = 'node' | 'edge';
export type PatchOp =
  | { op: 'addNode'; node: Node }
  | { op: 'addEdge'; edge: Edge }
  | { op: 'removeNode' | 'removeEdge'; id: string }
  | { op: 'updateNode' | 'updateEdge'; id: string; set: Fields }
  | { op: 'restyle'; target: Target; id: string; style: Fields }
  | { op: 'setProp'; target: Target; id: string; key: string; value: unknown }
  | { op: 'removeProp'; target: Target; id: string; key: string }
  /** Replaces the camera path whole, or removes it with null (docs/flythrough.md). The op vocabulary's name for it; the diff below is computed from two documents and writes a changed path through `setHeader`, so it never produces this one. */
  | { op: 'setFlythrough'; flythrough: Flythrough | null };
export type BrowserOp =
  | { op: 'setOrder'; target: Target; id: string; order: number }
  | { op: 'setHeader' | 'setMeta'; set: Fields };
export type DocumentOp = PatchOp | BrowserOp;

/** JSON structural equality ignores object key order, but retains array order and absent fields. */
export function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
  if (Array.isArray(a) || Array.isArray(b)) {
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => sameValue(v, b[i]));
  }
  const left = a as Fields;
  const right = b as Fields;
  return Object.keys(left).length === Object.keys(right).length && Object.keys(left).every((key) => Object.hasOwn(right, key) && sameValue(left[key], right[key]));
}

function fields(before: object, after: object, excluded: readonly string[] = []): Fields {
  const a = before as Fields;
  const b = after as Fields;
  return Object.fromEntries([...new Set([...Object.keys(a), ...Object.keys(b)])]
    .filter((key) => !excluded.includes(key) && !sameValue(a[key], b[key]))
    .map((key) => [key, b[key] ?? null]));
}

export function diffDocument(before: Document, after: Document): DocumentOp[] {
  parseDocument(after);
  if (before.id !== after.id) {
    throw new Error('Keep the document id and format version unchanged when applying text.');
  }
  const ids = [...after.nodes, ...after.edges].map((element) => element.id);
  if (new Set(ids).size !== ids.length) throw new Error('Every node and edge needs a unique id.');
  const ops: DocumentOp[] = [];
  const header = fields(before, after, ['id', 'schemaVersion', 'nodes', 'edges', 'meta']);
  const meta = fields(before.meta, after.meta);
  if (Object.keys(header).length) ops.push({ op: 'setHeader', set: header });
  if (Object.keys(meta).length) ops.push({ op: 'setMeta', set: meta });
  const elements = (target: Target, old: readonly (Node | Edge)[], next: readonly (Node | Edge)[]): void => {
    const previous = new Map(old.map((element) => [element.id, element]));
    const nextIds = new Set(next.map((element) => element.id));
    for (const element of old) {
      if (!nextIds.has(element.id)) ops.push({ op: target === 'node' ? 'removeNode' : 'removeEdge', id: element.id });
    }
    const reordered = old.length !== next.length || old.some((element, index) => element.id !== next[index]?.id);
    next.forEach((element, index) => {
      const existing = previous.get(element.id);
      if (existing === undefined) {
        ops.push(target === 'node' ? { op: 'addNode', node: element as Node } : { op: 'addEdge', edge: element as Edge });
      } else {
        const set = fields(existing, element, ['id', 'props', 'style']);
        if (Object.keys(set).length) ops.push({ op: target === 'node' ? 'updateNode' : 'updateEdge', id: element.id, set });
        const style = fields(existing.style ?? {}, element.style ?? {});
        if (Object.keys(style).length) ops.push({ op: 'restyle', target, id: element.id, style });
        const a = existing.props ?? {};
        const b = element.props ?? {};
        for (const key of new Set([...Object.keys(a), ...Object.keys(b)])) {
          if (!Object.hasOwn(b, key)) ops.push({ op: 'removeProp', target, id: element.id, key });
          else if (!Object.hasOwn(a, key) || !sameValue(a[key], b[key])) ops.push({ op: 'setProp', target, id: element.id, key, value: b[key] });
        }
      }
      if (reordered) ops.push({ op: 'setOrder', target, id: element.id, order: index });
    });
  };
  elements('node', before.nodes, after.nodes);
  elements('edge', before.edges, after.edges);
  return ops;
}

/** Returns false for a no-op. `base` guards async parsing and drafts against intervening canvas edits. */
export function applyDocumentDiff(doc: Y.Doc, after: Document, base: Document): boolean {
  const before = yDocToDocument(doc);
  if (!sameValue(before, base)) throw new Error('The canvas changed while this text was being edited. Reset text to the current canvas before applying.');
  const ops = diffDocument(before, after);
  if (!ops.length) return false;
  const nodes = doc.getMap<Y.Map<unknown>>(NODES_MAP);
  const edges = doc.getMap<Y.Map<unknown>>(EDGES_MAP);
  const mapFor = (target: Target): Y.Map<Y.Map<unknown>> => target === 'node' ? nodes : edges;
  const requireElement = (map: Y.Map<Y.Map<unknown>>, id: string): Y.Map<unknown> => {
    const element = map.get(id);
    if (element === undefined) throw new Error(`Missing element ${id}`);
    return element;
  };
  const write = (map: Y.Map<unknown>, values: Fields): void => {
    for (const [key, value] of Object.entries(values)) {
      if (value === null) map.delete(key);
      else map.set(key, value);
    }
  };
  doc.transact(() => {
    for (const op of ops) {
      switch (op.op) {
        case 'setHeader': write(doc.getMap(HEADER_MAP), op.set); break;
        case 'setMeta': write(doc.getMap(META_MAP), op.set); break;
        case 'removeNode': nodes.delete(op.id); break;
        case 'removeEdge': edges.delete(op.id); break;
        case 'addNode':
        case 'addEdge': {
          const element = op.op === 'addNode' ? op.node : op.edge;
          const map = new Y.Map<unknown>();
          (op.op === 'addNode' ? nodes : edges).set(element.id, map);
          write(map, { ...element });
          break;
        }
        case 'updateNode': write(requireElement(nodes, op.id), op.set); break;
        case 'updateEdge': write(requireElement(edges, op.id), op.set); break;
        case 'setOrder': requireElement(mapFor(op.target), op.id).set(ORDER_FIELD, op.order); break;
        case 'restyle': {
          const map = requireElement(mapFor(op.target), op.id);
          const style = { ...(map.get('style') as Fields | undefined) };
          for (const [key, value] of Object.entries(op.style)) {
            if (value === null) Reflect.deleteProperty(style, key);
            else style[key] = value;
          }
          if (Object.keys(style).length) map.set('style', style);
          else map.delete('style');
          break;
        }
        case 'setProp':
        case 'removeProp': {
          const map = requireElement(mapFor(op.target), op.id);
          const props = { ...(map.get('props') as Fields | undefined), ...(op.op === 'setProp' ? { [op.key]: op.value } : {}) };
          if (op.op === 'removeProp') Reflect.deleteProperty(props, op.key);
          if (Object.keys(props).length) map.set('props', props);
          else map.delete('props');
          break;
        }
      }
    }
  }, LOCAL_ORIGIN);
  return true;
}
