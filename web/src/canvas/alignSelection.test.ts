import type { Document, Node } from '../model/types.ts';
import { alignSelection, distributeSelection } from './alignSelection.ts';
import { indexDocument } from './documentIndex.ts';

function doc(nodes: Node[]): Document {
  return {
    id: 'doc_align',
    schemaVersion: 1,
    title: 'Align',
    type: 'architecture',
    nodes,
    edges: [{ id: 'e_ab', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
  };
}

const node = (id: string, x: number, y: number, w: number, h: number, parent?: string): Node => ({
  id,
  kind: 'service',
  label: id,
  shape: 'rectangle',
  geometry: { x, y, w, h },
  ...(parent === undefined ? {} : { parent }),
});

// A container holding a child that holds a grandchild, plus two free nodes and one still waiting for layout.
const nested = doc([
  node('n_a', 10, 0, 100, 40),
  node('n_b', 50, 100, 60, 40),
  node('c_box', 200, 200, 300, 200),
  node('n_child', 220, 220, 100, 60, 'c_box'),
  node('n_grand', 230, 230, 10, 10, 'n_child'),
  node('n_other', 700, 700, 50, 50),
]);

describe('alignSelection', () => {
  beforeEach(() => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('moves a container and everything inside it, and leaves the outermost and unselected nodes alone', () => {
    const moves = alignSelection(indexDocument(nested), new Set(['n_a', 'n_b', 'c_box', 'e_ab', 'n_missing']), 'left');
    expect(moves).toEqual(
      new Map([
        ['n_b', { x: 10, y: 100, w: 60, h: 40 }],
        ['c_box', { x: 10, y: 200, w: 300, h: 200 }],
        // Carried by the container's move of -190, keeping their place inside it.
        ['n_child', { x: 30, y: 220, w: 100, h: 60 }],
        ['n_grand', { x: 40, y: 230, w: 10, h: 10 }],
      ]),
    );
    // n_a is already leftmost; n_other is not selected.
    expect(moves.has('n_a')).toBe(false);
    expect(moves.has('n_other')).toBe(false);
  });

  it('aligns a selected child to its own target rather than carrying it with its selected container', () => {
    const moves = alignSelection(indexDocument(nested), new Set(['n_a', 'c_box', 'n_child']), 'left');
    expect(moves.get('c_box')?.x).toBe(10);
    expect(moves.get('n_child')?.x).toBe(10);
    // The grandchild follows its own parent's move (-210), not the container's.
    expect(moves.get('n_grand')?.x).toBe(20);
  });

  it('makes the chosen centres coincide exactly on a half unit', () => {
    const moves = alignSelection(indexDocument(nested), new Set(['n_a', 'n_b', 'n_other']), 'middle');
    // Bounding box y 0..750, centre 375: each node's middle lands on it.
    expect(moves.get('n_a')).toEqual({ x: 10, y: 355, w: 100, h: 40 });
    expect(moves.get('n_b')).toEqual({ x: 50, y: 355, w: 60, h: 40 });
    expect(moves.get('n_other')).toEqual({ x: 700, y: 350, w: 50, h: 50 });
  });

  it('aligns a node waiting for layout from its provisional spot, giving it real geometry', () => {
    const document = doc([node('n_a', 10, 0, 100, 40), { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle' }]);
    const moves = alignSelection(indexDocument(document), new Set(['n_a', 'n_b']), 'top');
    // Provisional: below the placed content at the placed nodes' left, 160 by 48 (documentIndex.ts).
    expect(moves).toEqual(new Map([['n_b', { x: 10, y: 0, w: 160, h: 48 }]]));
  });

  it('does nothing for a single node or a selection of edges only', () => {
    expect(alignSelection(indexDocument(nested), new Set(['n_b']), 'left').size).toBe(0);
    expect(alignSelection(indexDocument(nested), new Set(['n_b', 'e_ab']), 'left').size).toBe(0);
  });
});

describe('distributeSelection', () => {
  it('makes every gap equal, fractional where the space does not divide, keeping the outer two in place', () => {
    const document = doc([node('n_a', 0, 0, 10, 10), node('n_b', 13, 50, 10, 10), node('n_c', 31, 0, 10, 10)]);
    const moves = distributeSelection(indexDocument(document), new Set(['n_a', 'n_b', 'n_c']), 'horizontal');
    // Span 41, sizes 30: two gaps of 5.5, so n_b's left is 10 + 5.5. Its y is untouched.
    expect(moves).toEqual(new Map([['n_b', { x: 15.5, y: 50, w: 10, h: 10 }]]));
  });

  it('carries a distributed container\'s contents with it', () => {
    const document = doc([node('n_a', 0, 0, 10, 10), node('c_box', 0, 100, 100, 100), node('n_in', 10, 110, 20, 20, 'c_box'), node('n_c', 0, 400, 10, 10)]);
    const moves = distributeSelection(indexDocument(document), new Set(['n_a', 'c_box', 'n_c']), 'vertical');
    // Span 410, sizes 120: gaps of 145, so the container's top goes to 155 (+55) and its child with it.
    expect(moves.get('c_box')?.y).toBe(155);
    expect(moves.get('n_in')).toEqual({ x: 10, y: 165, w: 20, h: 20 });
    expect(moves.has('n_a')).toBe(false);
    expect(moves.has('n_c')).toBe(false);
  });

  it('does nothing for two nodes', () => {
    expect(distributeSelection(indexDocument(nested), new Set(['n_a', 'n_b']), 'horizontal').size).toBe(0);
  });
});
