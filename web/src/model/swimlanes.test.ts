/**
 * The swimlane model (model/swimlanes.ts) and the container rule that lets a lane take a dropped node. Each property
 * is checked in both directions: a band maths fact and its opposite, a layout that changes and one that is already
 * packed, an inward drop and one that lands outside.
 */
import { indexDocument } from '../canvas/documentIndex.ts';
import { dragStarts } from '../canvas/selection.ts';
import { reparent, dropTarget } from './containers.ts';
import {
  addLanePlan,
  isLane,
  isPool,
  laneBands,
  laneHeaderHeight,
  layoutLanes,
  moveLanePlan,
  NEW_POOL_LANES,
  NEW_POOL_SIZE,
  orderedLanes,
  poolHeaderHeight,
  poolLaneArea,
  poolOrientation,
  poolWithLanes,
  relayoutAfterMoves,
} from './swimlanes.ts';
import type { Document } from './types.ts';

const META = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };

/** Narrows a lookup so the tests read plainly without a non-null assertion. */
function defined<T>(value: T | undefined): T {
  if (value === undefined) {
    throw new Error('expected a defined value');
  }
  return value;
}

function node(doc: Document, id: string): Document['nodes'][number] {
  return defined(doc.nodes.find((candidate) => candidate.id === id));
}

function document(overrides: Partial<Document> = {}): Document {
  return {
    id: 'doc_swim',
    schemaVersion: 1,
    title: 'Swim',
    type: 'flowchart',
    nodes: [
      { id: 'c_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool', geometry: { x: 0, y: 0, w: 400, h: 300 } },
      { id: 'c_lane1', kind: 'lane', label: 'Lane 1', shape: 'custom:swimlane/lane', parent: 'c_pool', geometry: { x: 0, y: 48, w: 400, h: 126 } },
      { id: 'c_lane2', kind: 'lane', label: 'Lane 2', shape: 'custom:swimlane/lane', parent: 'c_pool', geometry: { x: 0, y: 174, w: 400, h: 126 } },
      { id: 'n_task', kind: 'process', label: 'Task', shape: 'rounded', geometry: { x: 60, y: 60, w: 100, h: 40 } },
    ],
    edges: [],
    meta: META,
    ...overrides,
  };
}

describe('swimlane kinds and orientation', () => {
  it('reads a pool as a pool and a lane as a lane, and a plain node as neither', () => {
    const doc = document();
    expect(isPool(node(doc, 'c_pool'))).toBe(true);
    expect(isPool(node(doc, 'c_lane1'))).toBe(false);
    expect(isLane(node(doc, 'c_lane1'))).toBe(true);
    expect(isLane(node(doc, 'n_task'))).toBe(false);
    expect(isPool(node(doc, 'n_task'))).toBe(false);
  });

  it('defaults a pool to horizontal and reads a vertical override, ignoring anything else', () => {
    const doc = document();
    expect(poolOrientation(node(doc, 'c_pool'))).toBe('horizontal');
    expect(poolOrientation({ ...node(doc, 'c_pool'), props: { orientation: 'vertical' } })).toBe('vertical');
    expect(poolOrientation({ ...node(doc, 'c_pool'), props: { orientation: 'sideways' } })).toBe('horizontal');
  });
});

describe('title bands', () => {
  it('holds the pool band between its floor and cap and never past the box', () => {
    expect(poolHeaderHeight(300)).toBe(48);
    expect(poolHeaderHeight(100)).toBe(24);
    expect(poolHeaderHeight(5)).toBe(5);
  });

  it('holds the lane band between its floor and cap, shallower than the pool band at a normal size', () => {
    expect(laneHeaderHeight(300)).toBe(32);
    expect(laneHeaderHeight(80)).toBe(16);
    // Two-sided: at the same height the pool band is the deeper one.
    expect(poolHeaderHeight(160)).toBeGreaterThan(laneHeaderHeight(160));
  });
});

describe('lane bands', () => {
  it('packs horizontal lanes into equal rows inside the band-free area', () => {
    const bands = laneBands(document(), 'c_pool');
    expect([...bands]).toEqual([
      ['c_lane1', { x: 0, y: 48, w: 400, h: 126 }],
      ['c_lane2', { x: 0, y: 174, w: 400, h: 126 }],
    ]);
    expect(poolLaneArea(node(document(), 'c_pool'))).toEqual({ x: 0, y: 48, w: 400, h: 252 });
  });

  it('packs vertical lanes into equal columns, same band-free area', () => {
    const doc = document({ nodes: document().nodes.map((candidate) => (candidate.id === 'c_pool' ? { ...candidate, props: { orientation: 'vertical' } } : candidate)) });
    const bands = laneBands(doc, 'c_pool');
    expect([...bands]).toEqual([
      ['c_lane1', { x: 0, y: 48, w: 200, h: 252 }],
      ['c_lane2', { x: 200, y: 48, w: 200, h: 252 }],
    ]);
  });

  it('returns nothing for a pool with no geometry or no lanes', () => {
    const noGeometry = document({ nodes: document().nodes.filter((candidate) => candidate.id !== 'c_pool').concat({ id: 'c_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool' }) });
    expect(laneBands(noGeometry, 'c_pool').size).toBe(0);
    const noLanes = document({ nodes: document().nodes.filter((candidate) => !isLane(candidate)) });
    expect(laneBands(noLanes, 'c_pool').size).toBe(0);
  });

  it('orders lanes by document order, not geometry', () => {
    const doc = document();
    expect(orderedLanes(doc, 'c_pool').map((candidate) => candidate.id)).toEqual(['c_lane1', 'c_lane2']);
    const reversed = document({ nodes: [...doc.nodes].reverse() });
    expect(orderedLanes(reversed, 'c_pool').map((candidate) => candidate.id)).toEqual(['c_lane2', 'c_lane1']);
  });
});

describe('layoutLanes', () => {
  it('changes nothing when the lanes are already packed, and repacks them when the pool resizes', () => {
    expect(layoutLanes(document(), 'c_pool').size).toBe(0);
    const taller = document({ nodes: document().nodes.map((candidate) => (candidate.id === 'c_pool' ? { ...candidate, geometry: { x: 0, y: 0, w: 400, h: 400 } } : candidate)) });
    const changed = layoutLanes(taller, 'c_pool');
    expect([...changed]).toEqual([
      ['c_lane1', { x: 0, y: 48, w: 400, h: 176 }],
      ['c_lane2', { x: 0, y: 224, w: 400, h: 176 }],
    ]);
  });
});

describe('addLanePlan', () => {
  it('adds a lane at the far edge and repacks the pool, proposing an unused id', () => {
    const plan = defined(addLanePlan(document(), 'c_pool', () => 'c_lane3'));
    expect(plan.lane).toMatchObject({ id: 'c_lane3', kind: 'lane', parent: 'c_pool', shape: 'custom:swimlane/lane' });
    expect(plan.lane.geometry).toEqual({ x: 0, y: 216, w: 400, h: 84 });
    expect([...plan.geometries]).toEqual([
      ['c_lane1', { x: 0, y: 48, w: 400, h: 84 }],
      ['c_lane2', { x: 0, y: 132, w: 400, h: 84 }],
      ['c_lane3', { x: 0, y: 216, w: 400, h: 84 }],
    ]);
  });

  it('refuses a pool that is not a pool or has no geometry', () => {
    expect(addLanePlan(document(), 'n_task')).toBeUndefined();
    const unplaced = document({ nodes: document().nodes.map((candidate) => (candidate.id === 'c_pool' ? { id: 'c_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool' } : candidate)) });
    expect(addLanePlan(unplaced, 'c_pool')).toBeUndefined();
  });
});

describe('moveLanePlan', () => {
  it('swaps a lane with its neighbour and repacks, and refuses at either end', () => {
    const doc = document();
    const plan = defined(moveLanePlan(doc, 'c_lane2', -1));
    expect(plan.nodes.filter((candidate) => isLane(candidate)).map((candidate) => candidate.id)).toEqual(['c_lane2', 'c_lane1']);
    expect([...plan.geometries]).toEqual([
      ['c_lane2', { x: 0, y: 48, w: 400, h: 126 }],
      ['c_lane1', { x: 0, y: 174, w: 400, h: 126 }],
    ]);
    expect(moveLanePlan(doc, 'c_lane1', -1)).toBeUndefined();
    expect(moveLanePlan(doc, 'c_lane2', 1)).toBeUndefined();
    expect(moveLanePlan(doc, 'n_task', 1)).toBeUndefined();
  });
});

describe('relayoutAfterMoves', () => {
  it('repacks a resizing pool and leaves a moving pool’s lanes alone', () => {
    const resize = relayoutAfterMoves(document(), new Map([['c_pool', { x: 0, y: 0, w: 400, h: 400 }]]));
    expect([...resize]).toEqual([
      ['c_lane1', { x: 0, y: 48, w: 400, h: 176 }],
      ['c_lane2', { x: 0, y: 224, w: 400, h: 176 }],
    ]);
    // A moved pool's lanes travel with it (they are in the same frame's moves), so nothing is repacked.
    const move = relayoutAfterMoves(
      document(),
      new Map([
        ['c_pool', { x: 10, y: 0, w: 400, h: 300 }],
        ['c_lane1', { x: 10, y: 48, w: 400, h: 126 }],
        ['c_lane2', { x: 10, y: 174, w: 400, h: 126 }],
      ]),
    );
    expect(move.size).toBe(0);
  });
});

describe('a lane is a drop target', () => {
  it('picks the lane for a node dropped inside it, and the pool for one dropped in its header', () => {
    const doc = document();
    expect(dropTarget(doc, 'n_task', { x: 50, y: 70, w: 20, h: 20 })).toBe('c_lane1');
    expect(dropTarget(doc, 'n_task', { x: 50, y: 10, w: 20, h: 20 })).toBe('c_pool');
  });

  it('picks nothing for a node dropped outside the pool', () => {
    expect(dropTarget(document(), 'n_task', { x: 500, y: 70, w: 20, h: 20 })).toBeNull();
  });

  it('reparents a node into and out of a lane without moving it', () => {
    const doc = document();
    expect(reparent(doc, 'n_task', 'c_lane1')).toEqual({ changed: true, nodeId: 'n_task', parent: 'c_lane1' });
    const inLane = document({ nodes: doc.nodes.map((candidate) => (candidate.id === 'n_task' ? { ...candidate, parent: 'c_lane1' } : candidate)) });
    expect(reparent(inLane, 'n_task', null)).toEqual({ changed: true, nodeId: 'n_task', parent: null });
  });

  it('moves the nodes inside a lane when the lane is dragged', () => {
    const doc = document({ nodes: [...document().nodes, { id: 'n_in', kind: 'process', label: 'In', shape: 'rounded', parent: 'c_lane1', geometry: { x: 70, y: 70, w: 60, h: 30 } }] });
    const starts = dragStarts(indexDocument(doc), new Set(['c_lane1']));
    expect([...starts.keys()].sort()).toEqual(['c_lane1', 'n_in']);
  });
});

describe('poolWithLanes (P4)', () => {
  const pool = { id: 'n_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool', geometry: { x: 100, y: 60, ...NEW_POOL_SIZE } };

  it('gives a dropped pool its lanes, packed edge to edge inside the pool under its title band, parented to it', () => {
    const added = poolWithLanes(document({ nodes: [] }), pool);
    expect(added.map((n) => n.kind)).toEqual(['pool', ...Array.from({ length: NEW_POOL_LANES }, () => 'lane')]);
    expect(added.slice(1).map((n) => n.parent)).toEqual(Array.from({ length: NEW_POOL_LANES }, () => 'n_pool'));
    expect(added.slice(1).map((n) => n.label)).toEqual(['Lane 1', 'Lane 2']);
    // The lane geometry is exactly what packing the same pool's lanes gives (so a later resize repacks to the same place).
    const doc = document({ nodes: added });
    const packed = laneBands(doc, 'n_pool');
    for (const lane of added.slice(1)) {
      expect(lane.geometry).toEqual(packed.get(lane.id));
    }
    const area = defined(poolLaneArea(pool));
    expect(defined(added[1]?.geometry).y).toBe(area.y);
    expect(defined(added[2]?.geometry).y + defined(added[2]?.geometry).h).toBe(area.y + area.h);
    expect(new Set(added.map((n) => n.id)).size).toBe(added.length);
  });

  it('is a sensible working size, not the everyday 160 x 80 box', () => {
    expect(NEW_POOL_SIZE.w).toBeGreaterThanOrEqual(480);
    expect(NEW_POOL_SIZE.h).toBeGreaterThanOrEqual(240);
  });

  it('keeps clear of ids the document already uses', () => {
    let next = 0;
    const taken = document({ nodes: [{ id: 'l_0', kind: 'process', label: 'x', shape: 'rectangle' }] });
    const added = poolWithLanes(taken, pool, () => `l_${String(next++)}`);
    expect(added.slice(1).map((n) => n.id)).toEqual(['l_1', 'l_2']);
  });
});
