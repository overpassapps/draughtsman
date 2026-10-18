import { describe, expect, it } from 'vitest';
import orderService from '../../../docs/examples/order-service.json';
import perfBenchmark from '../../../docs/examples/perf-2000n-2500e.json';
import { isDocument, parseDocument } from './parse.ts';
import { CURRENT_SCHEMA_VERSION, type Document } from './types.ts';

describe('document contract', () => {
  it.each(['document', 'predefined-process', 'manual-input', 'off-page-connector'])('accepts %s through JSON and rejects unknown shapes', (shape) => {
    const input = { ...orderService, nodes: [{ id: 'n_shape', kind: 'process', label: 'Shape', shape }], edges: [] };
    expect(parseDocument(JSON.parse(JSON.stringify(input))).nodes[0]?.shape).toBe(shape);
    expect(isDocument({ ...input, nodes: [{ ...input.nodes[0], shape: 'not-a-shape' }] })).toBe(false);
  });

  it('parses docs/examples/order-service.json against the schema and types', () => {
    const doc: Document = parseDocument(orderService);

    expect(doc.id).toBe('doc_order_service');
    expect(doc.schemaVersion).toBe(CURRENT_SCHEMA_VERSION);
    expect(doc.type).toBe('architecture');
    expect(doc.nodes).toHaveLength(5);
    expect(doc.edges).toHaveLength(3);

    const api = doc.nodes.find((n) => n.id === 'n_api');
    expect(api?.parent).toBe('c_vpc');
    expect(api?.props?.owner).toBe('platform');
    expect(api?.ports?.map((p) => p.id)).toEqual(['in', 'db', 'cache']);

    const e2 = doc.edges.find((e) => e.id === 'e_2');
    expect(e2?.style?.arrowStart).toBe('arrow');
    expect(e2?.routing?.mode).toBe('orthogonal');

    const e3 = doc.edges.find((e) => e.id === 'e_3');
    expect(e3?.label).toBeUndefined();
    expect(e3?.routing?.waypoints).toEqual([{ x: 480, y: 240 }]);

    expect(doc.meta.aiProvenance?.[0]?.elementIds).toEqual(['n_cache', 'e_3']);
  });

  it('accepts a document built from the TypeScript types', () => {
    // Compile-time check that the types are usable, run-time check that they agree with the schema.
    const built: Document = {
      id: 'doc_ts',
      schemaVersion: 1,
      title: 'Built in TypeScript',
      type: 'flowchart',
      theme: { 'node.fill': '#FFFFFF' },
      nodes: [
        { id: 'c_1', kind: 'container', label: 'Lane', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 200 } },
        {
          id: 'n_1',
          kind: 'process',
          label: 'Step',
          shape: 'rounded',
          geometry: { x: 10, y: 10, w: 100, h: 50, rotation: 0 },
          parent: 'c_1',
          style: { fill: '#FFF', dash: 'dashed', strokeWidth: 2 },
          ports: [{ id: 'out', x: 1, y: 0.5 }],
        },
        { id: 'n_2', kind: 'decision', label: 'Ok?', shape: 'diamond', geometry: { x: 200, y: 10, w: 100, h: 100 }, parent: 'c_1' },
      ],
      edges: [
        {
          id: 'e_1',
          kind: 'flow',
          label: 'next',
          from: { node: 'n_1', port: 'out' },
          to: { node: 'n_2' },
          routing: { mode: 'orthogonal', waypoints: [{ x: 150, y: 35 }] },
          style: { arrowEnd: 'arrow', dash: 'dotted' },
        },
      ],
      meta: {
        createdAt: '2026-09-20T22:00:00.000Z',
        updatedAt: '2026-09-20T22:00:00.000Z',
        createdBy: 'test',
        aiProvenance: [{ agent: 'claude-code', model: 'claude-fable-5-1', at: '2026-09-20T22:00:00.000Z', action: 'created', elementIds: ['n_1', 'e_1'] }],
      },
    };

    expect(isDocument(built)).toBe(true);
  });

  it('parses the committed synthetic benchmark at the scale of checklist item 29', () => {
    // docs/examples/perf-2000n-2500e.json is generated deterministically by web/scripts/generate-benchmark.ts at the
    // exact scale named by the M2 editor checklist, item 29, so the perf test has a schema-valid document to run
    // against rather than an unvalidated hand-built graph.
    const doc: Document = parseDocument(perfBenchmark);

    expect(doc.type).toBe('architecture');
    expect(doc.nodes).toHaveLength(2000);
    expect(doc.edges).toHaveLength(2500);
    // Every node the benchmark places has authored geometry, so none depends on provisional placement.
    expect(doc.nodes.every((n) => n.geometry !== undefined)).toBe(true);
  });

  it('rejects a document with an unknown top-level member or bad enum', () => {
    expect(isDocument({ ...orderService, picture: 'data:...' })).toBe(false);
    expect(isDocument({ ...orderService, type: 'mindmap' })).toBe(false);
    expect(() => parseDocument({})).toThrow(/Not a Draughtsman document/);
  });
});
