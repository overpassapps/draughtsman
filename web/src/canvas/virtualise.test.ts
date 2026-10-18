import type { Document, Node } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { LABEL_FONT_SIZE } from './text.ts';
import { expandViewport, FULL_DETAIL_ZOOM, fullDetail, labelsLegible, MAX_REGION_GROWTH, MIN_LABEL_DEVICE_PX, stableVisibleSet, VIEWPORT_MARGIN, visibleElements, visibleSetMemo, type VisibleSet } from './virtualise.ts';

function node(id: string, x: number, y: number, w = 100, h = 50, extra: Partial<Node> = {}): Node {
  return { id, kind: 'service', label: id, shape: 'rectangle', geometry: { x, y, w, h }, ...extra };
}

function doc(nodes: Node[], edges: Document['edges'] = []): Document {
  return {
    id: 'doc',
    schemaVersion: 1,
    title: 'test',
    type: 'architecture',
    nodes,
    edges,
    meta: { createdAt: '2026-09-21T00:00:00.000Z', updatedAt: '2026-09-21T00:00:00.000Z', createdBy: 'test' },
  };
}

describe('expandViewport', () => {
  it('grows the rectangle by the margin fraction on every side', () => {
    expect(expandViewport({ x: 0, y: 0, w: 100, h: 50 }, 0.2)).toEqual({ x: -20, y: -10, w: 140, h: 70 });
  });
});

describe('visibleElements', () => {
  const view = { x: 0, y: 0, w: 1000, h: 500 };

  it('mounts nodes inside the view and drops nodes far outside it', () => {
    const index = indexDocument(doc([node('in', 10, 10), node('far', 5000, 5000)]));
    const visible = visibleElements(index, view);
    expect([...visible.nodes]).toEqual(['in']);
  });

  it('keeps nodes within the 20% margin but not beyond it', () => {
    // Right edge of the view is x=1000; the margin reaches x=1200.
    const index = indexDocument(doc([node('margin', 1150, 10), node('beyond', 1250, 10), node('partial', 950, 10)]));
    const visible = visibleElements(index, view);
    expect([...visible.nodes].sort()).toEqual(['margin', 'partial']);
  });

  it('uses rotated bounds so a spun node on the edge still mounts', () => {
    // Unrotated it spans x 1210..1220 (outside the margin); rotated 90 degrees about its centre it spans 1165..1265.
    const index = indexDocument(doc([node('spun', 1210, 100, 10, 100, { geometry: { x: 1210, y: 100, w: 10, h: 100, rotation: 90 } })]));
    expect(visibleElements(index, view).nodes.has('spun')).toBe(true);
  });

  it('clips children to their container so a child hanging outside an off-screen container is not mounted', () => {
    const index = indexDocument(
      doc([
        node('box', 2000, 2000, 300, 300, { kind: 'container' }),
        node('child', 2010, 2010, 50, 50, { parent: 'box' }),
        node('escapee', 10, 10, 50, 50, { parent: 'box' }),
      ]),
    );
    const visible = visibleElements(index, view);
    expect(visible.nodes.has('child')).toBe(false);
    expect(visible.nodes.has('escapee')).toBe(false);
    expect(visibleElements(index, { x: 1900, y: 1900, w: 400, h: 400 }).nodes.has('child')).toBe(true);
  });

  it('leaves a child clipped entirely away, and any edge to it, out of the content bounds', () => {
    const index = indexDocument(
      doc(
        [node('box', 0, 0, 300, 300, { kind: 'container' }), node('beyond', 5000, 5000, 50, 50, { parent: 'box' })],
        [{ id: 'e', kind: 'flow', from: { node: 'box' }, to: { node: 'beyond' } }],
      ),
    );
    expect(visibleElements(index, view).nodes.has('beyond')).toBe(false);
    // The edge would run to a corner where nothing is drawn, and fit-to-screen works from contentBounds, so a
    // node nobody can see must not widen it through its edges either.
    expect(index.edgePaths.has('e')).toBe(false);
    expect(index.contentBounds).toEqual({ x: 0, y: 0, w: 300, h: 300 });
  });

  it('mounts an edge whose line crosses the view even when both ends are off screen', () => {
    const index = indexDocument(
      doc([node('a', -600, 200), node('b', 1600, 200)], [{ id: 'e', kind: 'flow', from: { node: 'a' }, to: { node: 'b' } }]),
    );
    const visible = visibleElements(index, view);
    expect(visible.nodes.size).toBe(0);
    expect(visible.edges.has('e')).toBe(true);
  });

  it('never mounts an edge with a missing endpoint', () => {
    const index = indexDocument(doc([node('a', 0, 0)], [{ id: 'e', kind: 'flow', from: { node: 'a' }, to: { node: 'ghost' } }]));
    expect(index.edgePaths.has('e')).toBe(false);
    expect(visibleElements(index, view).edges.size).toBe(0);
  });

  it('is a pure function of the index and the view', () => {
    const index = indexDocument(doc([node('a', 0, 0)]));
    const first = visibleElements(index, view);
    const second = visibleElements(index, view);
    expect([...first.nodes]).toEqual([...second.nodes]);
  });
});

describe('indexDocument', () => {
  it('routes edges from boundary to boundary without ports and exactly to ports with them', () => {
    const index = indexDocument(
      doc(
        [node('a', 0, 0, 100, 50), node('b', 300, 0, 100, 50, { ports: [{ id: 'left', x: 0, y: 0.5 }] })],
        [
          { id: 'plain', kind: 'flow', from: { node: 'a' }, to: { node: 'b' } },
          { id: 'ported', kind: 'flow', from: { node: 'a' }, to: { node: 'b', port: 'left' } },
        ],
      ),
    );
    expect(index.edgePaths.get('plain')?.points).toEqual([
      { x: 100, y: 25 },
      { x: 300, y: 25 },
    ]);
    expect(index.edgePaths.get('ported')?.points[1]).toEqual({ x: 300, y: 25 });
  });

  it('threads waypoints between the endpoints', () => {
    const index = indexDocument(
      doc(
        [node('a', 0, 0, 100, 50), node('b', 0, 300, 100, 50)],
        [
          {
            id: 'e',
            kind: 'flow',
            from: { node: 'a' },
            to: { node: 'b' },
            routing: {
              mode: 'orthogonal',
              waypoints: [
                { x: 200, y: 25 },
                { x: 200, y: 325 },
              ],
            },
          },
        ],
      ),
    );
    expect(index.edgePaths.get('e')?.points).toEqual([
      { x: 100, y: 25 },
      { x: 200, y: 25 },
      { x: 200, y: 325 },
      { x: 100, y: 325 },
    ]);
  });

  it('treats a dangling or cyclic parent as no parent so every node still renders', () => {
    const index = indexDocument(
      doc([node('a', 0, 0, 100, 50, { parent: 'b' }), node('b', 0, 0, 100, 50, { parent: 'a' }), node('c', 0, 0, 100, 50, { parent: 'nope' })]),
    );
    expect(index.rootNodes.map((n) => n.id)).toEqual(['a', 'b', 'c']);
    expect(index.childrenOf.size).toBe(0);
  });

  it('reports content bounds over nodes and edges', () => {
    const index = indexDocument(doc([node('a', 10, 20, 100, 50), node('b', 400, 20, 100, 50)]));
    expect(index.contentBounds).toEqual({ x: 10, y: 20, w: 490, h: 50 });
    expect(indexDocument(doc([])).contentBounds).toBeUndefined();
  });

  it('gives a node with no position a provisional one, marks it, and keeps fit-to-screen working', () => {
    // A Mermaid import before layout: nothing has a position, so without this the canvas is blank and Fit does
    // nothing because the content bounds are undefined.
    const index = indexDocument(doc([{ id: 'a', kind: 'service', label: 'a', shape: 'rectangle' }, { id: 'b', kind: 'service', label: 'b', shape: 'rectangle' }]));
    expect([...index.unplaced]).toEqual(['a', 'b']);
    expect(index.rootNodes.map((n) => n.id)).toEqual(['a', 'b']);
    expect(index.contentBounds).toBeDefined();
    expect(index.contentBounds?.w).toBeGreaterThan(0);
    expect(index.contentBounds?.h).toBeGreaterThan(0);
    // The provisional geometry is deterministic but does not leak into the document's own nodes.
    expect(index.document.nodes.map((n) => n.geometry)).toEqual([undefined, undefined]);
  });
});

describe('labelsLegible', () => {
  const threshold = MIN_LABEL_DEVICE_PX / LABEL_FONT_SIZE;

  it('draws labels from the zoom at which they reach the minimum device size upwards', () => {
    expect(labelsLegible(threshold)).toBe(true);
    expect(labelsLegible(1)).toBe(true);
    expect(labelsLegible(4)).toBe(true);
  });

  it('drops labels once they would be drawn smaller than the minimum device size', () => {
    expect(labelsLegible(threshold * 0.99)).toBe(false);
    expect(labelsLegible(0.1)).toBe(false);
  });

  it('keeps labels further out on a denser screen, and drops them at the same device size', () => {
    expect(labelsLegible(threshold * 0.6, 2)).toBe(true);
    expect(labelsLegible(threshold * 0.6, 1)).toBe(false);
    expect(labelsLegible(threshold / 2, 2)).toBe(true);
    expect(labelsLegible((threshold / 2) * 0.99, 2)).toBe(false);
  });
});

describe('fullDetail', () => {
  it('draws full detail from FULL_DETAIL_ZOOM upwards and simplifies below it', () => {
    expect(fullDetail(FULL_DETAIL_ZOOM)).toBe(true);
    expect(fullDetail(1)).toBe(true);
    expect(fullDetail(FULL_DETAIL_ZOOM * 0.99)).toBe(false);
    expect(fullDetail(0.05)).toBe(false);
  });

  it('switches below where labels are already gone at a pixel ratio of 1', () => {
    expect(labelsLegible(FULL_DETAIL_ZOOM, 1)).toBe(false);
  });
});

describe('stableVisibleSet', () => {
  const set = (nodes: string[], edges: string[] = []): VisibleSet => ({ nodes: new Set(nodes), edges: new Set(edges) });

  it('hands back the previous set when the next holds exactly the same ids, whatever their order', () => {
    const previous = set(['a', 'b'], ['e']);
    expect(stableVisibleSet(previous, set(['b', 'a'], ['e']))).toBe(previous);
  });

  it('hands back the next set when any node or edge is added, removed or swapped', () => {
    const previous = set(['a', 'b'], ['e']);
    for (const next of [set(['a', 'b', 'c'], ['e']), set(['a'], ['e']), set(['a', 'c'], ['e']), set(['a', 'b'], []), set(['a', 'b'], ['f'])]) {
      expect(stableVisibleSet(previous, next)).toBe(next);
    }
    const first = set(['a']);
    expect(stableVisibleSet(undefined, first)).toBe(first);
  });
});

describe('visibleSetMemo', () => {
  // A 20 x 20 grid of 100 x 50 nodes on a 200 x 100 pitch, 4,000 x 2,000 world units, with the 1,000 x 500 view.
  const grid = indexDocument(doc(Array.from({ length: 400 }, (_, i) => node(`n${String(i)}`, (i % 20) * 200, Math.floor(i / 20) * 100))));
  const view = { x: 1000, y: 500, w: 1000, h: 500 };
  const ids = (visible: VisibleSet): string[] => [...visible.nodes].sort();
  /** The view scaled about its centre, as a zoom by `1 / factor` would leave it. */
  const scaled = (factor: number) => ({ x: view.x + (view.w * (1 - factor)) / 2, y: view.y + (view.h * (1 - factor)) / 2, w: view.w * factor, h: view.h * factor });

  it('hands back the same set while a pan stays inside the margin it was computed with', () => {
    const visibleIn = visibleSetMemo();
    const first = visibleIn(grid, view);
    const panned = { ...view, x: view.x + 150, y: view.y - 80 };
    // A fresh computation would mount different nodes; the remembered set still covers the whole view.
    expect(ids(visibleElements(grid, panned))).not.toEqual(ids(first));
    expect(visibleIn(grid, panned)).toBe(first);
    for (const id of visibleElements(grid, panned, 0).nodes) {
      expect(first.nodes.has(id)).toBe(true);
    }
  });

  it('computes a new set once a pan leaves the region, and mounts what came into view', () => {
    const visibleIn = visibleSetMemo();
    const first = visibleIn(grid, view);
    const panned = { ...view, x: view.x + 250 };
    const next = visibleIn(grid, panned);
    expect(next).not.toBe(first);
    expect(ids(next)).toEqual(ids(visibleElements(grid, panned)));
  });

  it('keeps the set through a small zoom in and drops the far margin after a large one', () => {
    const visibleIn = visibleSetMemo();
    const first = visibleIn(grid, view);
    expect(visibleIn(grid, scaled(1 / 1.2))).toBe(first);
    const close = visibleIn(grid, scaled(1 / 2));
    expect(close).not.toBe(first);
    expect(ids(close)).toEqual(ids(visibleElements(grid, scaled(1 / 2))));
    expect(close.nodes.size).toBeLessThan(first.nodes.size);
  });

  it('computes a new set as soon as a zoom out shows more than the region covered', () => {
    const visibleIn = visibleSetMemo();
    const first = visibleIn(grid, view);
    expect(visibleIn(grid, scaled(1.3))).toBe(first);
    const wide = visibleIn(grid, scaled(1.5));
    expect(wide).not.toBe(first);
    expect(ids(wide)).toEqual(ids(visibleElements(grid, scaled(1.5))));
  });

  it('recomputes for a new index or margin, keeping the identity only when the ids are unchanged', () => {
    const visibleIn = visibleSetMemo();
    const first = visibleIn(grid, view);
    const rebuilt = indexDocument(grid.document);
    expect(visibleIn(rebuilt, view)).toBe(first);
    const added = indexDocument({ ...grid.document, nodes: [...grid.document.nodes, node('new', view.x + 10, view.y + 10)] });
    const withNew = visibleIn(added, view);
    expect(withNew).not.toBe(first);
    expect(withNew.nodes.has('new')).toBe(true);
    expect(ids(visibleIn(added, view, 0))).toEqual(ids(visibleElements(added, view, 0)));
  });

  it('always mounts everything in view, and never more than the view grown by one view size, over a random walk', () => {
    const visibleIn = visibleSetMemo();
    let seed = 7;
    const random = (): number => {
      seed = (seed * 16807) % 2147483647;
      return seed / 2147483647;
    };
    let current = view;
    for (let step = 0; step < 300; step += 1) {
      const factor = random() < 0.5 ? 1 + (random() - 0.5) * 0.3 : 1;
      const w = Math.min(4000, Math.max(100, current.w * factor));
      const h = w / 2;
      current = { x: current.x + (current.w - w) / 2 + (random() - 0.5) * w * 0.3, y: current.y + (current.h - h) / 2 + (random() - 0.5) * h * 0.3, w, h };
      const mounted = visibleIn(grid, current);
      for (const id of visibleElements(grid, current, 0).nodes) {
        expect(mounted.nodes.has(id)).toBe(true);
      }
      // The region is at most MAX_REGION_GROWTH times the fresh one's area, with the view's aspect, and covers the view.
      const bound = visibleElements(grid, current, Math.sqrt(MAX_REGION_GROWTH) * (1 + 2 * VIEWPORT_MARGIN) - 1);
      for (const id of mounted.nodes) {
        expect(bound.nodes.has(id)).toBe(true);
      }
    }
  });
});
