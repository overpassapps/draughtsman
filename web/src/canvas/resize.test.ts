import type { Geometry } from '../model/types.ts';
import { STATE_FORK_JOIN_SHAPE, STATE_SHAPES, stateShapeName } from '../model/stateShapes.ts';
import { HANDLE_ANCHORS, lockedHandle, MIN_NODE_SIZE, resizeGeometry, resizeLockForShape } from './resize.ts';

const start: Geometry = { x: 100, y: 100, w: 200, h: 100 };

/** A handle by where it sits, so a test reads as the corner or edge it drags rather than an index. */
function handle(x: number, y: number) {
  const found = HANDLE_ANCHORS.find((anchor) => anchor.x === x && anchor.y === y);
  if (!found) {
    throw new Error(`no handle at ${String(x)},${String(y)}`);
  }
  return found;
}

function expectClose(actual: Geometry, expected: Geometry): void {
  expect(Object.keys(actual).sort()).toEqual(Object.keys(expected).sort());
  for (const key of Object.keys(expected) as (keyof Geometry)[]) {
    expect(actual[key], key).toBeCloseTo(expected[key] ?? 0, 9);
  }
}

describe('resizeGeometry', () => {
  it('has eight handles: four corners and four edge midpoints', () => {
    const spots = HANDLE_ANCHORS.map((a) => `${String(a.x)},${String(a.y)}`).sort();
    expect(spots).toEqual(['0,0', '0,0.5', '0,1', '0.5,0', '0.5,1', '1,0', '1,0.5', '1,1']);
  });

  it('drags a corner with the opposite corner held still', () => {
    // Bottom-right: the top-left corner (100,100) stays put.
    expect(resizeGeometry(start, handle(1, 1), { x: 50, y: 20 }, false)).toEqual({ x: 100, y: 100, w: 250, h: 120 });
    // Top-left: the bottom-right corner (300,200) stays put.
    expect(resizeGeometry(start, handle(0, 0), { x: 50, y: 20 }, false)).toEqual({ x: 150, y: 120, w: 150, h: 80 });
    // Top-right: the bottom-left corner (100,200) stays put.
    expect(resizeGeometry(start, handle(1, 0), { x: 30, y: -40 }, false)).toEqual({ x: 100, y: 60, w: 230, h: 140 });
  });

  it('drags an edge in one dimension only, whatever the pointer does along it', () => {
    expect(resizeGeometry(start, handle(1, 0.5), { x: 50, y: 999 }, false)).toEqual({ x: 100, y: 100, w: 250, h: 100 });
    expect(resizeGeometry(start, handle(0.5, 0), { x: 999, y: -30 }, false)).toEqual({ x: 100, y: 70, w: 200, h: 130 });
    expect(resizeGeometry(start, handle(0, 0.5), { x: -20, y: 5 }, false)).toEqual({ x: 80, y: 100, w: 220, h: 100 });
  });

  it('keeps the aspect ratio with Shift, following the axis that moved further, and not without', () => {
    // 2:1 start; the pointer asks for 1.5x wide and 1.1x tall.
    expect(resizeGeometry(start, handle(1, 1), { x: 100, y: 10 }, true)).toEqual({ x: 100, y: 100, w: 300, h: 150 });
    expect(resizeGeometry(start, handle(1, 1), { x: 100, y: 10 }, false)).toEqual({ x: 100, y: 100, w: 300, h: 110 });
    // Now the vertical pull is the larger: 2x tall wins over 1.1x wide.
    expect(resizeGeometry(start, handle(1, 1), { x: 20, y: 100 }, true)).toEqual({ x: 100, y: 100, w: 400, h: 200 });
  });

  it('scales the other dimension about the anchor edge when Shift is held on an edge handle', () => {
    // Right edge +100: 1.5x, so the height grows 50 split evenly about the unchanged centre line y = 150.
    expect(resizeGeometry(start, handle(1, 0.5), { x: 100, y: 0 }, true)).toEqual({ x: 100, y: 75, w: 300, h: 150 });
  });

  it('stops at the minimum size instead of inverting when dragged past the anchor', () => {
    expect(MIN_NODE_SIZE).toBe(8);
    expect(resizeGeometry(start, handle(1, 1), { x: -500, y: -500 }, false)).toEqual({ x: 100, y: 100, w: 8, h: 8 });
    // From the other side the far corner (300,200) still holds, so the minimum box ends there.
    expect(resizeGeometry(start, handle(0, 0), { x: 500, y: 500 }, false)).toEqual({ x: 292, y: 192, w: 8, h: 8 });
    // With Shift the ratio survives the clamp: the short side reaches the minimum first.
    expect(resizeGeometry(start, handle(1, 1), { x: -500, y: -500 }, true)).toEqual({ x: 100, y: 100, w: 16, h: 8 });
  });

  it('never grows a node that started below the minimum just because it was touched', () => {
    const small: Geometry = { x: 0, y: 0, w: 4, h: 4 };
    expect(resizeGeometry(small, handle(1, 1), { x: -10, y: -10 }, false)).toEqual({ x: 0, y: 0, w: 4, h: 4 });
  });

  it('resizes a rotated node along its own axes, keeping its rotation and its anchor in world space', () => {
    // Rotated 90° clockwise about its centre (100,50): its local +x points down the screen.
    const rotated: Geometry = { x: 0, y: 0, w: 200, h: 100, rotation: 90 };
    // Pulling the local right edge 50 down the screen widens it; the local left edge midpoint, world (100,-50), holds.
    expectClose(resizeGeometry(rotated, handle(1, 0.5), { x: 0, y: 50 }, false), { x: -25, y: 25, w: 250, h: 100, rotation: 90 });
    // The same pull across the screen runs along the local y axis, which that handle does not resize.
    expectClose(resizeGeometry(rotated, handle(1, 0.5), { x: 50, y: 0 }, false), rotated);
  });
});

describe('single-axis resize', () => {
  it('locks the fork/join bar and only the fork/join bar', () => {
    expect(resizeLockForShape(STATE_FORK_JOIN_SHAPE)).toBe('horizontal');
    // Two-sided: every other state shape and every other shape is free.
    for (const shape of STATE_SHAPES) {
      if (shape.name === 'fork-join') {
        continue;
      }
      expect(resizeLockForShape(stateShapeName(shape.name)), shape.name).toBeUndefined();
    }
    expect(resizeLockForShape('rounded')).toBeUndefined();
  });

  it('pins the perpendicular axis to the middle so only the bar lengthens', () => {
    const lock = resizeLockForShape(STATE_FORK_JOIN_SHAPE);
    // A corner drag keeps its x and pins y; an edge drag on the length keeps only x.
    expect(lockedHandle(handle(1, 1), lock)).toEqual({ x: 1, y: 0.5 });
    expect(lockedHandle(handle(1, 0), lock)).toEqual({ x: 1, y: 0.5 });
    // A vertical drag of a locked bar does nothing: the effective handle is the centre column.
    expect(resizeGeometry(start, lockedHandle(handle(1, 1), lock), { x: 0, y: 80 }, false)).toEqual(start);
    // A horizontal drag still lengthens it, from the left anchor.
    expect(resizeGeometry(start, lockedHandle(handle(1, 1), lock), { x: 50, y: 0 }, false)).toEqual({ x: 100, y: 100, w: 250, h: 100 });
    // A free shape passes the handle through unchanged.
    expect(lockedHandle(handle(1, 1), undefined)).toBe(handle(1, 1));
  });
});
