import { parseDocument } from '../model/parse.ts';
import type { Edge, Node } from '../model/types.ts';
import { indexDocument, type PlacedNode } from './documentIndex.ts';
import { attachmentHit, connectablePorts, DEFAULT_PORTS, nearestPort, nodeAt, nudgedPortPoint, offsetAlongSide, pairPorts, portAt, portFacing, portPosition, portSide, sharedPortOffsets } from './ports.ts';

const box = { x: 100, y: 50, w: 200, h: 100 };

function placed(node: Partial<Node> = {}): PlacedNode {
  return { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: box, ...node };
}

describe('connectablePorts', () => {
  it('offers the four side midpoints when a node declares no ports, and only its own when it declares some', () => {
    expect(connectablePorts(placed()).map((port) => port.id)).toEqual(['top', 'right', 'bottom', 'left']);
    expect(connectablePorts(placed({ ports: [] })).map((port) => port.id)).toEqual(['top', 'right', 'bottom', 'left']);
    expect(connectablePorts(placed({ ports: [{ id: 'in', x: 0.25, y: 0 }] })).map((port) => port.id)).toEqual(['in']);
  });

  it('puts the default ports on the midpoints of the sides', () => {
    expect(DEFAULT_PORTS.map((port) => portPosition(box, port))).toEqual([
      { x: 200, y: 50 },
      { x: 300, y: 100 },
      { x: 200, y: 150 },
      { x: 100, y: 100 },
    ]);
  });
});

describe('portSide', () => {
  it('leaves by the nearest side of the box', () => {
    expect(portSide(box, { x: 0.3, y: 0 })).toBe('top');
    expect(portSide(box, { x: 1, y: 0.7 })).toBe('right');
    expect(portSide(box, { x: 0.3, y: 0.9 })).toBe('bottom');
    expect(portSide(box, { x: 0.1, y: 0.4 })).toBe('left');
  });

  it('breaks a tie the same way every time: top, then right, bottom, left', () => {
    expect(portSide(box, { x: 1, y: 0 })).toBe('top');
    expect(portSide(box, { x: 1, y: 1 })).toBe('right');
    expect(portSide(box, { x: 0, y: 1 })).toBe('bottom');
    expect(portSide(box, { x: 0.5, y: 0.5 })).toBe('top');
  });
});

describe('offsetAlongSide', () => {
  it('nudges along x for top/bottom and along y for left/right, leaving 0 untouched', () => {
    const point = { x: 200, y: 50 };
    expect(offsetAlongSide(point, 'top', 6)).toEqual({ x: 206, y: 50 });
    expect(offsetAlongSide(point, 'bottom', -6)).toEqual({ x: 194, y: 50 });
    expect(offsetAlongSide(point, 'left', 6)).toEqual({ x: 200, y: 56 });
    expect(offsetAlongSide(point, 'right', -6)).toEqual({ x: 200, y: 44 });
    expect(offsetAlongSide(point, 'top', 0)).toEqual(point);
  });
});

describe('sharedPortOffsets (M2 final Finding D)', () => {
  const edge = (id: string, fromNode: string, fromPort: string | undefined, toNode: string, toPort: string | undefined): Edge => ({
    id,
    kind: 'flow',
    from: { node: fromNode, ...(fromPort === undefined ? {} : { port: fromPort }) },
    to: { node: toNode, ...(toPort === undefined ? {} : { port: toPort }) },
  });

  it('leaves a port used by only one edge unoffset (absent from the map)', () => {
    const offsets = sharedPortOffsets([edge('e_1', 'n_a', 'out', 'n_b', undefined)]);
    expect(offsets.has('e_1:from')).toBe(false);
  });

  it('leaves the first edge to declare a shared port at 0 and fans the rest out both ways, growing', () => {
    const offsets = sharedPortOffsets([
      edge('e_1', 'n_a', 'out', 'n_b', undefined),
      edge('e_2', 'n_a', 'out', 'n_c', undefined),
      edge('e_3', 'n_a', 'out', 'n_d', undefined),
      edge('e_4', 'n_a', 'out', 'n_e', undefined),
    ]);
    expect(offsets.get('e_1:from')).toBeUndefined();
    expect(offsets.get('e_2:from')).toBe(16);
    expect(offsets.get('e_3:from')).toBe(-16);
    expect(offsets.get('e_4:from')).toBe(32);
  });

  it('groups a from-endpoint and a to-endpoint together when they name the same node and port', () => {
    const offsets = sharedPortOffsets([edge('e_1', 'n_a', 'in', 'n_b', undefined), edge('e_2', 'n_c', undefined, 'n_a', 'in')]);
    expect(offsets.get('e_1:from')).toBeUndefined();
    expect(offsets.get('e_2:to')).toBe(16);
  });

  it('never offsets a self-loop, whose exact port coincidence documentIndex.ts depends on', () => {
    const offsets = sharedPortOffsets([edge('e_1', 'n_a', 'p', 'n_a', 'p'), edge('e_2', 'n_a', 'p', 'n_b', undefined)]);
    expect(offsets.size).toBe(0);
  });

  it('keeps separate (node, port) pairs in separate groups', () => {
    const offsets = sharedPortOffsets([
      edge('e_1', 'n_a', 'out', 'n_b', undefined),
      edge('e_2', 'n_a', 'out', 'n_c', undefined),
      edge('e_3', 'n_x', 'out', 'n_y', undefined),
      edge('e_4', 'n_x', 'out', 'n_z', undefined),
    ]);
    expect(offsets.get('e_2:from')).toBe(16);
    expect(offsets.get('e_4:from')).toBe(16);
  });
});

describe('ports on a rotated node', () => {
  const upright = { x: 0, y: 0, w: 200, h: 100 };
  const turned = { ...upright, rotation: 90 };

  it('puts the right port of a node turned 90 degrees on the drawn shape, leaving by the bottom', () => {
    const right = { x: 1, y: 0.5 };
    expect(portPosition(turned, right)).toEqual({ x: 100, y: 150 });
    expect(portSide(turned, right)).toBe('bottom');
  });

  it('turns every default port with the shape, onto the sides of its drawn box', () => {
    expect(DEFAULT_PORTS.map((port) => portPosition(turned, port))).toEqual([
      { x: 150, y: 50 },
      { x: 100, y: 150 },
      { x: 50, y: 50 },
      { x: 100, y: -50 },
    ]);
    expect(DEFAULT_PORTS.map((port) => portSide(turned, port))).toEqual(['right', 'bottom', 'left', 'top']);
  });

  it('gives the unrotated positions and sides at rotation 0, a full turn, or with no rotation', () => {
    for (const geometry of [upright, { ...upright, rotation: 0 }, { ...upright, rotation: 360 }]) {
      expect(DEFAULT_PORTS.map((port) => portPosition(geometry, port))).toEqual([
        { x: 100, y: 0 },
        { x: 200, y: 50 },
        { x: 100, y: 100 },
        { x: 0, y: 50 },
      ]);
      expect(DEFAULT_PORTS.map((port) => portSide(geometry, port))).toEqual(['top', 'right', 'bottom', 'left']);
    }
  });

  it('finds the port under the pointer, and the nearest one, where the rotated port is drawn', () => {
    const node = placed({ geometry: turned });
    expect(portAt(node, { x: 101, y: 149 }, 5)?.id).toBe('right');
    expect(portAt(node, { x: 200, y: 50 }, 5)).toBeUndefined();
    expect(nearestPort(node, { x: 100, y: 140 }).id).toBe('right');
  });
});

describe('nearestPort and portAt', () => {
  const node = placed();

  it('picks the port nearest the point, whichever side it is on', () => {
    expect(nearestPort(node, { x: 290, y: 90 }).id).toBe('right');
    expect(nearestPort(node, { x: 110, y: 110 }).id).toBe('left');
    expect(nearestPort(node, { x: 190, y: 140 }).id).toBe('bottom');
    expect(nearestPort(node, { x: 210, y: 55 }).id).toBe('top');
  });

  it('is on a port only within the radius, and off it just beyond', () => {
    expect(portAt(node, { x: 303, y: 104 }, 5)?.id).toBe('right');
    expect(portAt(node, { x: 306, y: 100 }, 5)).toBeUndefined();
    expect(portAt(node, { x: 200, y: 100 }, 5)).toBeUndefined();
  });
});

describe('nodeAt', () => {
  const index = indexDocument(
    parseDocument({
      id: 'doc_ports',
      schemaVersion: 1,
      title: 'Ports',
      type: 'architecture',
      nodes: [
        { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 300 } },
        { id: 'n_in', kind: 'service', label: 'In', shape: 'rectangle', geometry: { x: 100, y: 100, w: 100, h: 50 }, parent: 'c_box' },
        { id: 'n_out', kind: 'service', label: 'Out', shape: 'rectangle', geometry: { x: 600, y: 0, w: 100, h: 50 } },
      ],
      edges: [],
      meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
    }),
  );

  it('finds the child drawn over its container, and the container around it', () => {
    expect(nodeAt(index, { x: 150, y: 120 }, 0)?.id).toBe('n_in');
    expect(nodeAt(index, { x: 50, y: 250 }, 0)?.id).toBe('c_box');
  });

  it('counts a point just outside a node within the pad, and nothing beyond it', () => {
    expect(nodeAt(index, { x: 596, y: 25 }, 5)?.id).toBe('n_out');
    expect(nodeAt(index, { x: 594, y: 25 }, 5)).toBeUndefined();
    expect(nodeAt(index, { x: 500, y: 200 }, 5)).toBeUndefined();
  });
});

describe('fanned connectors stay apart and on their side (QA-07)', () => {
  const node = (w: number): PlacedNode => ({ id: 'n_a', kind: 'process', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w, h: 60 } });
  const bottom = { x: 0.5, y: 1 };

  it('puts three connectors sharing a port at least 12 units apart, so their arrowheads do not overlap', () => {
    const at = (offset: number): number => nudgedPortPoint(node(200), bottom, offset).x;
    const [low = 0, middle = 0, high = 0] = [at(0), at(16), at(-16)].sort((a, b) => a - b);
    expect(middle - low).toBeGreaterThanOrEqual(12);
    expect(high - middle).toBeGreaterThanOrEqual(12);
  });

  it('never slides a fanned connector off the end of a short side', () => {
    expect(nudgedPortPoint(node(20), bottom, 32).x).toBe(16);
    expect(nudgedPortPoint(node(20), bottom, -32).x).toBe(4);
    expect(nudgedPortPoint(node(200), bottom, 32).x).toBe(132);
  });
});

describe('attachmentHit and pairPorts (S1)', () => {
  const a = placed({ id: 'n_a', geometry: { x: 0, y: 0, w: 100, h: 50 } });
  const below = placed({ id: 'n_b', geometry: { x: 0, y: 200, w: 100, h: 50 } });
  const byId = (node: PlacedNode, id: string) => {
    const found = connectablePorts(node).find((port) => port.id === id);
    if (found === undefined) {
      throw new Error(`no ${id} port`);
    }
    return found;
  };

  it('pins a point inside a port\'s ring and leaves one on the body unpinned, with the nearest port as placeholder', () => {
    expect(attachmentHit(a, { x: 52, y: 4 }, 10)).toEqual({ port: byId(a, 'top'), pinned: true });
    expect(attachmentHit(a, { x: 60, y: 20 }, 10)).toEqual({ port: byId(a, 'top'), pinned: false });
  });

  it('picks the facing pair for two body ends and keeps a pinned end\'s port whichever is nearer', () => {
    const body = (node: PlacedNode) => ({ node, hit: { port: nearestPort(node, { x: 99, y: 25 }), pinned: false } });
    expect(pairPorts(body(a), body(below))).toEqual({ a: byId(a, 'bottom'), b: byId(below, 'top') });
    const pinnedRight = { node: a, hit: { port: byId(a, 'right'), pinned: true } };
    expect(pairPorts(pinnedRight, body(below)).a).toBe(byId(a, 'right'));
    expect(pairPorts(pinnedRight, body(below)).b).toBe(byId(below, 'top'));
    const pinnedBoth = { node: below, hit: { port: byId(below, 'left'), pinned: true } };
    expect(pairPorts(pinnedRight, pinnedBoth)).toEqual({ a: byId(a, 'right'), b: byId(below, 'left') });
  });

  it('faces a re-attached end towards the point that stays put', () => {
    expect(portFacing(below, { x: 50, y: 25 }).id).toBe('top');
    expect(portFacing(a, { x: 500, y: 25 }).id).toBe('right');
  });
});
