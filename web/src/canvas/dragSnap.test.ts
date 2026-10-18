import type { Document, Geometry } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { snapMove, snapResize, snapTargets, type SnapContext } from './dragSnap.ts';
import type { Rect } from './geometry.ts';

const A: Geometry = { x: 0, y: 0, w: 100, h: 50 };
/** A neighbour to the right of A, top-aligned with it: left edge 200, centre 250, right edge 300. */
const B: Rect = { x: 200, y: 0, w: 100, h: 50 };

function context(others: readonly Rect[], overrides: Partial<SnapContext> = {}): SnapContext {
  return { others, gridSize: 20, gridEnabled: false, zoom: 1, ...overrides };
}

describe('snapMove', () => {
  it('lands the dragged node on the grid while the grid is on, and leaves it where the pointer put it while off', () => {
    // Unsnapped: x 33..133 (both edges 7 from a line; the leading edge wins the tie), y 7..57 (bottom 3 from 60).
    const on = snapMove(new Map([['n_a', A]]), 33, 7, context([], { gridEnabled: true }));
    expect(on.moves).toEqual(new Map([['n_a', { x: 40, y: 10, w: 100, h: 50 }]]));
    expect(on.guides.map((g) => g.kind)).toEqual(['grid', 'grid']);

    const off = snapMove(new Map([['n_a', A]]), 33, 7, context([]));
    expect(off.moves).toEqual(new Map([['n_a', { x: 33, y: 7, w: 100, h: 50 }]]));
    expect(off.guides).toEqual([]);
  });

  it('snaps to a neighbour edge within 6 px and draws the guide spanning both nodes', () => {
    // Unsnapped right edge 195 is 5 px from B's left edge; y 40..90 is clear of B's edges and centre.
    const snapped = snapMove(new Map([['n_a', A]]), 95, 40, context([B]));
    expect(snapped.moves).toEqual(new Map([['n_a', { x: 100, y: 40, w: 100, h: 50 }]]));
    expect(snapped.guides).toEqual([{ kind: 'edge', from: { x: 200, y: 0 }, to: { x: 200, y: 90 } }]);
  });

  it('scales the 6 px magnet by the zoom: 5 canvas units is out of reach at 200% and 10 is in reach at 50%', () => {
    expect(snapMove(new Map([['n_a', A]]), 95, 40, context([B], { zoom: 2 })).moves.get('n_a')?.x).toBe(95);
    expect(snapMove(new Map([['n_a', A]]), 90, 40, context([B], { zoom: 0.5 })).moves.get('n_a')?.x).toBe(100);
    expect(snapMove(new Map([['n_a', A]]), 90, 40, context([B], { zoom: 1 })).moves.get('n_a')?.x).toBe(90);
  });

  it('snaps to the spacing between two aligned nodes and draws an equal-spacing guide across the matched gap', () => {
    // P and Q are 50 apart; the dragged node would sit 47 right of Q, so it slides back to 50.
    const P: Rect = { x: 0, y: 0, w: 100, h: 50 };
    const Q: Rect = { x: 150, y: 0, w: 100, h: 50 };
    const snapped = snapMove(new Map([['n_m', { x: 400, y: 0, w: 100, h: 50 }]]), -103, 0, context([P, Q]));
    expect(snapped.moves.get('n_m')).toEqual({ x: 300, y: 0, w: 100, h: 50 });
    expect(snapped.guides).toContainEqual({ kind: 'equal-spacing', from: { x: 250, y: 25 }, to: { x: 300, y: 25 } });
  });

  it('snaps a multi-selection by its bounding box and moves every node by the same amount', () => {
    const C: Geometry = { x: 0, y: 100, w: 100, h: 50 };
    const snapped = snapMove(
      new Map([
        ['n_a', A],
        ['n_c', C],
      ]),
      95,
      0,
      context([B]),
    );
    expect(snapped.moves).toEqual(
      new Map([
        ['n_a', { x: 100, y: 0, w: 100, h: 50 }],
        ['n_c', { x: 100, y: 100, w: 100, h: 50 }],
      ]),
    );
  });
});

describe('snapResize', () => {
  it('snaps only the edges under the handle', () => {
    // Bottom-right corner: the right edge (196) reaches B's left edge and the bottom (47) reaches B's bottom.
    const corner = snapResize(A, { x: 1, y: 1 }, { x: 0, y: 0, w: 196, h: 47 }, false, context([B]));
    expect(corner.geometry).toEqual({ x: 0, y: 0, w: 200, h: 50 });
    expect(corner.guides.map((g) => g.kind)).toEqual(['edge', 'edge']);

    // The right-edge handle: the bottom is not moving, so only x snaps even though the bottom is 3 from B's.
    const edge = snapResize(A, { x: 1, y: 0.5 }, { x: 0, y: 0, w: 196, h: 47 }, false, context([B]));
    expect(edge.geometry).toEqual({ x: 0, y: 0, w: 200, h: 47 });
    expect(edge.guides).toEqual([{ kind: 'edge', from: { x: 200, y: 0 }, to: { x: 200, y: 50 } }]);
  });

  it('snaps a resized edge to the grid', () => {
    const snapped = snapResize(A, { x: 1, y: 0.5 }, { x: 0, y: 0, w: 133, h: 50 }, false, context([], { gridEnabled: true }));
    expect(snapped.geometry).toEqual({ x: 0, y: 0, w: 140, h: 50 });
    expect(snapped.guides).toEqual([{ kind: 'grid', from: { x: 140, y: 0 }, to: { x: 140, y: 50 } }]);
  });

  it('leaves a Shift resize and a rotated node unsnapped', () => {
    expect(snapResize(A, { x: 1, y: 1 }, { x: 0, y: 0, w: 196, h: 98 }, true, context([B]))).toEqual({ geometry: { x: 0, y: 0, w: 196, h: 98 }, guides: [] });
    const turned = { ...A, rotation: 30 };
    expect(snapResize(turned, { x: 1, y: 1 }, { ...turned, w: 196, h: 47 }, false, context([B]))).toEqual({ geometry: { ...turned, w: 196, h: 47 }, guides: [] });
  });

  it('keeps an axis unsnapped when its snap would take the node under the minimum size', () => {
    // Dragging the left handle in until the node is 8 wide at x 92; a target edge at 97 would leave it 3 wide.
    const target: Rect = { x: 0, y: 200, w: 97, h: 10 };
    const snapped = snapResize(A, { x: 0, y: 0.5 }, { x: 92, y: 0, w: 8, h: 50 }, false, context([target]));
    expect(snapped).toEqual({ geometry: { x: 92, y: 0, w: 8, h: 50 }, guides: [] });
  });
});

describe('snapTargets', () => {
  it('offers every placed node the gesture does not move, and no node awaiting layout', () => {
    const document: Document = {
      id: 'doc_targets',
      schemaVersion: 1,
      title: 'Targets',
      type: 'flowchart',
      nodes: [
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50 } },
        { id: 'n_u', kind: 'service', label: 'U', shape: 'rectangle' },
      ],
      edges: [],
      meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
    };
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const index = indexDocument(document);
    warn.mockRestore();
    expect(snapTargets(index, new Set(['n_a']))).toEqual([{ x: 200, y: 0, w: 100, h: 50 }]);
    expect(snapTargets(index, new Set(['n_b']))).toEqual([{ x: 0, y: 0, w: 100, h: 50 }]);
  });
});
