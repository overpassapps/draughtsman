/**
 * Turns the server's structured diff (`historyClient.ts` `diffRevisions`, the `diff_documents` shape from `DocumentDiff`)
 * into the two things the History panel shows: a readable list, and the marks the canvas draws
 * (design/atlas/version-history.md). Pure: no fetching, no view state. The diff is always "what happened since <version>":
 * `from` is the older revision, `to` the document as it is now, so an *added* element is one the document has and the
 * version did not, and a *removed* element is one only the version had (it is listed, and drawn where it used to be).
 */

import type { DocumentDiffResult, ElementChange } from './historyClient.ts';
import type { Document, Edge, Geometry, Id, Node } from './types.ts';

export type CompareStatus = 'added' | 'removed' | 'changed';

export interface CompareEntry {
  status: CompareStatus;
  kind: 'node' | 'edge';
  id: Id;
  /** What to call it: a node's label, an edge's label or "from → to", else its id. */
  title: string;
  /** Its kind for an added or removed element, the changed field names for a changed one ("label, position or size"). */
  detail: string;
  /** Whether the current document still has it (added and changed do; a removed one does not), so a click can reveal it. */
  present: boolean;
}

/** What the canvas draws over the current document for a comparison. View state only. */
export interface CompareMarks {
  added: ReadonlySet<Id>;
  changed: ReadonlySet<Id>;
  /** Removed nodes at the place they had in the old version; a removed node with no position cannot be drawn and is only listed. */
  removed: readonly { id: Id; geometry: Geometry }[];
}

export interface CompareResult {
  entries: readonly CompareEntry[];
  marks: CompareMarks;
  counts: { added: number; removed: number; changed: number };
}

/** The plain-words name of a changed top-level field (`DocumentDiff`'s own field list). */
const FIELD_WORDS: Readonly<Record<string, string>> = {
  kind: 'kind',
  label: 'label',
  shape: 'shape',
  geometry: 'position or size',
  style: 'style',
  props: 'properties',
  parent: 'container',
  ports: 'ports',
  from: 'start',
  to: 'end',
  routing: 'route',
};

export function describeChange(change: ElementChange): string {
  return change.fields.map((field) => FIELD_WORDS[field.field] ?? field.field).join(', ');
}

function nodeTitle(node: Node): string {
  return node.label.trim() === '' ? node.id : node.label;
}

function edgeTitle(edge: Edge, names: ReadonlyMap<Id, string>): string {
  if (edge.label !== undefined && edge.label.trim() !== '') {
    return edge.label;
  }
  return `${names.get(edge.from.node) ?? edge.from.node} → ${names.get(edge.to.node) ?? edge.to.node}`;
}

/**
 * `current` is the document as stored now (the diff's newer side), used to name changed elements and to tell which still
 * exist; the added and removed elements arrive whole in the diff, so they need no other source.
 */
export function compareResult(diff: DocumentDiffResult, current: Document): CompareResult {
  const names = new Map<Id, string>();
  for (const node of current.nodes) {
    names.set(node.id, nodeTitle(node));
  }
  for (const node of [...diff.addedNodes, ...diff.removedNodes]) {
    if (!names.has(node.id)) {
      names.set(node.id, nodeTitle(node));
    }
  }
  const nodesById = new Map(current.nodes.map((node) => [node.id, node] as const));
  const edgesById = new Map(current.edges.map((edge) => [edge.id, edge] as const));

  const entries: CompareEntry[] = [];
  for (const node of diff.addedNodes) {
    entries.push({ status: 'added', kind: 'node', id: node.id, title: nodeTitle(node), detail: node.kind, present: true });
  }
  for (const node of diff.removedNodes) {
    entries.push({ status: 'removed', kind: 'node', id: node.id, title: nodeTitle(node), detail: node.kind, present: false });
  }
  for (const change of diff.changedNodes) {
    const node = nodesById.get(change.id);
    entries.push({ status: 'changed', kind: 'node', id: change.id, title: node === undefined ? change.id : nodeTitle(node), detail: describeChange(change), present: node !== undefined });
  }
  for (const edge of diff.addedEdges) {
    entries.push({ status: 'added', kind: 'edge', id: edge.id, title: edgeTitle(edge, names), detail: edge.kind, present: true });
  }
  for (const edge of diff.removedEdges) {
    entries.push({ status: 'removed', kind: 'edge', id: edge.id, title: edgeTitle(edge, names), detail: edge.kind, present: false });
  }
  for (const change of diff.changedEdges) {
    const edge = edgesById.get(change.id);
    entries.push({ status: 'changed', kind: 'edge', id: change.id, title: edge === undefined ? change.id : edgeTitle(edge, names), detail: describeChange(change), present: edge !== undefined });
  }

  const added = new Set<Id>([...diff.addedNodes.map((n) => n.id), ...diff.addedEdges.map((e) => e.id)]);
  const changed = new Set<Id>([...diff.changedNodes.map((c) => c.id), ...diff.changedEdges.map((c) => c.id)]);
  const removed = diff.removedNodes.flatMap((node) => (node.geometry === undefined ? [] : [{ id: node.id, geometry: node.geometry }]));
  return {
    entries,
    marks: { added, changed, removed },
    counts: {
      added: diff.addedNodes.length + diff.addedEdges.length,
      removed: diff.removedNodes.length + diff.removedEdges.length,
      changed: diff.changedNodes.length + diff.changedEdges.length,
    },
  };
}

/** "2 added, 1 removed, 3 changed"; "no differences" when there are none. */
export function summarise(counts: CompareResult['counts']): string {
  const parts = [
    counts.added > 0 ? `${String(counts.added)} added` : undefined,
    counts.removed > 0 ? `${String(counts.removed)} removed` : undefined,
    counts.changed > 0 ? `${String(counts.changed)} changed` : undefined,
  ].filter((part): part is string => part !== undefined);
  return parts.length === 0 ? 'no differences' : parts.join(', ');
}
