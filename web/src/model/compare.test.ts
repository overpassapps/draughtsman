import { compareResult, describeChange, summarise } from './compare.ts';
import type { DocumentDiffResult } from './historyClient.ts';
import type { Document, Edge, Node } from './types.ts';

const node = (id: string, label: string, extra: Partial<Node> = {}): Node => ({ id, kind: 'process', label, shape: 'rectangle', ...extra });
const edge = (id: string, from: string, to: string, label?: string): Edge => ({ id, kind: 'flow', from: { node: from }, to: { node: to }, ...(label === undefined ? {} : { label }) });

const current: Document = {
  id: 'doc_c',
  schemaVersion: 1,
  title: 'Now',
  type: 'flowchart',
  nodes: [node('n_keep', 'Keep', { geometry: { x: 0, y: 0, w: 100, h: 40 } }), node('n_new', 'Brand new', { geometry: { x: 200, y: 0, w: 100, h: 40 } }), node('n_renamed', 'Renamed')],
  edges: [edge('e_new', 'n_keep', 'n_new'), edge('e_named', 'n_keep', 'n_renamed', 'Yes')],
  meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' },
};

const diff: DocumentDiffResult = {
  addedNodes: [node('n_new', 'Brand new', { geometry: { x: 200, y: 0, w: 100, h: 40 } })],
  removedNodes: [node('n_gone', 'Gone', { geometry: { x: 50, y: 90, w: 120, h: 50 } }), node('n_gone_unplaced', 'No position')],
  changedNodes: [{ id: 'n_renamed', fields: [{ field: 'label' }, { field: 'geometry' }] }],
  addedEdges: [edge('e_new', 'n_keep', 'n_new')],
  removedEdges: [edge('e_old', 'n_keep', 'n_gone')],
  changedEdges: [{ id: 'e_named', fields: [{ field: 'label' }] }],
};

describe('compareResult', () => {
  const result = compareResult(diff, current);

  it('counts what was added, removed and changed across nodes and connectors', () => {
    expect(result.counts).toEqual({ added: 2, removed: 3, changed: 2 });
    expect(summarise(result.counts)).toBe('2 added, 3 removed, 2 changed');
  });

  it('lists each element under the right status, named the way a person would', () => {
    const titles = (status: string) => result.entries.filter((entry) => entry.status === status).map((entry) => `${entry.kind}:${entry.title}`);
    expect(titles('added')).toEqual(['node:Brand new', 'edge:Keep → Brand new']);
    expect(titles('removed')).toEqual(['node:Gone', 'node:No position', 'edge:Keep → Gone']);
    expect(titles('changed')).toEqual(['node:Renamed', 'edge:Yes']);
  });

  it('says in plain words which fields changed, and whether the element can be shown on the canvas', () => {
    const changed = result.entries.find((entry) => entry.id === 'n_renamed');
    expect(changed?.detail).toBe('label, position or size');
    expect(describeChange({ id: 'x', fields: [{ field: 'parent' }, { field: 'somethingNew' }] })).toBe('container, somethingNew');
    expect(result.entries.filter((entry) => entry.present).map((entry) => entry.id)).toEqual(['n_new', 'n_renamed', 'e_new', 'e_named']);
    expect(result.entries.filter((entry) => !entry.present).every((entry) => entry.status === 'removed')).toBe(true);
  });

  it('marks added and changed elements by id and draws a removed node only where the old version had it positioned', () => {
    expect([...result.marks.added].sort()).toEqual(['e_new', 'n_new']);
    expect([...result.marks.changed].sort()).toEqual(['e_named', 'n_renamed']);
    expect(result.marks.removed).toEqual([{ id: 'n_gone', geometry: { x: 50, y: 90, w: 120, h: 50 } }]);
  });

  it('reports no differences as such, with nothing to draw', () => {
    const none = compareResult({ addedNodes: [], removedNodes: [], changedNodes: [], addedEdges: [], removedEdges: [], changedEdges: [] }, current);
    expect(none.entries).toEqual([]);
    expect(summarise(none.counts)).toBe('no differences');
    expect(none.marks.added.size + none.marks.changed.size + none.marks.removed.length).toBe(0);
  });
});
