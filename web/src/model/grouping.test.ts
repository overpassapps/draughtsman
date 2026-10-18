import { describe, expect, it } from 'vitest';
import { canFitContents, containerPadding, fitContainers, group, ungroup } from './grouping.ts';
import type { Document } from './types.ts';

// Absolute geometry throughout. c_vpc (0,0 600x400) holds n_a (100,100 50x50) and n_b (200,150 40x40); n_c is top
// level at (400,500 60x30); n_free has no geometry. Edge e_ab joins n_a and n_b, e_vpc joins c_vpc to n_c.
function doc(): Document {
  return {
    id: 'doc_grouping',
    schemaVersion: 1,
    title: 'Grouping',
    type: 'architecture',
    nodes: [
      { id: 'c_vpc', kind: 'container', label: 'VPC', shape: 'rectangle', geometry: { x: 0, y: 0, w: 600, h: 400 } },
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rounded', parent: 'c_vpc', geometry: { x: 100, y: 100, w: 50, h: 50 } },
      { id: 'n_x', kind: 'service', label: 'X', shape: 'rounded', geometry: { x: 700, y: 0, w: 50, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rounded', parent: 'c_vpc', geometry: { x: 200, y: 150, w: 40, h: 40 } },
      { id: 'n_c', kind: 'service', label: 'C', shape: 'rounded', geometry: { x: 400, y: 500, w: 60, h: 30 } },
      { id: 'n_free', kind: 'service', label: 'Free', shape: 'rounded' },
    ],
    edges: [
      { id: 'e_ab', kind: 'data', from: { node: 'n_a' }, to: { node: 'n_b' } },
      { id: 'e_vpc', kind: 'data', from: { node: 'c_vpc' }, to: { node: 'n_c' } },
    ],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 'test' },
  };
}

const ids = (document: { nodes: { id: string }[] }) => document.nodes.map((node) => node.id);

describe('containerPadding', () => {
  it('is two grid steps, and never negative', () => {
    expect(containerPadding(20)).toBe(40);
    expect(containerPadding(8)).toBe(16);
    expect(containerPadding(-5)).toBe(0);
  });
});

describe('group', () => {
  it('wraps the selection in a fitted container with the padding on all four sides and moves no child', () => {
    const plan = group(doc(), new Set(['n_a', 'n_b']), 40, () => 'c_new');
    expect(plan).toBeDefined();
    // Children span x 100..240 and y 100..190, so the box is that plus 40 on every side.
    expect(plan?.container).toEqual({ id: 'c_new', kind: 'container', label: 'Container', shape: 'rectangle', parent: 'c_vpc', geometry: { x: 60, y: 60, w: 220, h: 170 } });
    expect(plan?.children).toEqual(['n_a', 'n_b']);
    const byId = new Map(plan?.nodes.map((node) => [node.id, node]));
    expect(byId.get('n_a')).toMatchObject({ parent: 'c_new', geometry: { x: 100, y: 100, w: 50, h: 50 } });
    expect(byId.get('n_b')).toMatchObject({ parent: 'c_new', geometry: { x: 200, y: 150, w: 40, h: 40 } });
    // Nodes outside the selection keep their parent (or lack of one).
    expect(byId.get('n_x')?.parent).toBeUndefined();
    expect(byId.get('n_c')?.parent).toBeUndefined();
  });

  it('inserts the container just before the topmost grouped node in render order', () => {
    const plan = group(doc(), new Set(['n_a', 'n_b']), 40, () => 'c_new');
    expect(plan && ids(plan)).toEqual(['c_vpc', 'n_a', 'n_x', 'c_new', 'n_b', 'n_c', 'n_free']);
  });

  it('parents the group to the nodes’ nearest common ancestor, or the top level when they share none', () => {
    expect(group(doc(), new Set(['n_a', 'n_b']), 0, () => 'c_new')?.container.parent).toBe('c_vpc');
    expect(group(doc(), new Set(['n_a', 'n_c']), 0, () => 'c_new')?.container.parent).toBeUndefined();
  });

  it('groups only the top-most selected nodes: a selected container carries its selected children', () => {
    const plan = group(doc(), new Set(['c_vpc', 'n_a']), 0, () => 'c_new');
    expect(plan?.children).toEqual(['c_vpc']);
    expect(plan?.nodes.find((node) => node.id === 'n_a')?.parent).toBe('c_vpc');
    expect(plan?.container.geometry).toEqual({ x: 0, y: 0, w: 600, h: 400 });
  });

  it('leaves out edges and nodes without geometry, and does nothing when that leaves nothing', () => {
    expect(group(doc(), new Set(['n_c', 'n_free', 'e_ab']), 10, () => 'c_new')?.children).toEqual(['n_c']);
    expect(group(doc(), new Set(['n_free', 'e_ab']), 10, () => 'c_new')).toBeUndefined();
    expect(group(doc(), new Set(), 10, () => 'c_new')).toBeUndefined();
  });

  it('never reuses an id already in the document', () => {
    const proposals = ['n_a', 'e_ab', 'c_fresh'];
    const plan = group(doc(), new Set(['n_c']), 0, () => proposals.shift() ?? 'c_exhausted');
    expect(plan?.container.id).toBe('c_fresh');
    expect(() => group(doc(), new Set(['n_c']), 0, () => 'n_a')).toThrow(/stuck/);
  });
});

describe('one level of parent growth', () => {
  it('grows the parent only on the sides a new group crosses, leaving the padding there, and not otherwise', () => {
    // Padding 40 keeps the group (60,60 130x130) inside c_vpc (0,0 600x400): nothing grows.
    expect(group(doc(), new Set(['n_a', 'n_b']), 40, () => 'c_new')?.resized.size).toBe(0);
    // Padding 120 makes it (-20,-20 380x330): it crosses c_vpc's left and top only, so those two sides move out to
    // leave 120 there and the right and bottom stay at 600 and 400.
    const plan = group(doc(), new Set(['n_a', 'n_b']), 120, () => 'c_new');
    expect(plan?.container.geometry).toEqual({ x: -20, y: -20, w: 380, h: 330 });
    expect(plan?.resized).toEqual(new Map([['c_vpc', { x: -140, y: -140, w: 740, h: 540 }]]));
  });

  it('grows an unselected parent a fitted container crosses, but not the grandparent that parent then crosses', () => {
    const inner = group(doc(), new Set(['n_a', 'n_b']), 5, () => 'c_inner');
    if (!inner) throw new Error('no group');
    const nested: Document = {
      ...doc(),
      nodes: [{ id: 'c_top', kind: 'container', label: 'Top', shape: 'rectangle', geometry: { x: 0, y: 0, w: 600, h: 400 } }, ...inner.nodes.map((node) => (node.id === 'c_vpc' ? { ...node, parent: 'c_top' } : node))],
    };
    // c_inner refitted with 150 is (-50,-50 440x390), past c_vpc's left and top.
    const fitted = fitContainers(nested, new Set(['c_inner']), 150);
    expect(fitted).toEqual(
      new Map([
        ['c_inner', { x: -50, y: -50, w: 440, h: 390 }],
        ['c_vpc', { x: -200, y: -200, w: 800, h: 600 }],
      ]),
    );
  });
});

describe('ungroup', () => {
  // doc() without e_vpc, so c_vpc has no connections of its own.
  const unconnected = (): Document => ({ ...doc(), edges: doc().edges.filter((edge) => edge.id !== 'e_vpc') });
  const parentsOf = (plan: ReturnType<typeof ungroup>) => plan?.parents;

  it('moves the children to the container’s parent, keeps their geometry and removes the container only', () => {
    const plan = ungroup(unconnected(), new Set(['c_vpc']));
    if (plan === undefined) throw new Error('not ungrouped');
    expect(plan.containers).toEqual(new Set(['c_vpc']));
    expect(plan.refused).toEqual([]);
    expect(plan.parents).toEqual(new Map([['n_a', null], ['n_b', null]]));
    const byId = new Map(plan.nodes.map((node) => [node.id, node]));
    expect(byId.has('c_vpc')).toBe(false);
    expect(byId.get('n_a')).toEqual({ id: 'n_a', kind: 'service', label: 'A', shape: 'rounded', geometry: { x: 100, y: 100, w: 50, h: 50 } });
    expect(byId.get('n_b')?.geometry).toEqual({ x: 200, y: 150, w: 40, h: 40 });
  });

  it('refuses, naming the container, when a selected container has an edge of its own', () => {
    const refusedOnly = (document: Document) => ({ containers: new Set(), parents: new Map(), nodes: document.nodes, refused: ['c_vpc'] });
    expect(ungroup(doc(), new Set(['c_vpc']))).toEqual(refusedOnly(doc()));
    const reversed: Document = { ...doc(), edges: [{ id: 'e_in', kind: 'data', from: { node: 'n_c' }, to: { node: 'c_vpc' } }] };
    expect(ungroup(reversed, new Set(['c_vpc']))).toEqual(refusedOnly(reversed));
    // An edge between the children is not the container's own.
    expect(ungroup(unconnected(), new Set(['c_vpc']))?.refused).toEqual([]);
  });

  it('ungroups the unconnected containers of a mixed selection and leaves the connected one as it is', () => {
    const nested = group(doc(), new Set(['n_a', 'n_b']), 40, () => 'c_inner');
    if (!nested) throw new Error('no group');
    const plan = ungroup({ ...doc(), nodes: nested.nodes }, new Set(['c_inner', 'c_vpc']));
    if (plan === undefined) throw new Error('not ungrouped');
    expect(plan.refused).toEqual(['c_vpc']);
    expect(plan.containers).toEqual(new Set(['c_inner']));
    expect(plan.parents).toEqual(new Map([['n_a', 'c_vpc'], ['n_b', 'c_vpc']]));
    expect(plan.nodes.find((node) => node.id === 'c_vpc')).toEqual(doc().nodes[0]);
  });

  it('draws the released children where their container was, in their own order', () => {
    const plan = ungroup(unconnected(), new Set(['c_vpc']));
    expect(plan && 'nodes' in plan && ids(plan)).toEqual(['n_a', 'n_b', 'n_x', 'n_c', 'n_free']);
  });

  it('releases to the nearest ancestor that stays, when nested containers are ungrouped together', () => {
    const nested = group(doc(), new Set(['n_a', 'n_b']), 40, () => 'c_inner');
    if (!nested) throw new Error('no group');
    const grouped: Document = { ...unconnected(), nodes: nested.nodes };
    expect(parentsOf(ungroup(grouped, new Set(['c_inner'])))).toEqual(new Map([['n_a', 'c_vpc'], ['n_b', 'c_vpc']]));
    const both = ungroup(grouped, new Set(['c_inner', 'c_vpc']));
    if (both === undefined) throw new Error('not ungrouped');
    expect(both.parents).toEqual(new Map([['n_a', null], ['n_b', null]]));
    expect(ids(both)).toEqual(['n_x', 'n_a', 'n_b', 'n_c', 'n_free']);
  });

  it('ungroups only container-kind nodes, never a node of another kind that holds children', () => {
    const holder: Document = { ...doc(), nodes: doc().nodes.map((node) => (node.id === 'c_vpc' ? { ...node, kind: 'service' } : node)) };
    expect(ungroup(holder, new Set(['c_vpc']))).toBeUndefined();
    expect(ungroup(doc(), new Set(['n_a', 'e_ab']))).toBeUndefined();
  });

  it('releases a group’s children to the group’s own parent, not to where each came from', () => {
    const plan = group(doc(), new Set(['n_a', 'n_c']), 40, () => 'c_new');
    if (!plan) throw new Error('no group');
    const back = ungroup({ ...unconnected(), nodes: plan.nodes }, new Set(['c_new']));
    // n_a came from c_vpc, but the group sat at the top level, so ungrouping releases both there.
    expect(parentsOf(back)).toEqual(new Map([['n_a', null], ['n_c', null]]));
  });
});

describe('fitContainers', () => {
  it('fits a container around its children with the padding on all four sides', () => {
    const fitted = fitContainers(doc(), new Set(['c_vpc']), 40);
    const box = fitted.get('c_vpc');
    expect(box).toEqual({ x: 60, y: 60, w: 220, h: 170 });
    if (!box) throw new Error('not fitted');
    // Every side: the gap between the container's edge and the nearest child edge.
    expect([100 - box.x, 100 - box.y, box.x + box.w - 240, box.y + box.h - 190]).toEqual([40, 40, 40, 40]);
  });

  it('fits a selected inner container first, so the outer one fits its new box', () => {
    const inner = group(doc(), new Set(['n_a', 'n_b']), 5, () => 'c_inner');
    if (!inner) throw new Error('no group');
    const grouped: Document = { ...doc(), nodes: inner.nodes };
    // c_inner is 95,95 150x100 with padding 5; refitting it with 10 makes it 90,90 160x110, and c_vpc wraps that.
    const fitted = fitContainers(grouped, new Set(['c_vpc', 'c_inner']), 10);
    expect(fitted.get('c_inner')).toEqual({ x: 90, y: 90, w: 160, h: 110 });
    expect(fitted.get('c_vpc')).toEqual({ x: 80, y: 80, w: 180, h: 130 });
  });

  it('leaves out nodes with nothing inside and containers that already fit', () => {
    expect(fitContainers(doc(), new Set(['n_a', 'n_c']), 40).size).toBe(0);
    const once = fitContainers(doc(), new Set(['c_vpc']), 40);
    const fittedDoc: Document = { ...doc(), nodes: doc().nodes.map((node) => { const geometry = once.get(node.id); return geometry ? { ...node, geometry } : node; }) };
    expect(fitContainers(fittedDoc, new Set(['c_vpc']), 40).size).toBe(0);
  });
});

describe('canFitContents', () => {
  it('is true only when a selected node holds a placed child', () => {
    expect(canFitContents(doc(), new Set(['c_vpc']))).toBe(true);
    expect(canFitContents(doc(), new Set(['n_a', 'n_c']))).toBe(false);
  });
});
