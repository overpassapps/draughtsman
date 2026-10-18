import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import schema from '../../../docs/schema/document.schema.json';
import { defaultPorts } from './connect.ts';
import {
  defaultSize,
  fitClass,
  isClassShape,
  layoutClass,
  memberLines,
  parseMember,
  partAt,
  partBox,
  stereotypeOf,
  UML_OPTIONS,
  UML_SHAPES,
  UML_SHAPE_PREFIX,
  umlShape,
  umlShapeName,
} from './umlShapes.ts';

function shapeNamed(name: string) {
  const shape = umlShape(umlShapeName(name));
  if (shape === undefined) {
    throw new Error(`no UML shape ${name}`);
  }
  return shape;
}

describe('UML palette options', () => {
  it('lists every shape once, each admitted by the schema', () => {
    const pattern = new RegExp(schema.$defs.node.properties.shape.pattern);
    expect(UML_OPTIONS.map((option) => option.name)).toEqual([...new Set(UML_OPTIONS.map((option) => option.name))]);
    expect(UML_OPTIONS.length).toBe(UML_SHAPES.length);
    expect(UML_OPTIONS.length).toBe(4);
    for (const option of UML_OPTIONS) {
      expect(option.shape).toBe(UML_SHAPE_PREFIX + option.name);
      expect(pattern.test(option.shape), `${option.shape} is not in the schema`).toBe(true);
    }
    // Two-sided: a name outside the library is not a UML shape.
    expect(umlShape('custom:uml/not-a-shape')).toBeUndefined();
    expect(umlShape('custom:org/role')).toBeUndefined();
  });

  it('matches the architecture vocabulary both ways, each kind naming its shape as default', () => {
    const vocabulary = architecture.nodeKinds.filter((kind) => kind.defaultShape.startsWith(UML_SHAPE_PREFIX));
    expect(new Set(vocabulary.map((kind) => kind.name))).toEqual(new Set(UML_OPTIONS.map((option) => option.kind)));
    for (const option of UML_OPTIONS) {
      expect(vocabulary.find((kind) => kind.name === option.kind)?.defaultShape).toBe(option.shape);
      expect(option.kind).not.toBe('shape');
    }
  });

  it('places a class or interface sized to its starter members and a symbol at its fixed size', () => {
    const byName = new Map(UML_OPTIONS.map((option) => [option.name, option.template]));
    // Header 28 + a one-row attribute compartment 28 + a one-row operation compartment 28.
    expect(byName.get('class')?.size).toEqual({ w: 156, h: 84 });
    expect(byName.get('class')?.props).toEqual({ attributes: ['- attribute: Type'], operations: ['+ operation(): void'] });
    // The interface's stereotype row makes its header 46 tall, and it starts with operations only.
    expect(byName.get('interface')?.size).toEqual({ w: 156, h: 102 });
    expect(byName.get('interface')?.props).toEqual({ operations: ['+ operation(): void'] });
    expect(byName.get('lollipop')?.size).toEqual({ w: 72, h: 48 });
    expect(byName.get('lollipop')?.props).toBeUndefined();
  });
});

describe('members', () => {
  it('splits the visibility glyph off a member line and leaves a plain line alone', () => {
    expect(parseMember('+ save(): void')).toEqual({ visibility: '+', text: 'save(): void' });
    expect(parseMember('-id: int')).toEqual({ visibility: '-', text: 'id: int' });
    expect(parseMember('# helper()')).toEqual({ visibility: '#', text: 'helper()' });
    expect(parseMember('~ shared: bool')).toEqual({ visibility: '~', text: 'shared: bool' });
    expect(parseMember('  name: string  ')).toEqual({ visibility: '', text: 'name: string' });
    // Only the first character counts; a glyph inside the text is text.
    expect(parseMember('a + b')).toEqual({ visibility: '', text: 'a + b' });
  });

  it('reads only the non-blank strings of a list, in order', () => {
    expect(memberLines({ attributes: ['a', '', '  ', 3, null, 'b'] }, 'attributes')).toEqual(['a', 'b']);
    expect(memberLines({ attributes: 'a' }, 'attributes')).toEqual([]);
    expect(memberLines(undefined, 'operations')).toEqual([]);
    expect(memberLines({ attributes: ['x'] }, 'operations')).toEqual([]);
  });

  it('shows the interface stereotype by default, a stereotype prop over it, and none for a plain class', () => {
    expect(stereotypeOf(shapeNamed('interface'), undefined)).toBe('interface');
    expect(stereotypeOf(shapeNamed('class'), undefined)).toBeUndefined();
    expect(stereotypeOf(shapeNamed('class'), { stereotype: 'abstract' })).toBe('abstract');
    expect(stereotypeOf(shapeNamed('interface'), { stereotype: 'service' })).toBe('service');
    // An empty stereotype prop hides the default.
    expect(stereotypeOf(shapeNamed('interface'), { stereotype: '' })).toBeUndefined();
  });
});

describe('class layout', () => {
  it('stacks three compartments whose heights follow the content', () => {
    const layout = layoutClass(shapeNamed('class'), 'Order', { attributes: ['- id: int', '- total: money', '- paid: bool'], operations: ['+ pay(): void'] }, 0);
    // Name band 10 + 18; three rows 10 + 54; one row 10 + 18.
    expect(layout.headerHeight).toBe(28);
    expect(layout.attributes).toMatchObject({ top: 28, height: 64 });
    expect(layout.operations).toMatchObject({ top: 92, height: 28 });
    expect(layout.naturalHeight).toBe(120);
    expect(layout.attributes.hasGlyph).toBe(true);
  });

  it('keeps one row for an empty compartment and adds a stereotype row to the name band', () => {
    const empty = layoutClass(shapeNamed('class'), 'Marker', undefined, 0);
    expect(empty.attributes.height).toBe(28);
    expect(empty.operations.height).toBe(28);
    expect(empty.naturalHeight).toBe(84);
    const iface = layoutClass(shapeNamed('interface'), 'Repo', undefined, 0);
    expect(iface.headerHeight).toBe(46);
    expect(iface.naturalHeight).toBe(102);
  });

  it('gives extra height to the operations compartment and never squeezes under the content', () => {
    const props = { attributes: ['- a'], operations: ['+ b()'] };
    const taller = layoutClass(shapeNamed('class'), 'C', props, 200);
    expect(taller.operations.height).toBe(200 - 28 - 28);
    expect(taller.attributes.height).toBe(28);
    // Shorter than the content: the compartment keeps its own height (the box is the caller's to fit).
    const shorter = layoutClass(shapeNamed('class'), 'C', props, 40);
    expect(shorter.operations.height).toBe(28);
    expect(shorter.naturalHeight).toBe(84);
  });

  it('sizes the width from the longest line, the glyph column and the name, with a minimum', () => {
    expect(layoutClass(shapeNamed('class'), 'A', undefined, 0).naturalWidth).toBe(120);
    const long = layoutClass(shapeNamed('class'), 'A', { operations: ['+ findByCustomerAndStatus(): void'] }, 0);
    // 16 padding + 12 glyph column + 7.5 per character of "findByCustomerAndStatus(): void" (31).
    expect(long.naturalWidth).toBe(Math.ceil(16 + 12 + 31 * 7.5));
    // Without a glyph the column is not reserved.
    const bare = layoutClass(shapeNamed('class'), 'A', { operations: ['findByCustomerAndStatus(): void'] }, 0);
    expect(bare.naturalWidth).toBe(Math.ceil(16 + 31 * 7.5));
    // The name is bold and measured wider.
    expect(layoutClass(shapeNamed('class'), 'AVeryLongClassNameIndeed', undefined, 0).naturalWidth).toBe(Math.ceil(24 * 7.5 * 1.1 + 16));
  });

  it('finds the part of the box a y falls in, at the compartment boundaries', () => {
    const layout = layoutClass(shapeNamed('class'), 'C', { attributes: ['- a'], operations: ['+ b()'] }, 84);
    expect(partAt(layout, 0)).toBe('name');
    expect(partAt(layout, 27.9)).toBe('name');
    expect(partAt(layout, 28)).toBe('attributes');
    expect(partAt(layout, 55.9)).toBe('attributes');
    expect(partAt(layout, 56)).toBe('operations');
    expect(partAt(layout, 83)).toBe('operations');
    expect(partBox(layout, 'attributes', 160)).toEqual({ x: 0, y: 28, w: 160, h: 28 });
    expect(partBox(layout, 'name', 160)).toEqual({ x: 0, y: 0, w: 160, h: 28 });
  });

  it('fits a box to its content: exactly the needed height, width only ever grown', () => {
    const props = { attributes: ['- a', '- b'], operations: ['+ c()'] };
    const fitted = fitClass(shapeNamed('class'), 'C', props, { x: 5, y: 6, w: 400, h: 500, rotation: 10 });
    expect(fitted).toEqual({ x: 5, y: 6, w: 400, h: 28 + 46 + 28, rotation: 10 });
    const grown = fitClass(shapeNamed('class'), 'C', { operations: ['+ aVeryLongOperationName(argument: Type): ReturnType'] }, { x: 0, y: 0, w: 100, h: 100 });
    expect(grown.w).toBeGreaterThan(300);
    expect(grown.h).toBe(84);
  });

  it('identifies which shapes have compartments', () => {
    expect(isClassShape(shapeNamed('class'))).toBe(true);
    expect(isClassShape(shapeNamed('interface'))).toBe(true);
    expect(isClassShape(shapeNamed('lollipop'))).toBe(false);
    expect(isClassShape(undefined)).toBe(false);
    expect(defaultSize(shapeNamed('socket'))).toEqual({ w: 72, h: 48 });
  });
});

describe('interface symbol ports', () => {
  it('puts a lollipop and a socket port at the symbol band mid-height, left and right only', () => {
    for (const name of ['lollipop', 'socket']) {
      const ports = defaultPorts(umlShapeName(name));
      expect(new Set(ports.map((port) => port.id))).toEqual(new Set(['left', 'right']));
      for (const port of ports) {
        expect(port.y).toBe(0.25);
      }
      expect(ports.find((port) => port.id === 'left')?.x).toBe(0);
      expect(ports.find((port) => port.id === 'right')?.x).toBe(1);
    }
    // A class keeps the four ordinary side ports.
    expect(defaultPorts(umlShapeName('class')).map((port) => port.id).sort()).toEqual(['bottom', 'left', 'right', 'top']);
  });
});
