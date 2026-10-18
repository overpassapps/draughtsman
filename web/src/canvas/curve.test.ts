import { curveMidpoint, smoothCurvePath } from './curve.ts';
import { distance, pointAlongPolyline } from './geometry.ts';
import type { Point } from '../model/types.ts';

/** Parses the anchors (the `M` and each `C`'s final point) out of a curve `d` string. */
function anchorsOf(d: string): Point[] {
  const tokens = d.split(' ').filter(Boolean);
  const anchors: Point[] = [];
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token === 'M' || token === 'L' || token === 'C') {
      const point = tokens[i + 1]?.split(',').map(Number) ?? [];
      if (token === 'C') {
        // The last coordinate pair of a C is its endpoint; control points come before it.
        const end = tokens[i + 3]?.split(',').map(Number) ?? [];
        anchors.push({ x: end[0] ?? NaN, y: end[1] ?? NaN });
      } else if (point.length === 2) {
        anchors.push({ x: point[0] ?? NaN, y: point[1] ?? NaN });
      }
    }
  }
  return anchors;
}

describe('smoothCurvePath', () => {
  it('draws nothing for fewer than two points', () => {
    expect(smoothCurvePath([])).toBe('');
    expect(smoothCurvePath([{ x: 0, y: 0 }])).toBe('');
  });

  it('draws a straight line for two points', () => {
    expect(smoothCurvePath([{ x: 1, y: 2 }, { x: 3, y: 4 }])).toBe('M 1,2 L 3,4');
  });

  it('passes exactly through every waypoint and endpoint', () => {
    const points: Point[] = [
      { x: 0, y: 0 },
      { x: 100, y: 80 },
      { x: 200, y: 20 },
      { x: 300, y: 100 },
    ];
    const anchors = anchorsOf(smoothCurvePath(points));
    expect(anchors).toHaveLength(points.length);
    for (let i = 0; i < points.length; i++) {
      expect(distance(anchors[i] ?? { x: NaN, y: NaN }, points[i] ?? { x: NaN, y: NaN })).toBeLessThan(0.001);
    }
  });

  it('leaves a two-point curve as a straight line, never a self-crossing spline', () => {
    // A 2-point curve has no interior segment, so it must be straight, matching the straight polyline's midpoint.
    const d = smoothCurvePath([{ x: 0, y: 0 }, { x: 10, y: 0 }]);
    expect(d).toBe('M 0,0 L 10,0');
  });

  it('produces control points that bend the curve, not a straight chain', () => {
    // A V-shaped set of three points: the middle segment must carry a curve, so the path holds a C command.
    const d = smoothCurvePath([{ x: 0, y: 0 }, { x: 10, y: 50 }, { x: 20, y: 0 }]);
    expect(d).toContain(' C ');
    expect(d.split('C').length - 1).toBe(2);
  });
});

/** Parses a smooth curve `d` string back into the cubic Béziers it drew, independently of curve.ts's internals. */
function cubicsOf(d: string): { p0: Point; c1: Point; c2: Point; p3: Point }[] {
  const tokens = d.split(' ').filter(Boolean);
  const pair = (token: string | undefined): Point => {
    const [x, y] = (token ?? '').split(',').map(Number);
    return { x: x ?? NaN, y: y ?? NaN };
  };
  const out: { p0: Point; c1: Point; c2: Point; p3: Point }[] = [];
  let current: Point | undefined;
  for (let i = 0; i < tokens.length; i++) {
    if (tokens[i] === 'M' || tokens[i] === 'L') {
      current = pair(tokens[i + 1]);
      i += 1;
    } else if (tokens[i] === 'C') {
      const c1 = pair(tokens[i + 1]);
      const c2 = pair(tokens[i + 2]);
      const p3 = pair(tokens[i + 3]);
      if (current !== undefined) {
        out.push({ p0: current, c1, c2, p3 });
      }
      current = p3;
      i += 3;
    }
  }
  return out;
}

/** A point on a cubic Bézier, weighted by its Bernstein polynomials: independent of the implementation under test. */
function bezierAt(cubic: { p0: Point; c1: Point; c2: Point; p3: Point }, t: number): Point {
  const mt = 1 - t;
  const a = mt * mt * mt;
  const b = 3 * mt * mt * t;
  const c = 3 * mt * t * t;
  const d = t * t * t;
  return {
    x: a * cubic.p0.x + b * cubic.c1.x + c * cubic.c2.x + d * cubic.p3.x,
    y: a * cubic.p0.y + b * cubic.c1.y + c * cubic.c2.y + d * cubic.p3.y,
  };
}

/** The nearest distance from `point` to the curve `d`, found by sampling every cubic finely. */
function distanceToCurve(d: string, point: Point): number {
  let nearest = Number.POSITIVE_INFINITY;
  for (const cubic of cubicsOf(d)) {
    for (let i = 0; i <= 400; i++) {
      nearest = Math.min(nearest, distance(point, bezierAt(cubic, i / 400)));
    }
  }
  return nearest;
}

describe('curveMidpoint', () => {
  it('returns the segment midpoint of a straight two-point curve, so its label does not move', () => {
    const straight: Point[] = [{ x: 0, y: 0 }, { x: 200, y: 60 }];
    const at = curveMidpoint(straight);
    expect(at.x).toBeCloseTo(100, 6);
    expect(at.y).toBeCloseTo(30, 6);
    // And it matches the polyline midpoint exactly where a curve has no turn to make.
    expect(distance(at, pointAlongPolyline(straight, 0.5))).toBeLessThan(1e-6);
  });

  it('places the midpoint on the drawn curve for an asymmetric spline', () => {
    // A waypoint far to one side bends the spline, so its arc-length midpoint is not on the straight chord.
    const asymmetric: Point[] = [
      { x: 0, y: 0 },
      { x: 300, y: 0 },
      { x: 320, y: 40 },
      { x: 360, y: 0 },
    ];
    const at = curveMidpoint(asymmetric);
    expect(distanceToCurve(smoothCurvePath(asymmetric), at)).toBeLessThan(1);
  });

  it('differs from the polyline midpoint of the same asymmetric curve', () => {
    // Two-sided: the curve's own midpoint is on the curve, while the polyline midpoint (what the label used to use)
    // is measurably off it, so the two are not the same point dressed up.
    const asymmetric: Point[] = [
      { x: 0, y: 0 },
      { x: 300, y: 0 },
      { x: 320, y: 40 },
      { x: 360, y: 0 },
    ];
    const at = curveMidpoint(asymmetric);
    const polyline = pointAlongPolyline(asymmetric, 0.5);
    expect(distance(at, polyline)).toBeGreaterThan(5);
    expect(distanceToCurve(smoothCurvePath(asymmetric), polyline)).toBeGreaterThan(1);
  });
});
