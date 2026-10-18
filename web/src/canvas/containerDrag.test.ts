import { describe, expect, it } from 'vitest';
import { dropTarget, moveWithContents } from '../model/containers.ts';
import type { Document, Geometry, Id } from '../model/types.ts';
import { dropParents, dropPlan, dropTargets, translateWaypoints, waypointStarts } from './containerDrag.ts';
import { rotatedBounds } from './geometry.ts';
import { indexDocument } from './documentIndex.ts';
import { dragStarts } from './selection.ts';

// c_vpc (0,0 400x300) holds n_in (20,20 40x40) and n_in2 (200,200 40x40); c_other (500,0 200x200) is empty; n_out
// (500,400 40x40) is top level. e_inside joins the two children, e_across leaves c_vpc, e_bare has no waypoints.
function doc(): Document {
  return {
    id: 'doc_drag',
    schemaVersion: 1,
    title: 'Drag',
    type: 'architecture',
    nodes: [
      { id: 'c_vpc', kind: 'container', label: 'VPC', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 300 } },
      { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', parent: 'c_vpc', geometry: { x: 20, y: 20, w: 40, h: 40 } },
      { id: 'n_in2', kind: 'service', label: 'In 2', shape: 'rectangle', parent: 'c_vpc', geometry: { x: 200, y: 200, w: 40, h: 40 } },
      { id: 'c_other', kind: 'container', label: 'Other', shape: 'rectangle', geometry: { x: 500, y: 0, w: 200, h: 200 } },
      { id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 500, y: 400, w: 40, h: 40 } },
    ],
    edges: [
      { id: 'e_inside', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_in2' }, routing: { mode: 'straight', waypoints: [{ x: 100, y: 40 }, { x: 100, y: 220 }] } },
      { id: 'e_across', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_out' }, routing: { mode: 'straight', waypoints: [{ x: 300, y: 420 }] } },
      { id: 'e_bare', kind: 'flow', from: { node: 'n_in' }, to: { node: 'n_in2' } },
    ],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 'test' },
  };
}

const startsOf = (document: Document, selection: Id[]): Map<Id, Geometry> => dragStarts(indexDocument(document), new Set(selection));

describe('waypointStarts', () => {
  it('carries the waypoints of edges with both ends moving, and only those', () => {
    const document = doc();
    const starts = waypointStarts(document, startsOf(document, ['c_vpc']));
    expect([...starts.keys()]).toEqual(['e_inside']);
    expect(translateWaypoints(starts, 30, -10).get('e_inside')).toEqual([{ x: 130, y: 30 }, { x: 130, y: 210 }]);
  });

  it('matches moveWithContents for a container drag, nodes and waypoints alike', () => {
    const document = doc();
    const starts = startsOf(document, ['c_vpc']);
    const expected = moveWithContents(document, 'c_vpc', { x: 30, y: -10 });
    const moved = new Map([...starts].map(([id, start]) => [id, { ...start, x: start.x + 30, y: start.y - 10 }]));
    expect(moved).toEqual(expected.nodes);
    expect(translateWaypoints(waypointStarts(document, starts), 30, -10)).toEqual(expected.waypoints);
  });

  it('moves nothing when only one end moves', () => {
    const document = doc();
    expect(waypointStarts(document, startsOf(document, ['n_out'])).size).toBe(0);
    expect(waypointStarts(document, startsOf(document, ['n_in'])).size).toBe(0);
  });
});

describe('dropTargets and dropParents', () => {
  it('drops a node into the container its box ends up inside', () => {
    const document = doc();
    const starts = startsOf(document, ['n_out']);
    // n_out to (550,50): fully inside c_other.
    expect(dropTargets(dropPlan(document, starts), { x: 50, y: -350 })).toEqual(new Map([['n_out', 'c_other']]));
    expect(dropParents(document, dropPlan(document, starts), { x: 50, y: -350 })).toEqual(new Map([['n_out', 'c_other']]));
  });

  it('clears the parent of a node dragged out of its container, and keeps it for a move within', () => {
    const document = doc();
    const starts = startsOf(document, ['n_in']);
    // n_in to (20,420): below c_vpc, inside nothing.
    expect(dropParents(document, dropPlan(document, starts), { x: 0, y: 400 })).toEqual(new Map([['n_in', null]]));
    // n_in to (120,120): still inside c_vpc, so nothing changes.
    expect(dropTargets(dropPlan(document, starts), { x: 100, y: 100 })).toEqual(new Map([['n_in', 'c_vpc']]));
    expect(dropParents(document, dropPlan(document, starts), { x: 100, y: 100 }).size).toBe(0);
  });

  it('keeps a node that straddles a container border out of it', () => {
    const document = doc();
    // n_out to (480,50): its left 20 units hang outside c_other.
    expect(dropParents(document, dropPlan(document, startsOf(document, ['n_out'])), { x: -20, y: -350 }).size).toBe(0);
  });

  it('never drops into a container that moves in the same drag, and never reparents children that move with theirs', () => {
    const document = doc();
    // c_vpc and n_out move together by (-400,-300), which puts n_out at (100,100): inside c_vpc's old box, where a
    // target read from the document before the drag would wrongly find it.
    const starts = startsOf(document, ['c_vpc', 'n_out']);
    const targets = dropTargets(dropPlan(document, starts), { x: -400, y: -300 });
    expect(targets.has('n_in')).toBe(false);
    expect(targets.get('n_out')).toBeNull();
    expect(targets.get('c_vpc')).toBeNull();
  });

  it('picks what dropTarget picks for each root, on the document without the other moved nodes', () => {
    // Beside doc(): c_outer (0,500 300x200) clips c_nested (200,550 300x100), so only its left 100 units take a drop;
    // n_holder is a plain node holding n_kid, so it is a container only while n_kid is its child; n_waiting has no
    // geometry and holds n_under, which is dragged with neither; n_side is dragged alongside.
    const base = doc();
    const document: Document = {
      ...base,
      nodes: [
        ...base.nodes,
        { id: 'c_outer', kind: 'container', label: 'Outer', shape: 'rectangle', geometry: { x: 0, y: 500, w: 300, h: 200 } },
        { id: 'c_nested', kind: 'container', label: 'Nested', shape: 'rectangle', parent: 'c_outer', geometry: { x: 200, y: 550, w: 300, h: 100 } },
        { id: 'n_holder', kind: 'service', label: 'Holder', shape: 'rectangle', geometry: { x: 800, y: 0, w: 200, h: 200 } },
        { id: 'n_kid', kind: 'service', label: 'Kid', shape: 'rectangle', parent: 'n_holder', geometry: { x: 820, y: 20, w: 40, h: 40 } },
        { id: 'n_waiting', kind: 'service', label: 'Waiting', shape: 'rectangle', parent: 'n_kid' },
        { id: 'n_under', kind: 'container', label: 'Under', shape: 'rectangle', parent: 'n_waiting', geometry: { x: 900, y: 300, w: 200, h: 200 } },
        { id: 'n_side', kind: 'service', label: 'Side', shape: 'rectangle', geometry: { x: 1200, y: 0, w: 40, h: 40 } },
      ],
    };
    const starts = startsOf(document, ['n_kid', 'n_out', 'n_side']);
    const plan = dropPlan(document, starts);
    const deltas = [{ x: 0, y: 0 }, { x: 10, y: 10 }, { x: 100, y: 320 }, { x: -800, y: 530 }, { x: -600, y: 560 }, { x: 100, y: 300 }, { x: -300, y: 0 }, { x: -1170, y: 530 }];
    for (const delta of deltas) {
      const expected = new Map(
        [...starts].map(([id, start]) => {
          const others = { ...document, nodes: document.nodes.filter((node) => node.id === id || !starts.has(node.id)) };
          return [id, dropTarget(others, id, rotatedBounds({ ...start, x: start.x + delta.x, y: start.y + delta.y }))] as const;
        }),
      );
      expect(dropTargets(plan, delta)).toEqual(expected);
    }
    // Spot checks: n_kid stays in n_holder, never drops into n_under (its own descendant through n_waiting), and takes
    // only c_nested's visible part.
    expect(dropTargets(plan, { x: 10, y: 10 }).get('n_kid')).toBe('n_holder');
    expect(dropTargets(plan, { x: 100, y: 320 }).get('n_kid')).toBeNull();
    expect(dropTargets(plan, { x: -600, y: 560 }).get('n_kid')).toBe('c_nested');
    expect(dropTargets(plan, { x: -800, y: 530 }).get('n_kid')).toBe('c_outer');
  });

  it('never drops a root into its own descendant, even one reached through an unplaced node that stays behind', () => {
    // c_r holds n_m (placed, so it moves with c_r), which holds n_u (no geometry, so it stays), which holds c_x.
    const base = doc();
    const document: Document = {
      ...base,
      nodes: [
        ...base.nodes,
        { id: 'c_r', kind: 'container', label: 'R', shape: 'rectangle', geometry: { x: 0, y: 1000, w: 100, h: 100 } },
        { id: 'n_m', kind: 'service', label: 'M', shape: 'rectangle', parent: 'c_r', geometry: { x: 10, y: 1010, w: 20, h: 20 } },
        { id: 'n_u', kind: 'service', label: 'U', shape: 'rectangle', parent: 'n_m' },
        { id: 'c_x', kind: 'container', label: 'X', shape: 'rectangle', parent: 'n_u', geometry: { x: 500, y: 1000, w: 300, h: 300 } },
      ],
    };
    const starts = startsOf(document, ['c_r']);
    expect([...starts.keys()].sort()).toEqual(['c_r', 'n_m']);
    const plan = dropPlan(document, starts);
    // c_r to (550,1050): inside c_x's box, which would make c_r its own ancestor.
    expect(dropTargets(plan, { x: 550, y: 50 })).toEqual(new Map([['c_r', null]]));
    expect(dropParents(document, plan, { x: 550, y: 50 }).size).toBe(0);
  });

  it('keeps a saved parent that is not a drop candidate unless the node lands in another container', () => {
    // c_unplaced has no geometry, so n_kept renders at the top level while its saved parent stays c_unplaced.
    const base = doc();
    const document: Document = {
      ...base,
      nodes: [
        ...base.nodes,
        { id: 'c_unplaced', kind: 'container', label: 'Unplaced', shape: 'rectangle' },
        { id: 'n_kept', kind: 'service', label: 'Kept', shape: 'rectangle', parent: 'c_unplaced', geometry: { x: 900, y: 400, w: 40, h: 40 } },
      ],
    };
    const plan = dropPlan(document, startsOf(document, ['n_kept']));
    expect(dropParents(document, plan, { x: 5, y: 3 }).size).toBe(0);
    // n_kept to (550,50): inside c_other, a real container, so it moves there.
    expect(dropParents(document, plan, { x: -350, y: -350 })).toEqual(new Map([['n_kept', 'c_other']]));
  });

  it('never writes a set of parent changes that forms a cycle together', () => {
    // n_a holds unplaced n_v, which holds c_y; n_b holds unplaced n_u, which holds c_x. Dragged together by (900,50),
    // n_a lands in c_x and n_b in c_y: each is fine alone, but both would make n_a → c_x → n_u → n_b → c_y → n_v → n_a.
    const base = doc();
    const document: Document = {
      ...base,
      nodes: [
        ...base.nodes,
        { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 2000, w: 100, h: 100 } },
        { id: 'n_v', kind: 'service', label: 'V', shape: 'rectangle', parent: 'n_a' },
        { id: 'c_y', kind: 'container', label: 'Y', shape: 'rectangle', parent: 'n_v', geometry: { x: 1000, y: 2000, w: 300, h: 300 } },
        { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 200, y: 2000, w: 100, h: 100 } },
        { id: 'n_u', kind: 'service', label: 'U', shape: 'rectangle', parent: 'n_b' },
        { id: 'c_x', kind: 'container', label: 'X', shape: 'rectangle', parent: 'n_u', geometry: { x: 850, y: 2000, w: 400, h: 300 } },
      ],
    };
    const plan = dropPlan(document, startsOf(document, ['n_a', 'n_b']));
    expect(dropTargets(plan, { x: 900, y: 50 })).toEqual(new Map([['n_a', 'c_x'], ['n_b', 'c_y']]));
    expect(dropParents(document, plan, { x: 900, y: 50 })).toEqual(new Map([['n_a', 'c_x']]));
  });
});
