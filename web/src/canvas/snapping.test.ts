import { describe, expect, it } from 'vitest';
import { MOVE_EDGES, SNAP_PIXELS, snapRect, type MovingEdges, type SnapParams, type SnapResult } from './snapping.ts';
import type { Rect } from './geometry.ts';
import { loadScale } from '../testing/loadScale.ts';

function run(over: Partial<SnapParams> & { moving?: Rect } = {}): SnapResult {
  return snapRect({
    moving: { x: 0, y: 0, w: 40, h: 40 },
    edges: MOVE_EDGES,
    others: [],
    gridSize: 20,
    gridEnabled: false,
    threshold: 6,
    ...over,
  });
}

function guidesOf(result: SnapResult, kind: SnapResult['guides'][number]['kind']): SnapResult['guides'] {
  return result.guides.filter((guide) => guide.kind === kind);
}

describe('snapping', () => {
  it('exposes the 6 px magnet the checklist names', () => {
    expect(SNAP_PIXELS).toBe(6);
  });

  describe('grid', () => {
    it('snaps a dragging node to the nearest grid line when within threshold', () => {
      const result = run({ moving: { x: 43, y: 22, w: 50, h: 26 }, gridEnabled: true });
      expect(result.rect).toEqual({ x: 40, y: 20, w: 50, h: 26 });
      expect(guidesOf(result, 'grid')).toEqual([
        { kind: 'grid', from: { x: 40, y: 20 }, to: { x: 40, y: 46 } },
        { kind: 'grid', from: { x: 40, y: 20 }, to: { x: 90, y: 20 } },
      ]);
    });

    it('quantises to the nearest grid line even when every moving edge is outside the threshold', () => {
      const result = run({ moving: { x: 47, y: 13, w: 20, h: 14 }, gridEnabled: true });
      expect(result.rect).toEqual({ x: 40, y: 20, w: 20, h: 14 });
      expect(guidesOf(result, 'grid')).toEqual([
        { kind: 'grid', from: { x: 40, y: 20 }, to: { x: 40, y: 34 } },
        { kind: 'grid', from: { x: 40, y: 20 }, to: { x: 60, y: 20 } },
      ]);
    });

    it('snaps only the moving edge of a resize and leaves the anchor in place', () => {
      const rightHandle: MovingEdges = { left: false, right: true, top: false, bottom: false };
      const result = run({ moving: { x: 10, y: 0, w: 28, h: 20 }, edges: rightHandle, gridEnabled: true });
      expect(result.rect).toEqual({ x: 10, y: 0, w: 30, h: 20 });
    });

    it('keeps a resize on the far side of an off-grid anchor instead of collapsing it', () => {
      const rightHandle: MovingEdges = { left: false, right: true, top: false, bottom: false };
      const result = run({ moving: { x: 43, y: 0, w: 6, h: 20 }, edges: rightHandle, gridEnabled: true });
      expect(result.rect).toEqual({ x: 43, y: 0, w: 17, h: 20 });
    });

    it('keeps a left-handle resize from reaching an on-grid anchor', () => {
      const leftHandle: MovingEdges = { left: true, right: false, top: false, bottom: false };
      const result = run({ moving: { x: 36, y: 0, w: 4, h: 20 }, edges: leftHandle, gridEnabled: true });
      expect(result.rect).toEqual({ x: 20, y: 0, w: 20, h: 20 });
    });

    it('does not snap when the grid is disabled', () => {
      const result = run({ moving: { x: 43, y: 10, w: 50, h: 30 }, gridEnabled: false });
      expect(result.rect.x).toBe(43);
      expect(result.guides).toEqual([]);
    });
  });

  describe('edge and centre alignment', () => {
    it('snaps a dragging edge to another edge within threshold and spans both nodes', () => {
      const other: Rect = { x: 45, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [other] });
      expect(result.rect).toEqual({ x: 5, y: 0, w: 40, h: 40 });
      expect(guidesOf(result, 'edge')).toEqual([{ kind: 'edge', from: { x: 45, y: 0 }, to: { x: 45, y: 120 } }]);
    });

    it('does not snap when the edge is just outside the threshold', () => {
      const other: Rect = { x: 47, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [other] });
      expect(result.rect).toEqual({ x: 0, y: 0, w: 40, h: 40 });
      expect(result.guides).toEqual([]);
    });

    it('snaps to a centre and reports a centre guide', () => {
      const other: Rect = { x: 50, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 22, y: 0, w: 40, h: 40 }, others: [other] });
      expect(result.rect.x).toBe(20);
      expect(guidesOf(result, 'centre')).toEqual([{ kind: 'centre', from: { x: 60, y: 0 }, to: { x: 60, y: 120 } }]);
    });

    it("snaps the moving node's centre to another node's centre", () => {
      const other: Rect = { x: 100, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 88, y: 0, w: 40, h: 40 }, others: [other] });
      expect(result.rect.x).toBe(90);
      expect(guidesOf(result, 'centre')).toEqual([{ kind: 'centre', from: { x: 110, y: 0 }, to: { x: 110, y: 120 } }]);
    });

    it('spans the guide across the node at its snapped position on the other axis', () => {
      const xNeighbour: Rect = { x: 44, y: 100, w: 10, h: 10 };
      const yNeighbour: Rect = { x: 200, y: 42, w: 10, h: 10 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [xNeighbour, yNeighbour] });
      expect(result.guides).toEqual([
        { kind: 'edge', from: { x: 44, y: 2 }, to: { x: 44, y: 110 } },
        { kind: 'edge', from: { x: 4, y: 42 }, to: { x: 210, y: 42 } },
      ]);
    });

    it('chooses the nearest candidate when two nodes are both within threshold', () => {
      const far: Rect = { x: 45, y: 100, w: 10, h: 10 };
      const near: Rect = { x: 44, y: 100, w: 10, h: 10 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [far, near] });
      expect(result.rect.x).toBe(4);
      expect(result.guides).toEqual([{ kind: 'edge', from: { x: 44, y: 0 }, to: { x: 44, y: 110 } }]);
    });

    it('breaks an exact tie deterministically by input order', () => {
      const first: Rect = { x: 45, y: 100, w: 10, h: 10 };
      const second: Rect = { x: 45, y: 100, w: 10, h: 30 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [first, second] });
      expect(result.rect.x).toBe(5);
      expect(guidesOf(result, 'edge')).toEqual([{ kind: 'edge', from: { x: 45, y: 0 }, to: { x: 45, y: 110 } }]);
    });

    it('snaps each axis independently against different nodes', () => {
      const xNeighbour: Rect = { x: 44, y: 100, w: 10, h: 10 };
      const yNeighbour: Rect = { x: 200, y: 42, w: 10, h: 10 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [xNeighbour, yNeighbour] });
      expect(result.rect).toEqual({ x: 4, y: 2, w: 40, h: 40 });
      expect(result.guides).toHaveLength(2);
    });
  });

  describe('equal spacing', () => {
    it('snaps the gap to a matching gap between two aligned nodes and reports an equal-spacing guide', () => {
      const neighbour: Rect = { x: 60, y: 10, w: 20, h: 20 };
      const refA: Rect = { x: 300, y: 100, w: 20, h: 20 };
      const refB: Rect = { x: 340, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 5, y: 0, w: 40, h: 40 }, others: [neighbour, refA, refB] });
      expect(result.rect.x).toBe(0);
      expect(guidesOf(result, 'equal-spacing')).toEqual([
        { kind: 'equal-spacing', from: { x: 40, y: 20 }, to: { x: 60, y: 20 } },
      ]);
    });

    it('does not snap when the gap is outside the threshold of every reference gap', () => {
      const neighbour: Rect = { x: 60, y: 10, w: 20, h: 20 };
      const refA: Rect = { x: 300, y: 100, w: 20, h: 20 };
      const refB: Rect = { x: 350, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 5, y: 0, w: 40, h:40 }, others: [neighbour, refA, refB] });
      expect(result.rect.x).toBe(5);
      expect(guidesOf(result, 'equal-spacing')).toEqual([]);
    });

    it('finds the reference gap in a column-aligned grid layout', () => {
      const a: Rect = { x: 0, y: 0, w: 40, h: 40 };
      const b: Rect = { x: 100, y: 0, w: 40, h: 40 };
      const c: Rect = { x: 0, y: 100, w: 40, h: 40 };
      const d: Rect = { x: 100, y: 100, w: 40, h: 40 };
      const result = run({ moving: { x: 205, y: 0, w: 40, h: 40 }, others: [a, b, c, d] });
      expect(result.rect.x).toBe(200);
      expect(guidesOf(result, 'equal-spacing')).toEqual([
        { kind: 'equal-spacing', from: { x: 140, y: 20 }, to: { x: 200, y: 20 } },
      ]);
    });

    it('only measures the gap to the nearest neighbour on each side', () => {
      const near: Rect = { x: 60, y: 0, w: 20, h: 20 };
      const far: Rect = { x: 240, y: 0, w: 20, h: 20 };
      const refA: Rect = { x: 0, y: 300, w: 20, h: 20 };
      const refB: Rect = { x: 218, y: 300, w: 20, h: 20 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [near, far, refA, refB] });
      expect(result.rect.x).toBe(0);
      expect(guidesOf(result, 'equal-spacing')).toEqual([]);
    });

    it('snaps vertically too', () => {
      const neighbour: Rect = { x: 10, y: 55, w: 20, h: 20 };
      const refA: Rect = { x: 100, y: 300, w: 20, h: 20 };
      const refB: Rect = { x: 100, y: 340, w: 20, h: 20 };
      const result = run({ moving: { x: 0, y: 0, w: 40, h: 40 }, others: [neighbour, refA, refB] });
      expect(result.rect.y).toBe(-5);
      expect(guidesOf(result, 'equal-spacing')).toEqual([
        { kind: 'equal-spacing', from: { x: 20, y: 35 }, to: { x: 20, y: 55 } },
      ]);
    });
  });

  describe('precedence', () => {
    it('prefers an object snap over the grid when both are within threshold', () => {
      const other: Rect = { x: 46, y: 100, w: 20, h: 20 };
      const result = run({ moving: { x: 43, y: 20, w: 60, h: 20 }, others: [other], gridEnabled: true });
      expect(result.rect.x).toBe(46);
      expect(guidesOf(result, 'edge')).toHaveLength(1);
      expect(guidesOf(result, 'grid')).toEqual([{ kind: 'grid', from: { x: 46, y: 20 }, to: { x: 106, y: 20 } }]);
    });
  });

  describe('performance', () => {
    it('snaps among 2000 other nodes within a frame', () => {
      const others: Rect[] = [];
      for (let i = 0; i < 2000; i++) {
        others.push({ x: 200 + (i % 50) * 24, y: 200 + Math.floor(i / 50) * 60, w: 20, h: 40 });
      }
      const moving: Rect = { x: 218, y: 250, w: 40, h: 40 };
      // Median over several runs after a warm-up, so the sample is not the first JIT pass on a busy runner.
      let result = run({ moving, others, gridEnabled: true });
      const samples: number[] = [];
      for (let i = 0; i < 9; i++) {
        const start = performance.now();
        result = run({ moving, others, gridEnabled: true });
        samples.push(performance.now() - start);
      }
      samples.sort((a, b) => a - b);
      expect(result.rect).toBeDefined();
      expect(samples[Math.floor(samples.length / 2)]).toBeLessThan(100 * loadScale());
    });
  });
});