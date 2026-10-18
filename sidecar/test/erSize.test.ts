import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { layoutDocument } from '../src/layout/elk.ts';
import { ER_METRICS, erSizeOf, parseColumn } from '../src/layout/erSize.ts';
import type { Document, Node } from '../src/model/types.ts';

const library = JSON.parse(readFileSync(new URL('../../src/Draughtsman.Core/Schemas/er-shapes.json', import.meta.url), 'utf8')) as {
  metrics: Record<string, number>;
  examples: { line: string; keys: string[]; name: string; type: string; nullable: string | null }[];
};

function node(shape: string, props?: Node['props'], label = 'Users'): Node {
  return { id: 'n', kind: 'entity', label, shape, ...(props === undefined ? {} : { props }) };
}

describe('er sizing', () => {
  it('uses the metrics of the shared library file', () => {
    // Every metric the sidecar copies must equal the file the web canvas and the C# renderer read.
    for (const [name, value] of Object.entries(ER_METRICS)) {
      expect(library.metrics[name], name).toBe(value);
    }
  });

  it('parses every example of the shared library file the way the file says', () => {
    expect(library.examples.length).toBeGreaterThan(10);
    for (const example of library.examples) {
      const parsed = parseColumn(example.line);
      expect({ keys: parsed.keys, name: parsed.name, type: parsed.type, nullable: parsed.nullable ?? null }, example.line).toEqual({ keys: example.keys, name: example.name, type: example.type, nullable: example.nullable });
    }
  });

  it('sizes an entity from its columns with the same numbers the other renderers use', () => {
    // The palette's default columns: 28 + (10 + 3 * 18) = 92 tall; key 16.5, name 37.5, type 97.5, null 66 and three 12 gaps, plus padding 16.
    const columns = ['PK Id int not null', 'Name nvarchar(100) not null', 'Notes nvarchar(max) null'];
    expect(erSizeOf(node('custom:er/entity', { columns }, 'TableName'))).toEqual({ w: 270, h: 92 });
    // The weak entity sizes by the same rule: composite PK FK key column, then the widest name and type.
    expect(erSizeOf(node('custom:er/weak-entity', { columns: ['PK FK OwnerId int not null', 'PK Line int not null', 'Detail nvarchar(200) null'] }, 'WeakTable'))).toEqual({ w: 310, h: 92 });
    // An empty entity keeps one row and the minimum width.
    expect(erSizeOf(node('custom:er/entity'))).toEqual({ w: 120, h: 56 });
    // A column no row fills is left out: no markers and no nullability, so just name and type.
    expect(erSizeOf(node('custom:er/entity', { columns: ['Id int', 'Name text'] }, 'T'))?.w).toBe(Math.max(120, Math.ceil(16 + 4 * 7.5 + 12 + 4 * 7.5)));
  });

  it('ignores props that are not column lists, and leaves other shapes alone', () => {
    expect(erSizeOf(node('custom:er/entity', { columns: 'not a list' }))?.h).toBe(56);
    expect(erSizeOf(node('custom:er/entity', { columns: [3, null, ''] }))?.h).toBe(56);
    expect(erSizeOf(node('rectangle'))).toBeUndefined();
    expect(erSizeOf(node('custom:er/unknown'))).toBeUndefined();
    expect(erSizeOf(node('custom:uml/class'))).toBeUndefined();
  });

  it('lays a table with no geometry out at its natural size and keeps a given size', async () => {
    const input: Document = {
      id: 'doc_er',
      schemaVersion: 1,
      title: 'ER',
      type: 'freeform',
      nodes: [
        { id: 'a', kind: 'entity', label: 'Users', shape: 'custom:er/entity', props: { columns: ['PK Id int not null', 'Email nvarchar(100) null'] } },
        { id: 'b', kind: 'entity', label: 'Fixed', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 300, h: 200 } },
      ],
      edges: [{ id: 'e', kind: 'association', from: { node: 'a' }, to: { node: 'b' } }],
      meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
    };
    const { document } = await layoutDocument(input, {});
    const byId = new Map(document.nodes.map((laid) => [laid.id, laid]));
    // Two rows: 28 + 10 + 36.
    expect(byId.get('a')?.geometry.h).toBe(74);
    expect(byId.get('b')?.geometry.w).toBe(300);
    expect(byId.get('b')?.geometry.h).toBe(200);
  });
});
