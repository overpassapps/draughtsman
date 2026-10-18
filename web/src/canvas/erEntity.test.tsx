// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { ER_SHAPES, erShapeName } from '../model/erShapes.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import type { PlacedNode } from './documentIndex.ts';
import { compartmentAt } from './labelEditing.ts';
import { renderShape, type ShapeContent } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'entity');

const columns = ['PK Id int not null', 'Name nvarchar(100) not null', 'Notes nvarchar(max) null'];

function svg(shape: string, content?: ShapeContent, w = 264, h = 92): SVGSVGElement {
  const { container, unmount } = render(<svg>{renderShape(shape, w, h, style, undefined, content).element}</svg>);
  const host = document.createElement('div');
  host.innerHTML = container.innerHTML;
  unmount();
  const root = host.querySelector('svg');
  if (root === null) {
    throw new Error('no svg rendered');
  }
  return root;
}

function markup(shape: string, content?: ShapeContent, w = 264, h = 92): string {
  return svg(shape, content, w, h).outerHTML;
}

/** A row cell's text with its x and anchor, in document order. */
function cells(root: SVGSVGElement): { text: string; x: string; y: string; anchor: string; bold: boolean }[] {
  return [...root.querySelectorAll('text')].map((t) => ({ text: t.textContent, x: t.getAttribute('x') ?? '', y: t.getAttribute('y') ?? '', anchor: t.getAttribute('text-anchor') ?? '', bold: t.getAttribute('font-weight') === 'bold' }));
}

describe('ER entity shapes', () => {
  it('draws every library shape as an ER shape, not the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(ER_SHAPES.length).toBe(2);
    for (const shape of ER_SHAPES) {
      const html = markup(erShapeName(shape.name), { label: 'T', props: { columns } });
      expect(html, `${shape.name} renders as the fallback rectangle`).not.toBe(fallback);
      expect(html).toContain('shape er');
    }
    // Two-sided: a name outside the library falls through to the fallback rectangle.
    expect(markup('custom:er/not-a-shape')).toBe(fallback);
  });

  it('draws a name band over rows laid out in aligned, left-aligned columns', () => {
    const root = svg('custom:er/entity', { label: 'TableName', props: { columns } });
    const all = cells(root);
    expect(all.map((c) => c.text)).toEqual(['TableName', 'PK', 'Id', 'int', 'NOT NULL', 'Name', 'nvarchar(100)', 'NOT NULL', 'Notes', 'nvarchar(max)', 'NULL']);
    // The name is centred and bold; every row cell is start-anchored.
    expect(all[0]).toMatchObject({ x: '132', anchor: 'middle', bold: true });
    for (const cell of all.slice(1)) {
      expect(cell.anchor, cell.text).toBe('start');
    }
    // Each column has ONE x for every row (aligned) ...
    const xOf = (text: string): string => all.find((c) => c.text === text)?.x ?? '';
    expect(new Set(['Id', 'Name', 'Notes'].map(xOf))).toEqual(new Set(['36.5']));
    expect(new Set(['int', 'nvarchar(100)', 'nvarchar(max)'].map(xOf))).toEqual(new Set(['86']));
    expect(new Set(all.filter((c) => c.text.endsWith('NULL')).map((c) => c.x))).toEqual(new Set(['195.5']));
    expect(xOf('PK')).toBe('8');
    // ... and each column starts to the right of the one before it (no two columns share an x).
    expect(new Set([xOf('PK'), xOf('Id'), xOf('int'), xOf('NOT NULL')]).size).toBe(4);
    // Rows step one row height down, from the middle of the first row (28 + 5 + 9).
    expect(all.filter((c) => ['Id', 'Name', 'Notes'].includes(c.text)).map((c) => c.y)).toEqual(['42', '60', '78']);
    // Markers are bold, the rest is not.
    expect(all.find((c) => c.text === 'PK')?.bold).toBe(true);
    expect(all.find((c) => c.text === 'Id')?.bold).toBe(false);
  });

  it('draws a rule under the name band and a divider under the leading primary-key rows', () => {
    const html = markup('custom:er/entity', { label: 'TableName', props: { columns } });
    expect(html).toContain('d="M0,28 H264 M0,51 H264"');
    // No primary key, or nothing but primary keys: only the rule under the name.
    expect(markup('custom:er/entity', { label: 'T', props: { columns: ['A int', 'B int'] } })).toContain('d="M0,28 H264"');
    expect(markup('custom:er/entity', { label: 'T', props: { columns: ['PK A int', 'PK B int'] } })).toContain('d="M0,28 H264"');
  });

  it('gives a weak entity an inner border and pulls its rules in to it; a plain entity has neither', () => {
    const weak = markup('custom:er/weak-entity', { label: 'W', props: { columns } });
    expect(weak).toContain('x="3" y="3" width="258" height="86"');
    expect(weak).toContain('d="M3,28 H261 M3,51 H261"');
    const plain = markup('custom:er/entity', { label: 'W', props: { columns } });
    expect(plain).not.toContain('x="3" y="3"');
    expect(plain).toContain('d="M0,28 H264 M0,51 H264"');
  });

  it('leaves out just the part the editor covers, or everything when labels are not legible', () => {
    const only = (hide: ShapeContent['hide']): string[] => cells(svg('custom:er/entity', { label: 'T', props: { columns: ['PK A int'] }, hide }, 200, 56)).map((c) => c.text);
    expect(only(undefined)).toEqual(['T', 'PK', 'A', 'int']);
    expect(only('columns')).toEqual(['T']);
    expect(only('name')).toEqual(['PK', 'A', 'int']);
    expect(only('all')).toEqual([]);
  });

  it('gives a box taller than its content the extra at the bottom, never moving the lines above', () => {
    const tight = markup('custom:er/entity', { label: 'T', props: { columns } }, 264, 92);
    const tall = markup('custom:er/entity', { label: 'T', props: { columns } }, 264, 300);
    expect(tall).toContain('d="M0,28 H264 M0,51 H264"');
    expect(tall).toContain('height="300"');
    expect(tight).toContain('height="92"');
  });

  it('draws a proportional glyph with no text when it has no content (the palette thumbnail)', () => {
    const root = svg('custom:er/entity', undefined, 96, 64);
    expect(root.querySelectorAll('text')).toHaveLength(0);
    expect(root.innerHTML).toContain('shape er');
  });

  it('ignores a columns prop that is not a list of strings, drawing an empty table', () => {
    expect(cells(svg('custom:er/entity', { label: 'T', props: { columns: 'PK Id int' } }, 200, 56)).map((c) => c.text)).toEqual(['T']);
  });

  it('does not treat a UML class as an entity: its own members still draw as a class', () => {
    const html = markup('custom:uml/class', { label: 'T', props: { attributes: ['- id: int'] } }, 200, 84);
    expect(html).toContain('shape uml');
    expect(html).not.toContain('shape er');
  });
});

describe('compartmentAt for an entity', () => {
  const base: PlacedNode = { id: 'n_e', kind: 'entity', label: 'T', shape: 'custom:er/entity', geometry: { x: 100, y: 100, w: 264, h: 92 }, props: { columns } };
  const placed = (extra: Partial<PlacedNode['geometry']> = {}): PlacedNode => ({ ...base, geometry: { ...base.geometry, ...extra } });

  it('is undefined on the name band and the columns area anywhere below it, and off the box', () => {
    expect(compartmentAt(placed(), { x: 200, y: 110 })).toBeUndefined();
    expect(compartmentAt(placed(), { x: 200, y: 127 })).toBeUndefined();
    expect(compartmentAt(placed(), { x: 200, y: 129 })).toBe('columns');
    expect(compartmentAt(placed(), { x: 200, y: 190 })).toBe('columns');
    expect(compartmentAt(placed(), { x: 50, y: 150 })).toBeUndefined();
    expect(compartmentAt(placed(), { x: 200, y: 400 })).toBeUndefined();
  });

  it('maps a point through the node rotation first', () => {
    // A 90 degree turn about the centre (232, 146): the row area now runs along the x axis' far side.
    const turned = placed({ rotation: 90 });
    expect(compartmentAt(turned, { x: 232, y: 146 })).toBe('columns');
    expect(compartmentAt(turned, { x: 232 + 40, y: 146 })).toBeUndefined();
  });

  it('is undefined for a shape with no compartments', () => {
    expect(compartmentAt({ ...placed(), shape: 'rectangle' }, { x: 200, y: 150 })).toBeUndefined();
  });
});
