import { curveWaypointAt, middleSegmentAt, moveCurvedWaypoint, moveOrthSegment } from './segmentDrag.ts';
import type { Point } from '../model/types.ts';

// A bent orthogonal route: start (0,0) to a bend at (100,0), up to (100,100), then right to the end at (200,100).
// Interior points are [ (100,0), (100,100) ]; the middle segment is the vertical one between them.
const bentRoute: Point[] = [
  { x: 0, y: 0 },
  { x: 100, y: 0 },
  { x: 100, y: 100 },
  { x: 200, y: 100 },
];

describe('middleSegmentAt', () => {
  it('finds the middle segment under the pointer and ignores the end segments', () => {
    // The vertical middle segment runs x=100, y 0..100.
    expect(middleSegmentAt(bentRoute, { x: 100, y: 50 }, 5)).toBe(1);
    // The first segment (leaving the start) is not a middle segment, so a point on it finds nothing.
    expect(middleSegmentAt(bentRoute, { x: 50, y: 0 }, 5)).toBeUndefined();
    // Likewise the last segment entering the end.
    expect(middleSegmentAt(bentRoute, { x: 150, y: 100 }, 5)).toBeUndefined();
  });

  it('finds nothing when the pointer is out of reach of every middle segment', () => {
    expect(middleSegmentAt(bentRoute, { x: 500, y: 500 }, 5)).toBeUndefined();
  });

  it('returns nothing for a route with fewer than four points', () => {
    expect(middleSegmentAt([{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }], { x: 100, y: 50 }, 5)).toBeUndefined();
  });
});

describe('moveOrthSegment', () => {
  it('moves the dragged middle segment perpendicular to itself, pinning interior waypoints', () => {
    // Middle segment (index 1) is vertical; a horizontal delta moves it, a vertical delta does not.
    const moved = moveOrthSegment(bentRoute, 1, { x: 30, y: 0 });
    expect(moved).toEqual([
      { x: 130, y: 0 },
      { x: 130, y: 100 },
    ]);
    // A delta along the segment's own axis moves its endpoints along nothing (perpendicular part is zero).
    expect(moveOrthSegment(bentRoute, 1, { x: 0, y: 50 })).toEqual([
      { x: 100, y: 0 },
      { x: 100, y: 100 },
    ]);
  });

  it('keeps every other interior waypoint exactly as it was', () => {
    const longer: Point[] = [
      { x: 0, y: 0 },
      { x: 100, y: 0 },
      { x: 100, y: 100 },
      { x: 150, y: 100 },
      { x: 150, y: 200 },
      { x: 200, y: 200 },
    ];
    // Middle segment index 2 is horizontal (between (100,100) and (150,100)); a vertical delta lifts it.
    expect(moveOrthSegment(longer, 2, { x: 0, y: 40 })).toEqual([
      { x: 100, y: 0 }, // untouched
      { x: 100, y: 140 }, // endpoint of the lifted segment
      { x: 150, y: 140 }, // endpoint of the lifted segment
      { x: 150, y: 200 }, // untouched
    ]);
  });
});

describe('curveWaypointAt and moveCurvedWaypoint', () => {
  const waypoints: Point[] = [
    { x: 50, y: 50 },
    { x: 150, y: 80 },
  ];

  it('finds the nearest waypoint within reach, or none', () => {
    expect(curveWaypointAt(waypoints, { x: 52, y: 48 }, 5)).toBe(0);
    expect(curveWaypointAt(waypoints, { x: 148, y: 82 }, 5)).toBe(1);
    expect(curveWaypointAt(waypoints, { x: 300, y: 300 }, 5)).toBeUndefined();
  });

  it('moves an existing waypoint to the pointer', () => {
    expect(moveCurvedWaypoint(waypoints, 1, { x: 200, y: 200 })).toEqual([
      { x: 50, y: 50 },
      { x: 200, y: 200 },
    ]);
  });

  it('appends a new waypoint at the pointer for index -1', () => {
    expect(moveCurvedWaypoint(waypoints, -1, { x: 200, y: 200 })).toEqual([
      { x: 50, y: 50 },
      { x: 150, y: 80 },
      { x: 200, y: 200 },
    ]);
  });
});