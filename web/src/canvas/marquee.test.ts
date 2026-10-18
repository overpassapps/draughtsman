import type { Document, Node } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { marqueeRect, marqueeSelection, nodesInMarquee } from './marquee.ts';

function doc(nodes: Node[]): Document {
  return {
    id: 'doc_marquee',
    schemaVersion: 1,
    title: 'Marquee',
    type: 'flowchart',
    nodes,
    edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
  };
}

// n_a at 0..100 x 0..50, n_b at 200..300 x 0..50, n_c at 0..100 x 200..250.
const index = indexDocument(
  doc([
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
    { id: 'n_c', kind: 'service', label: 'C', shape: 'rectangle', geometry: { x: 0, y: 200, w: 100, h: 50 } },
  ]),
);

describe('marqueeRect', () => {
  it('is the same rectangle whichever corner the drag started from', () => {
    expect(marqueeRect({ x: 10, y: 20 }, { x: 110, y: 70 })).toEqual({ x: 10, y: 20, w: 100, h: 50 });
    expect(marqueeRect({ x: 110, y: 70 }, { x: 10, y: 20 })).toEqual({ x: 10, y: 20, w: 100, h: 50 });
    expect(marqueeRect({ x: 110, y: 20 }, { x: 10, y: 70 })).toEqual({ x: 10, y: 20, w: 100, h: 50 });
  });
});

describe('nodesInMarquee', () => {
  it('catches every node the marquee intersects and none it misses, and never an edge', () => {
    // Covers the right half of n_a and the left half of n_b; n_c is below it.
    expect(nodesInMarquee(index, { x: 50, y: -10, w: 200, h: 30 }, false)).toEqual(new Set(['n_a', 'n_b']));
    expect(nodesInMarquee(index, { x: 120, y: 100, w: 50, h: 50 }, false)).toEqual(new Set());
  });

  it('with Alt catches only nodes it fully encloses', () => {
    // Encloses n_a exactly on its bounds, and only half of n_b.
    expect(nodesInMarquee(index, { x: 0, y: 0, w: 250, h: 50 }, true)).toEqual(new Set(['n_a']));
    // The same marquee without Alt catches both.
    expect(nodesInMarquee(index, { x: 0, y: 0, w: 250, h: 50 }, false)).toEqual(new Set(['n_a', 'n_b']));
  });

  it('tests a rotated node by the bounds it turns through', () => {
    // A 100x10 bar turned 90 degrees about its centre (50, 5) covers x 45..55, y -45..55.
    const rotated = indexDocument(doc([{ id: 'n_r', kind: 'service', label: 'R', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 10, rotation: 90 } }]));
    expect(nodesInMarquee(rotated, { x: 40, y: -50, w: 20, h: 10 }, false)).toEqual(new Set(['n_r']));
    expect(nodesInMarquee(rotated, { x: 70, y: 0, w: 30, h: 10 }, false)).toEqual(new Set());
    expect(nodesInMarquee(rotated, { x: 40, y: -50, w: 20, h: 110 }, true)).toEqual(new Set(['n_r']));
    expect(nodesInMarquee(rotated, { x: 0, y: 0, w: 100, h: 10 }, true)).toEqual(new Set());
  });
});

describe('marqueeSelection', () => {
  it('replaces the selection with the catch, or with Shift adds the catch to it', () => {
    const base = new Set(['n_c']);
    const caught = new Set(['n_a', 'n_b']);
    expect(marqueeSelection(base, caught, false)).toEqual(new Set(['n_a', 'n_b']));
    expect(marqueeSelection(base, caught, true)).toEqual(new Set(['n_a', 'n_b', 'n_c']));
    expect(base).toEqual(new Set(['n_c']));
  });
});
