import type { Connection } from '../model/connect.ts';
import type { Document, Geometry, Id, Node } from '../model/types.ts';
import { connectionsForSelection } from './connectSelection.ts';

const meta = { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' };

function node(id: string, geometry: Geometry): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry };
}

// a above b, c to the right of b, d far to the left of everything.
const document: Document = {
  id: 'doc_sel',
  schemaVersion: 1,
  title: 'Selection',
  type: 'architecture',
  nodes: [
    node('a', { x: 0, y: 0, w: 100, h: 50 }),
    node('b', { x: 0, y: 200, w: 100, h: 50 }),
    node('c', { x: 300, y: 200, w: 100, h: 50 }),
    node('d', { x: -400, y: 200, w: 100, h: 50 }),
  ],
  edges: [{ id: 'e_ab', kind: 'flow', from: { node: 'a' }, to: { node: 'b' } }],
  meta,
};

const ends = (connections: Connection[]) => connections.map((c) => [c.from.node, c.from.port.id, c.to.node, c.to.port.id]);
const set = (...ids: Id[]) => new Set(ids);

describe('connectionsForSelection (S5)', () => {
  it('joins the shapes in the order they were selected, not document order, on the sides that face each other', () => {
    expect(ends(connectionsForSelection(document, set('c', 'b', 'd')))).toEqual([
      ['c', 'left', 'b', 'right'],
      ['b', 'left', 'd', 'right'],
    ]);
    // Selected the other way round, the chain and its directions reverse.
    expect(ends(connectionsForSelection(document, set('d', 'b', 'c')))).toEqual([
      ['d', 'right', 'b', 'left'],
      ['b', 'right', 'c', 'left'],
    ]);
  });

  it('leaves a pair that is already connected in that direction, and connects the same pair the other way round', () => {
    expect(connectionsForSelection(document, set('a', 'b'))).toEqual([]);
    expect(ends(connectionsForSelection(document, set('b', 'a')))).toEqual([['b', 'top', 'a', 'bottom']]);
  });

  it('ignores edges and unknown ids, and needs two shapes', () => {
    expect(connectionsForSelection(document, set('b'))).toEqual([]);
    expect(connectionsForSelection(document, set('b', 'e_ab', 'nothing'))).toEqual([]);
    expect(ends(connectionsForSelection(document, set('b', 'e_ab', 'c')))).toEqual([['b', 'right', 'c', 'left']]);
  });
});
