import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { layoutDocument } from '../src/layout/elk.ts';
import { UML_METRICS, UML_SYMBOL_SIZE, umlSizeOf } from '../src/layout/umlSize.ts';
import type { Document, Node } from '../src/model/types.ts';

const library = JSON.parse(readFileSync(new URL('../../src/Draughtsman.Core/Schemas/uml-shapes.json', import.meta.url), 'utf8')) as {
  metrics: Record<string, number>;
  shapes: { name: string; defaultSize?: { w: number; h: number } }[];
};

function node(shape: string, props?: Node['props'], label = 'Order'): Node {
  return { id: 'n', kind: 'class', label, shape, ...(props === undefined ? {} : { props }) };
}

describe('uml sizing', () => {
  it('uses the metrics of the shared library file', () => {
    // Every metric the sidecar copies must equal the file the web canvas and the C# renderer read.
    for (const [name, value] of Object.entries(UML_METRICS)) {
      expect(library.metrics[name], name).toBe(value);
    }
    for (const shape of library.shapes.filter((candidate) => candidate.defaultSize !== undefined)) {
      expect(shape.defaultSize).toEqual(UML_SYMBOL_SIZE);
    }
  });

  it('sizes a class from its members with the same numbers the other renderers use', () => {
    // Header 28 + one attribute row 28 + one operation row 28; the same literals are asserted in the web and C# tests.
    expect(umlSizeOf(node('custom:uml/class', { attributes: ['- id: int'], operations: ['+ save(): void'] }))).toEqual({ w: 120, h: 84 });
    // Three attributes: 10 + 3 * 18 = 64; an interface's stereotype row makes the header 46.
    expect(umlSizeOf(node('custom:uml/class', { attributes: ['a', 'b', 'c'] }))?.h).toBe(28 + 64 + 28);
    expect(umlSizeOf(node('custom:uml/interface'))?.h).toBe(46 + 28 + 28);
    // Width follows the longest member line plus the glyph column, and never drops under the minimum.
    expect(umlSizeOf(node('custom:uml/class', { operations: ['+ findByCustomerAndStatus(customer: Customer): List'] }))?.w).toBe(Math.ceil(16 + 12 + 'findByCustomerAndStatus(customer: Customer): List'.length * 7.5));
  });

  it('ignores props that are not member lists, and leaves other shapes alone', () => {
    expect(umlSizeOf(node('custom:uml/class', { attributes: 'not a list', operations: [3, null, ''] }))?.h).toBe(84);
    expect(umlSizeOf(node('rectangle'))).toBeUndefined();
    expect(umlSizeOf(node('custom:uml/unknown'))).toBeUndefined();
    expect(umlSizeOf(node('custom:uml/lollipop'))).toEqual(UML_SYMBOL_SIZE);
  });

  it('lays a class with no geometry out at its natural size and keeps a given size', async () => {
    const input: Document = {
      id: 'doc_uml',
      schemaVersion: 1,
      title: 'UML',
      type: 'freeform',
      nodes: [
        { id: 'a', kind: 'class', label: 'Order', shape: 'custom:uml/class', props: { attributes: ['- id: int', '- total: money'], operations: ['+ pay(): void'] } },
        { id: 'b', kind: 'class', label: 'Fixed', shape: 'custom:uml/class', geometry: { x: 0, y: 0, w: 300, h: 200 } },
      ],
      edges: [{ id: 'e', kind: 'association', from: { node: 'a' }, to: { node: 'b' } }],
      meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
    };
    const { document } = await layoutDocument(input, {});
    const byId = new Map(document.nodes.map((laid) => [laid.id, laid]));
    // Two attributes: 10 + 36 = 46, so 28 + 46 + 28.
    expect(byId.get('a')?.geometry.h).toBe(102);
    expect(byId.get('b')?.geometry.w).toBe(300);
    expect(byId.get('b')?.geometry.h).toBe(200);
  });
});
