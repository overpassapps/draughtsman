import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import { must } from '../model/sequenceFixture.ts';
import type { Document, Node } from '../model/types.ts';
import { indexDocument } from './documentIndex.ts';
import { canGenerateFromContainers, mayHaveContainers, SHOT_PADDING, shotsFromContainers } from './flythroughGenerate.ts';

/** The example with its tour taken out, so the shots under test are the only ones. */
function withoutTour(document: Document): Document {
  const copy = structuredClone(document);
  Reflect.deleteProperty(copy, 'flythrough');
  return copy;
}

const node = (id: string, x: number, y: number, extra: Partial<Node> = {}): Node => ({ id, kind: 'service', label: id, shape: 'rectangle', geometry: { x, y, w: 100, h: 50 }, ...extra });
const doc = (nodes: Node[]): Document => ({ id: 'd', schemaVersion: 1, title: 'T', type: 'architecture', nodes, edges: [], meta: { createdAt: '2026-09-30T00:00:00.000Z', updatedAt: '2026-09-30T00:00:00.000Z', createdBy: 't' } });

describe('shotsFromContainers', () => {
  it('walks the example: an overview, then its one container, then the node outside it', () => {
    const rest = withoutTour(parseDocument(JSON.parse(JSON.stringify(orderService))));

    const shots = shotsFromContainers(indexDocument(rest));

    expect(shots.map((s) => s.caption)).toEqual(['Overview', 'VPC', 'Everything else']);
    // The VPC is 568x300 at 16,168: the shot is that box with the padding on every side.
    expect(shots[1]?.view).toEqual({ x: 16 - SHOT_PADDING, y: 168 - SHOT_PADDING, w: 568 + 2 * SHOT_PADDING, h: 300 + 2 * SHOT_PADDING });
    // The node outside is the web app at 40,40 320x80.
    expect(shots[2]?.view).toEqual({ x: 40 - SHOT_PADDING, y: 40 - SHOT_PADDING, w: 320 + 2 * SHOT_PADDING, h: 80 + 2 * SHOT_PADDING });
  });

  it('the overview frames everything, so every other shot sits inside it', () => {
    const shots = shotsFromContainers(indexDocument(doc([node('c', 0, 0, { kind: 'container', geometry: { x: 0, y: 0, w: 400, h: 300 } }), node('a', 20, 20, { parent: 'c' }), node('z', 900, 700)])));
    const [first, ...rest] = shots;
    const overview = must(first);

    expect(overview.caption).toBe('Overview');
    expect(rest.length).toBeGreaterThan(0);
    for (const other of rest) {
      expect(other.view.x).toBeGreaterThanOrEqual(overview.view.x - 1e-9);
      expect(other.view.y).toBeGreaterThanOrEqual(overview.view.y - 1e-9);
      expect(other.view.x + other.view.w).toBeLessThanOrEqual(overview.view.x + overview.view.w + 1e-9);
      expect(other.view.y + other.view.h).toBeLessThanOrEqual(overview.view.y + overview.view.h + 1e-9);
    }
  });

  it('takes containers in document order, and a nested one is inside its parent’s shot, not a shot of its own', () => {
    const shots = shotsFromContainers(
      indexDocument(
        doc([
          node('c_b', 500, 0, { kind: 'container', label: 'Second', geometry: { x: 500, y: 0, w: 300, h: 200 } }),
          node('c_a', 0, 0, { kind: 'container', label: 'First', geometry: { x: 0, y: 0, w: 300, h: 200 } }),
          node('c_inner', 20, 20, { kind: 'container', label: 'Inner', parent: 'c_a', geometry: { x: 20, y: 20, w: 100, h: 100 } }),
        ]),
      ),
    );

    expect(shots.map((s) => s.caption)).toEqual(['Overview', 'Second', 'First']);
  });

  it('leaves out “Everything else” when every top-level node is a container', () => {
    const shots = shotsFromContainers(indexDocument(doc([node('c', 0, 0, { kind: 'container', geometry: { x: 0, y: 0, w: 400, h: 300 } }), node('a', 20, 20, { parent: 'c' })])));

    expect(shots.map((s) => s.caption)).toEqual(['Overview', 'c']);
  });

  it('names an unlabelled container “Group” and trims a padded label', () => {
    const shots = shotsFromContainers(
      indexDocument(doc([node('c1', 0, 0, { kind: 'container', label: '', geometry: { x: 0, y: 0, w: 100, h: 100 } }), node('c2', 300, 0, { kind: 'container', label: '  Edge  ', geometry: { x: 300, y: 0, w: 100, h: 100 } })])),
    );

    expect(shots.map((s) => s.caption)).toEqual(['Overview', 'Group', 'Edge']);
  });

  it('generates nothing for a diagram with no container, however many nodes it has', () => {
    const flat = indexDocument(doc([node('a', 0, 0), node('b', 300, 0), node('c', 600, 0)]));

    expect(shotsFromContainers(flat)).toEqual([]);
    expect(canGenerateFromContainers(flat)).toBe(false);
  });

  it('generates nothing for an empty diagram, and counts a node with children as a container', () => {
    expect(shotsFromContainers(indexDocument(doc([])))).toEqual([]);
    const parentWithChild = indexDocument(doc([node('p', 0, 0, { geometry: { x: 0, y: 0, w: 300, h: 200 } }), node('k', 20, 20, { parent: 'p' })]));

    expect(canGenerateFromContainers(parentWithChild)).toBe(true);
  });

  it('every generated view is one a camera can frame', () => {
    const rest = withoutTour(parseDocument(JSON.parse(JSON.stringify(orderService))));

    for (const { view } of shotsFromContainers(indexDocument(rest))) {
      expect(view.w).toBeGreaterThan(0);
      expect(view.h).toBeGreaterThan(0);
    }
  });
});

describe('mayHaveContainers', () => {
  it('is true for a container kind, a swimlane, a composite state, or any node with a parent, and false for a flat diagram', () => {
    expect(mayHaveContainers(doc([node('c', 0, 0, { kind: 'container' })]))).toBe(true);
    expect(mayHaveContainers(doc([node('p', 0, 0, { kind: 'pool' })]))).toBe(true);
    expect(mayHaveContainers(doc([node('s', 0, 0, { kind: 'composite-state' })]))).toBe(true);
    expect(mayHaveContainers(doc([node('a', 0, 0), node('b', 200, 0, { parent: 'a' })]))).toBe(true);
    expect(mayHaveContainers(doc([node('a', 0, 0), node('b', 200, 0)]))).toBe(false);
    expect(mayHaveContainers(doc([]))).toBe(false);
  });

  it('never says no when generation would say yes', () => {
    const cases = [
      doc([node('c', 0, 0, { kind: 'container', geometry: { x: 0, y: 0, w: 400, h: 300 } }), node('a', 20, 20, { parent: 'c' })]),
      doc([node('p', 0, 0, { geometry: { x: 0, y: 0, w: 300, h: 200 } }), node('k', 20, 20, { parent: 'p' })]),
      doc([node('a', 0, 0), node('b', 300, 0)]),
    ];

    for (const candidate of cases) {
      if (canGenerateFromContainers(indexDocument(candidate))) {
        expect(mayHaveContainers(candidate)).toBe(true);
      }
    }
  });
});
