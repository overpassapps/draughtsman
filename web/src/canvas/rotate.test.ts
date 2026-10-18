import type { Geometry } from '../model/types.ts';
import { angleFromCentre, normalizeRotation, rotateGeometry, rotationStep } from './rotate.ts';

const start: Geometry = { x: 100, y: 100, w: 200, h: 100 };
const centre = { x: 200, y: 150 };

describe('rotationStep', () => {
  it('is 1° plain and 15° with Shift', () => {
    expect(rotationStep(false)).toBe(1);
    expect(rotationStep(true)).toBe(15);
  });
});

describe('normalizeRotation', () => {
  it('wraps 360 and above down into [0, 360)', () => {
    expect(normalizeRotation(360)).toBe(0);
    expect(normalizeRotation(361)).toBe(1);
    expect(normalizeRotation(720 + 15)).toBe(15);
  });

  it('wraps a negative angle up into [0, 360)', () => {
    expect(normalizeRotation(-1)).toBe(359);
    expect(normalizeRotation(-360)).toBe(0);
    expect(normalizeRotation(-361)).toBe(359);
  });

  it('leaves an angle already inside the range untouched', () => {
    expect(normalizeRotation(0)).toBe(0);
    expect(normalizeRotation(45)).toBe(45);
    expect(normalizeRotation(359)).toBe(359);
  });
});

describe('angleFromCentre', () => {
  it('reads 0 due east, growing clockwise like geometry.rotation', () => {
    // East of centre.
    expect(angleFromCentre(centre, { x: centre.x + 50, y: centre.y })).toBeCloseTo(0, 9);
    // South of centre: a quarter-turn clockwise from east.
    expect(angleFromCentre(centre, { x: centre.x, y: centre.y + 50 })).toBeCloseTo(90, 9);
    // West of centre: a half-turn.
    expect(angleFromCentre(centre, { x: centre.x - 50, y: centre.y })).toBeCloseTo(180, 9);
    // North of centre: three-quarters (equivalently -90).
    expect(angleFromCentre(centre, { x: centre.x, y: centre.y - 50 })).toBeCloseTo(-90, 9);
  });
});

describe('rotateGeometry', () => {
  it('adds the pointer angle delta to the start rotation, quantised to 1°', () => {
    // Press at the handle (due north of centre); drag the pointer on 37.6° further clockwise around the centre.
    const startAngle = angleFromCentre(centre, { x: centre.x, y: centre.y - 50 });
    const pointerAngle = startAngle + 37.6;
    // The delta alone (37.6, independent of where the handle started), quantised to the nearest degree.
    expect(rotateGeometry(start, startAngle, pointerAngle, false)).toEqual({ ...start, rotation: 38 });
  });

  it('changes only rotation: the node keeps its position and size', () => {
    const rotated = rotateGeometry(start, 0, 40, false);
    expect(rotated.x).toBe(start.x);
    expect(rotated.y).toBe(start.y);
    expect(rotated.w).toBe(start.w);
    expect(rotated.h).toBe(start.h);
  });

  it('rotates clockwise for a clockwise drag and anticlockwise for an anticlockwise drag', () => {
    const clockwise = rotateGeometry(start, 0, 10, false);
    const anticlockwise = rotateGeometry(start, 0, -10, false);
    expect(clockwise.rotation).toBe(10);
    expect(anticlockwise.rotation).toBe(350);
    // Same magnitude, opposite sense: not the same value, and neither is a copy of the input used to build it.
    expect(clockwise.rotation).not.toBe(anticlockwise.rotation);
  });

  it('quantises to 1° steps without Shift, rounding to the nearest degree', () => {
    expect(rotateGeometry(start, 0, 1.49, false).rotation).toBe(1);
    expect(rotateGeometry(start, 0, 1.51, false).rotation).toBe(2);
  });

  it('quantises to 15° steps with Shift, rounding to the nearest multiple', () => {
    expect(rotateGeometry(start, 0, 7, true).rotation).toBe(0);
    expect(rotateGeometry(start, 0, 8, true).rotation).toBe(15);
    expect(rotateGeometry(start, 0, 22, true).rotation).toBe(15);
    expect(rotateGeometry(start, 0, 23, true).rotation).toBe(30);
    // Anticlockwise past zero rounds to the 15° step on the other side, not back to 0.
    expect(rotateGeometry(start, 0, -8, true).rotation).toBe(345);
  });

  it('normalises past a full turn in either direction', () => {
    expect(rotateGeometry({ ...start, rotation: 350 }, 0, 20, false).rotation).toBe(10);
    expect(rotateGeometry({ ...start, rotation: 10 }, 0, -20, false).rotation).toBe(350);
  });

  it('rotates from a non-zero start rotation, not just from 0', () => {
    const fromRotated = rotateGeometry({ ...start, rotation: 45 }, 0, 30, false);
    expect(fromRotated.rotation).toBe(75);
    // A regression guard: this must differ from rotating the same delta from an unrotated start.
    expect(fromRotated.rotation).not.toBe(rotateGeometry(start, 0, 30, false).rotation);
  });
});
