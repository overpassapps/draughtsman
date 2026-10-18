import { indexDocument } from '../canvas/documentIndex.ts';
import { routeDocumentEdges } from '../canvas/routeDocument.ts';
import { createSamples, sampleDocuments } from './samples.ts';

describe('the opt-in sample documents', () => {
  it('are one flowchart, one architecture, one sequence and one ER diagram, each a valid document', () => {
    const samples = sampleDocuments();

    expect(samples.map((sample) => sample.type)).toEqual(['flowchart', 'architecture', 'sequence', 'architecture']);
    expect(samples.map((sample) => sample.title)).toEqual(['Sample: Order approval', 'Sample: Order service', 'Sample: Checkout', 'Sample: Shop schema']);
    // The ER one is a table diagram: every node is an ER entity (a link table is a weak entity), and foreign keys are drawn as edges.
    const er = samples[3];
    expect(er?.nodes.every((node) => node.shape.startsWith('custom:er/'))).toBe(true);
    expect(er?.edges.length).toBeGreaterThan(0);
    // The sequence one has its participants and messages; the flowchart has a decision.
    expect(samples[2]?.edges.length).toBeGreaterThan(3);
    expect(samples[0]?.nodes.some((node) => node.kind === 'decision')).toBe(true);
  });

  // S7: the first sample a new user opens was drawn with connectors fanning out of the diamond and crossing, one through
  // its bottom vertex. The flowchart sample is routed here the way the canvas routes it and must come out square and clear.
  it('draws the flowchart sample with every connector routed square, off every other shape and clear of every other connector', () => {
    const flowchart = sampleDocuments()[0];
    if (flowchart === undefined) {
      throw new Error('no flowchart sample');
    }
    expect(flowchart.edges.every((edge) => edge.from.port !== undefined && edge.to.port !== undefined && edge.routing?.waypoints === undefined)).toBe(true);
    const index = indexDocument(flowchart);
    const routes = routeDocumentEdges(index);
    expect(routes.size).toBe(flowchart.edges.length);
    const segments = new Map<string, [{ x: number; y: number }, { x: number; y: number }][]>();
    for (const edge of flowchart.edges) {
      const route = routes.get(edge.id);
      expect(route?.status, edge.id).toBe('routed');
      const points = route?.points ?? [];
      const own: [{ x: number; y: number }, { x: number; y: number }][] = [];
      points.forEach((point, i) => {
        const next = points[i + 1];
        if (next === undefined) {
          return;
        }
        // Square: each segment is horizontal or vertical.
        expect(point.x === next.x || point.y === next.y, `${edge.id} segment ${String(i)}`).toBe(true);
        own.push([point, next]);
        // Off every shape but its own two ends (a shape's interior, shrunk a pixel so touching the outline is allowed).
        for (const node of flowchart.nodes) {
          if (node.id === edge.from.node || node.id === edge.to.node || node.geometry === undefined) {
            continue;
          }
          const { x, y, w, h } = node.geometry;
          const crosses = Math.max(point.x, next.x) > x + 1 && Math.min(point.x, next.x) < x + w - 1 && Math.max(point.y, next.y) > y + 1 && Math.min(point.y, next.y) < y + h - 1;
          expect(crosses, `${edge.id} crosses ${node.id}`).toBe(false);
        }
      });
      segments.set(edge.id, own);
    }
    // No two connectors cross (a shared end at a port is a touch, not a crossing: segments are compared properly).
    const ids = [...segments.keys()];
    for (let a = 0; a < ids.length; a++) {
      for (let b = a + 1; b < ids.length; b++) {
        for (const [p1, p2] of segments.get(ids[a] ?? '') ?? []) {
          for (const [q1, q2] of segments.get(ids[b] ?? '') ?? []) {
            const horizontal = p1.y === p2.y;
            if (horizontal === (q1.y === q2.y)) {
              continue;
            }
            const [h1, h2, v1, v2] = horizontal ? [p1, p2, q1, q2] : [q1, q2, p1, p2];
            const crossing = v1.x > Math.min(h1.x, h2.x) && v1.x < Math.max(h1.x, h2.x) && h1.y > Math.min(v1.y, v2.y) && h1.y < Math.max(v1.y, v2.y);
            expect(crossing, `${ids[a] ?? ''} crosses ${ids[b] ?? ''}`).toBe(false);
          }
        }
      }
    }
  });

  it('creates each as its own document, without the id or meta of the example it came from, in list order', async () => {
    const bodies: Record<string, unknown>[] = [];
    const fetchImpl = vi.fn<typeof fetch>((_url, init) => {
      bodies.push(JSON.parse(init?.body as string) as Record<string, unknown>);
      return Promise.resolve(new Response(JSON.stringify({ id: `doc_new_${String(bodies.length)}` }), { status: 201 }));
    });

    const ids = await createSamples(fetchImpl);

    expect(ids).toEqual(['doc_new_1', 'doc_new_2', 'doc_new_3', 'doc_new_4']);
    expect(bodies.map((body) => body.title)).toEqual(['Sample: Order approval', 'Sample: Order service', 'Sample: Checkout', 'Sample: Shop schema']);
    for (const body of bodies) {
      expect(body).not.toHaveProperty('id');
      expect(body).not.toHaveProperty('meta');
    }
  });

  it('stops at the first refusal rather than pretending all four exist', async () => {
    let calls = 0;
    const fetchImpl = vi.fn<typeof fetch>(() => {
      calls += 1;
      return Promise.resolve(calls === 2 ? new Response('{}', { status: 500 }) : new Response('{"id":"doc_x"}', { status: 201 }));
    });

    await expect(createSamples(fetchImpl)).rejects.toThrow(/500/);
    expect(calls).toBe(2);
  });
});
