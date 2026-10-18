import { describe, expect, it } from 'vitest';
import { alignNodes, distributeNodes, MIN_ALIGN_NODES, MIN_DISTRIBUTE_NODES, type Moves } from './align.ts';
import type { Rect } from './geometry.ts';

function rects(entries: Record<string, Rect>): Map<string, Rect> {
  return new Map(Object.entries(entries));
}

/** The rectangles after applying the moves: sizes kept, positions replaced where a move exists. */
function applied(input: ReadonlyMap<string, Rect>, moves: Moves): Map<string, Rect> {
  const out = new Map<string, Rect>();
  for (const [id, r] of input) {
    const p = moves.get(id);
    out.set(id, p ? { ...r, x: p.x, y: p.y } : r);
  }
  return out;
}

/** The x a node was moved to; undefined (failing any numeric assertion) when it did not move. */
function movedX(moves: Moves, id: string): number | undefined {
  return moves.get(id)?.x;
}

/** Gaps between neighbours on an axis, in order of position. */
function gapsOf(result: ReadonlyMap<string, Rect>, axis: 'x' | 'y'): number[] {
  const size = axis === 'x' ? 'w' : 'h';
  const gaps: number[] = [];
  let previous: Rect | undefined;
  for (const r of [...result.values()].sort((a, b) => a[axis] - b[axis])) {
    if (previous) {
      gaps.push(r[axis] - (previous[axis] + previous[size]));
    }
    previous = r;
  }
  return gaps;
}

/** Differences between consecutive values. */
function steps(values: readonly number[]): number[] {
  return values.slice(1).map((v, i) => v - (values[i] ?? v));
}

// Three nodes of different sizes, spread on both axes: x spans -5..55 (centre 25), y spans 0..110 (centre 55).
const trio = rects({
  a: { x: 10, y: 0, w: 40, h: 20 },
  b: { x: 30, y: 50, w: 20, h: 30 },
  c: { x: -5, y: 100, w: 60, h: 10 },
});

describe('align', () => {
  it('needs two nodes', () => {
    expect(MIN_ALIGN_NODES).toBe(2);
    expect(alignNodes(rects({ a: { x: 7, y: 3, w: 10, h: 10 } }), 'left').size).toBe(0);
    expect(alignNodes(new Map(), 'centre').size).toBe(0);
    expect(alignNodes(rects({ a: { x: 7, y: 3, w: 10, h: 10 }, b: { x: 20, y: 3, w: 10, h: 10 } }), 'left')).toEqual(
      new Map([['b', { x: 7, y: 3 }]]),
    );
  });

  it('left moves every node to the leftmost edge and leaves the outermost node out', () => {
    expect(alignNodes(trio, 'left')).toEqual(
      new Map([
        ['a', { x: -5, y: 0 }],
        ['b', { x: -5, y: 50 }],
      ]),
    );
  });

  it('right moves every right edge to the rightmost edge', () => {
    const moves = alignNodes(trio, 'right');
    expect(moves).toEqual(
      new Map([
        ['a', { x: 15, y: 0 }],
        ['b', { x: 35, y: 50 }],
      ]),
    );
    expect([...applied(trio, moves).values()].map((r) => r.x + r.w)).toEqual([55, 55, 55]);
  });

  it('centre moves every centre onto the centre of the bounding box, not the mean of the centres', () => {
    // Mean of the centres is (30 + 40 + 25) / 3 = 31.67; the bounding-box centre is 25.
    const moves = alignNodes(trio, 'centre');
    expect(moves).toEqual(
      new Map([
        ['a', { x: 5, y: 0 }],
        ['b', { x: 15, y: 50 }],
      ]),
    );
    expect([...applied(trio, moves).values()].map((r) => r.x + r.w / 2)).toEqual([25, 25, 25]);
  });

  it('top moves every node to the topmost edge', () => {
    expect(alignNodes(trio, 'top')).toEqual(
      new Map([
        ['b', { x: 30, y: 0 }],
        ['c', { x: -5, y: 0 }],
      ]),
    );
  });

  it('middle moves every vertical centre onto the centre of the bounding box', () => {
    const moves = alignNodes(trio, 'middle');
    expect(moves).toEqual(
      new Map([
        ['a', { x: 10, y: 45 }],
        ['b', { x: 30, y: 40 }],
        ['c', { x: -5, y: 50 }],
      ]),
    );
    expect([...applied(trio, moves).values()].map((r) => r.y + r.h / 2)).toEqual([55, 55, 55]);
  });

  it('bottom moves every bottom edge to the lowest edge', () => {
    expect(alignNodes(trio, 'bottom')).toEqual(
      new Map([
        ['a', { x: 10, y: 90 }],
        ['b', { x: 30, y: 80 }],
      ]),
    );
  });

  it('does not round a half-unit centre', () => {
    // Bounding box 0..11, centre 5.5, so the 4-wide node lands at 3.5 rather than 3 or 4.
    const moves = alignNodes(rects({ a: { x: 0, y: 0, w: 11, h: 10 }, b: { x: 2, y: 20, w: 4, h: 10 } }), 'centre');
    expect(moves).toEqual(new Map([['b', { x: 3.5, y: 20 }]]));
  });

  it('returns nothing when the nodes are already aligned', () => {
    const aligned = rects({ a: { x: 4, y: 0, w: 10, h: 10 }, b: { x: 4, y: 30, w: 30, h: 10 } });
    expect(alignNodes(aligned, 'left').size).toBe(0);
  });
});

describe('distribute', () => {
  it('needs three nodes', () => {
    expect(MIN_DISTRIBUTE_NODES).toBe(3);
    const two = rects({ a: { x: 0, y: 0, w: 10, h: 10 }, b: { x: 50, y: 0, w: 10, h: 10 } });
    expect(distributeNodes(two, 'horizontal').size).toBe(0);
  });

  it('places the middle node so both gaps are equal, keeping the outer two fixed', () => {
    // Span 0..100, sizes 10 + 20 + 20 = 50, so each of the two gaps is 25 and b lands at 10 + 25 = 35.
    const input = rects({
      a: { x: 0, y: 5, w: 10, h: 10 },
      b: { x: 12, y: 40, w: 20, h: 10 },
      c: { x: 80, y: 7, w: 20, h: 10 },
    });
    const moves = distributeNodes(input, 'horizontal');
    expect(moves).toEqual(new Map([['b', { x: 35, y: 40 }]]));
    expect(gapsOf(applied(input, moves), 'x')).toEqual([25, 25]);
  });

  it('gives nodes of different sizes equal gaps rather than equal centre spacing', () => {
    // Span 0..110, sizes 10 + 30 + 20 + 10 = 70, so each of the three gaps is 40 / 3.
    const input = rects({
      a: { x: 0, y: 0, w: 10, h: 10 },
      b: { x: 15, y: 0, w: 30, h: 10 },
      c: { x: 70, y: 0, w: 20, h: 10 },
      d: { x: 100, y: 0, w: 10, h: 10 },
    });
    const moves = distributeNodes(input, 'horizontal');
    expect([...moves.keys()]).toEqual(['b', 'c']);
    expect(movedX(moves, 'b')).toBeCloseTo(70 / 3, 10);
    expect(movedX(moves, 'c')).toBeCloseTo(200 / 3, 10);

    const result = applied(input, moves);
    for (const gap of gapsOf(result, 'x')) {
      expect(gap).toBeCloseTo(40 / 3, 10);
    }
    const centres = [...result.values()].map((r) => r.x + r.w / 2).sort((p, q) => p - q);
    expect(distributeNodes(result, 'horizontal')).toEqual(new Map());
    const spacings = steps(centres);
    expect(Math.max(...spacings) - Math.min(...spacings)).toBeGreaterThan(1);
  });

  it('keeps the fractional gap instead of rounding, so every gap is equal to the pixel', () => {
    // 40 / 3 is not a whole number; rounding positions to integers would make the gaps 13, 14 and 13.
    const input = rects({
      a: { x: 0, y: 0, w: 10, h: 10 },
      b: { x: 15, y: 0, w: 30, h: 10 },
      c: { x: 70, y: 0, w: 20, h: 10 },
      d: { x: 100, y: 0, w: 10, h: 10 },
    });
    const gaps = gapsOf(applied(input, distributeNodes(input, 'horizontal')), 'x');
    expect(Math.max(...gaps) - Math.min(...gaps)).toBeLessThan(1e-9);
    expect(Number.isInteger(gaps[0])).toBe(false);
  });

  it('orders by centre and keeps the outer two fixed whatever the input order', () => {
    const input = rects({
      c: { x: 80, y: 7, w: 20, h: 10 },
      b: { x: 12, y: 40, w: 20, h: 10 },
      a: { x: 0, y: 5, w: 10, h: 10 },
    });
    expect(distributeNodes(input, 'horizontal')).toEqual(new Map([['b', { x: 35, y: 40 }]]));
  });

  it('breaks a tie between equal centres by id regardless of input order', () => {
    // Span 0..110, sizes 40, gap 70 / 3: the second slot is at 10 + 70/3, the third at 20 + 140/3.
    const outer = { a: { x: 0, y: 0, w: 10, h: 10 }, d: { x: 100, y: 0, w: 10, h: 10 } };
    const same = { x: 40, y: 0, w: 10, h: 10 };
    const bFirst = distributeNodes(rects({ ...outer, b: same, c: { ...same, y: 20 } }), 'horizontal');
    expect(movedX(bFirst, 'b')).toBeCloseTo(10 + 70 / 3, 10);
    expect(movedX(bFirst, 'c')).toBeCloseTo(20 + 140 / 3, 10);
    const cFirst = distributeNodes(rects({ ...outer, c: { ...same, y: 20 }, b: same }), 'horizontal');
    expect(cFirst).toEqual(bFirst);
  });

  it.each([
    { bX: -40, cX: 10 },
    { bX: -42, cX: 11 },
  ])('uses stable equal centre spacing for overfull nodes at $bX and $cX', ({ bX, cX }) => {
    const input = rects({
      A: { x: 0, y: 1, w: 10, h: 10 },
      B: { x: bX, y: 2, w: 100, h: 10 },
      C: { x: cX, y: 3, w: 10, h: 10 },
      D: { x: 15, y: 4, w: 10, h: 10 },
    });
    const result = applied(input, distributeNodes(input, 'horizontal'));
    expect(result).toEqual(rects({
      A: { x: 0, y: 1, w: 10, h: 10 },
      B: { x: -40, y: 2, w: 100, h: 10 },
      C: { x: 10, y: 3, w: 10, h: 10 },
      D: { x: 15, y: 4, w: 10, h: 10 },
    }));
    expect([...result].sort(([, a], [, b]) => a.x + a.w / 2 - (b.x + b.w / 2))
      .map(([id]) => id)).toEqual(['A', 'B', 'C', 'D']);
    expect([...result.values()].map((r) => r.x + r.w / 2)).toEqual([5, 10, 15, 20]);
    expect(distributeNodes(result, 'horizontal')).toEqual(new Map());
  });

  it('distributes vertically on y and leaves x alone', () => {
    // Span 0..100 on y, sizes 10 + 20 + 20 = 50, so b lands at 10 + 25 = 35.
    const input = rects({
      a: { x: 5, y: 0, w: 10, h: 10 },
      b: { x: 40, y: 12, w: 10, h: 20 },
      c: { x: 7, y: 80, w: 10, h: 20 },
    });
    const moves = distributeNodes(input, 'vertical');
    expect(moves).toEqual(new Map([['b', { x: 40, y: 35 }]]));
    expect(gapsOf(applied(input, moves), 'y')).toEqual([25, 25]);
  });

  it('returns nothing when the nodes are already evenly spaced', () => {
    const even = rects({
      a: { x: 0, y: 0, w: 10, h: 10 },
      b: { x: 35, y: 0, w: 20, h: 10 },
      c: { x: 80, y: 0, w: 20, h: 10 },
    });
    expect(distributeNodes(even, 'horizontal').size).toBe(0);
  });
});
