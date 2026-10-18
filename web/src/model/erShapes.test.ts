import { ER_EXAMPLES, ER_METRICS, ER_OPTIONS, ER_SHAPES, columnLines, defaultSize, erShape, erShapeName, fitEntity, layoutEntity, nullText, keyText, parseColumn, partAt, partBox } from './erShapes.ts';
import { memberPartsOf } from './memberParts.ts';
import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json' with { type: 'json' };

const defaultColumns = ['PK Id int not null', 'Name nvarchar(100) not null', 'Notes nvarchar(max) null'];

describe('parseColumn', () => {
  it('parses every example of the library file the way the file says (the C# and sidecar tests replay the same list)', () => {
    expect(ER_EXAMPLES.length).toBeGreaterThan(10);
    for (const example of ER_EXAMPLES) {
      const parsed = parseColumn(example.line);
      expect({ keys: parsed.keys, name: parsed.name, type: parsed.type, nullable: parsed.nullable ?? null }, example.line).toEqual({ keys: example.keys, name: example.name, type: example.type, nullable: example.nullable });
    }
  });

  it('splits the SQL Server spellings the first real schema uses, keeping a type with spaces, commas and parentheses whole', () => {
    expect(parseColumn('FK UserId nvarchar(50) not null')).toEqual({ keys: ['FK'], name: 'UserId', type: 'nvarchar(50)', nullable: 'notnull' });
    expect(parseColumn('CreatedAt datetime2 not null')).toEqual({ keys: [], name: 'CreatedAt', type: 'datetime2', nullable: 'notnull' });
    expect(parseColumn('PK Id uniqueidentifier not null').type).toBe('uniqueidentifier');
    expect(parseColumn('Amount decimal(18, 4) null')).toEqual({ keys: [], name: 'Amount', type: 'decimal(18, 4)', nullable: 'null' });
    expect(parseColumn('Ts rowversion not null').type).toBe('rowversion');
  });

  it('reads a column that is both keys, in either order, as PK then FK', () => {
    expect(parseColumn('PK FK OrderId int not null').keys).toEqual(['PK', 'FK']);
    expect(parseColumn('FK PK OrderId int not null').keys).toEqual(['PK', 'FK']);
    // A marker repeated is the column's name, not a second marker.
    expect(parseColumn('PK PK int')).toEqual({ keys: ['PK'], name: 'PK', type: 'int', nullable: undefined });
  });

  it('keeps null and not null apart, and a type that merely contains the word intact', () => {
    expect(parseColumn('A int null').nullable).toBe('null');
    expect(parseColumn('A int not null').nullable).toBe('notnull');
    expect(parseColumn('A int').nullable).toBeUndefined();
    // "null" is only nullability at the end of the line, after whitespace.
    expect(parseColumn('A nullable_thing').type).toBe('nullable_thing');
    expect(parseColumn('A int not  NULL').nullable).toBe('notnull');
  });

  it('draws the marker and nullability texts the columns show', () => {
    expect(keyText(parseColumn('PK FK A int'))).toBe('PK FK');
    expect(keyText(parseColumn('A int'))).toBe('');
    expect(nullText(parseColumn('A int not null'))).toBe('NOT NULL');
    expect(nullText(parseColumn('A int null'))).toBe('NULL');
    expect(nullText(parseColumn('A int'))).toBe('');
  });
});

describe('layoutEntity', () => {
  it('stacks a name band over one row per column and aligns each column at the widest cell', () => {
    const layout = layoutEntity('TableName', { columns: defaultColumns }, 0);
    // Name band 5 + 18 + 5; the row area 5 + 3 * 18 + 5.
    expect(layout.headerHeight).toBe(28);
    expect(layout.body).toEqual({ top: 28, height: 64 });
    expect(layout.naturalHeight).toBe(92);
    // Key column: "PK" bold (2 * 7.5 * 1.1 = 16.5), name "Notes" (37.5), type "nvarchar(100)" (97.5), then "NOT NULL" (upper case, 8 * 7.5 * 1.1 = 66), 12 apart.
    expect(layout.offsets).toEqual({ key: 8, name: 36.5, type: 86, nullable: 195.5 });
    expect(layout.naturalWidth).toBe(270);
  });

  it('puts a divider after the leading primary-key rows only, and none when every row or no row is a key', () => {
    expect(layoutEntity('T', { columns: ['PK A int', 'PK B int', 'C int'] }, 0).keyDivider).toBe(28 + 5 + 36);
    expect(layoutEntity('T', { columns: ['PK A int', 'PK B int'] }, 0).keyDivider).toBeUndefined();
    expect(layoutEntity('T', { columns: ['A int', 'B int'] }, 0).keyDivider).toBeUndefined();
    // A key row below a plain row is not a leading run.
    expect(layoutEntity('T', { columns: ['A int', 'PK B int'] }, 0).keyDivider).toBeUndefined();
    // FK alone does not make a primary-key block.
    expect(layoutEntity('T', { columns: ['FK A int', 'B int'] }, 0).keyDivider).toBeUndefined();
  });

  it('leaves out a column no row fills, so a table with no markers or nullability is not padded for them', () => {
    const layout = layoutEntity('T', { columns: ['Id int', 'Name text'] }, 0);
    expect(layout.offsets).toEqual({ name: 8, type: 8 + 4 * 7.5 + 12 });
    expect(layout.offsets.key).toBeUndefined();
    expect(layout.offsets.nullable).toBeUndefined();
  });

  it('keeps one row for an empty entity, ignores blank and non-string lines, and never goes under the minimum width', () => {
    expect(layoutEntity('T', undefined, 0).naturalHeight).toBe(28 + 28);
    expect(layoutEntity('T', { columns: [] }, 0).naturalWidth).toBe(ER_METRICS.minWidth);
    expect(columnLines({ columns: ['A int', '', '  ', 3, null, 'B int'] })).toEqual(['A int', 'B int']);
    expect(columnLines({ columns: 'A int' })).toEqual([]);
    expect(layoutEntity('T', { columns: ['A int', '', 7] }, 0).rows).toHaveLength(1);
  });

  it('widens for a long table name (bold) even when the rows are narrow', () => {
    expect(layoutEntity('AVeryLongTableNameIndeed', { columns: ['Id int'] }, 0).naturalWidth).toBe(Math.ceil(24 * 7.5 * 1.1 + 16));
  });

  it('gives a box taller than its content the extra in the row area, and never squeezes a shorter one', () => {
    const props = { columns: ['PK Id int not null'] };
    expect(layoutEntity('T', props, 200).body).toEqual({ top: 28, height: 172 });
    expect(layoutEntity('T', props, 200).headerHeight).toBe(28);
    expect(layoutEntity('T', props, 10).body.height).toBe(28);
  });

  it('refits height exactly and width only upward', () => {
    const props = { columns: defaultColumns };
    expect(fitEntity('TableName', props, { x: 5, y: 6, w: 300, h: 40, rotation: 10 })).toEqual({ x: 5, y: 6, w: 300, h: 92, rotation: 10 });
    expect(fitEntity('TableName', props, { x: 5, y: 6, w: 100, h: 500 })).toEqual({ x: 5, y: 6, w: 270, h: 92 });
  });

  it('finds the part a node-local y is in', () => {
    const layout = layoutEntity('T', { columns: ['A int'] }, 0);
    expect(partAt(layout, 27)).toBe('name');
    expect(partAt(layout, 28)).toBe('columns');
    expect(partBox(layout, 'name', 200)).toEqual({ x: 0, y: 0, w: 200, h: 28 });
    expect(partBox(layout, 'columns', 200)).toEqual({ x: 0, y: 28, w: 200, h: 28 });
  });
});

describe('the library', () => {
  it('has an entity and a weak entity, only the weak one double bordered, each the default shape of its architecture kind', () => {
    expect(ER_SHAPES.map((shape) => [shape.name, shape.double])).toEqual([['entity', false], ['weak-entity', true]]);
    for (const shape of ER_SHAPES) {
      const kind = architecture.nodeKinds.find((candidate) => candidate.name === shape.kind);
      expect(kind?.defaultShape).toBe(erShapeName(shape.name));
      expect(kind?.description.length ?? 0).toBeGreaterThan(40);
    }
    // No other architecture kind claims an ER shape.
    const claimed = architecture.nodeKinds.filter((kind) => kind.defaultShape.startsWith('custom:er/')).map((kind) => kind.name);
    expect(claimed).toEqual(ER_SHAPES.map((shape) => shape.kind));
    expect(erShape('custom:er/entity')?.name).toBe('entity');
    expect(erShape('custom:er/not-a-shape')).toBeUndefined();
    expect(erShape('custom:uml/entity')).toBeUndefined();
  });

  it('teaches an agent the column line format from the kind description alone', () => {
    const description = architecture.nodeKinds.find((kind) => kind.name === 'entity')?.description ?? '';
    expect(description).toContain('props.columns');
    expect(description).toContain('[PK] [FK] name type [not null | null]');
    expect(description).toContain('PK FK OrderId int not null');
    expect(description).toContain('crow');
  });

  it('places each shape sized and populated from its default columns', () => {
    expect(ER_SHAPES.map(defaultSize)).toEqual([{ w: 270, h: 92 }, { w: 310, h: 92 }]);
    expect(ER_OPTIONS[0]?.template).toEqual({ kind: 'entity', shape: 'custom:er/entity', label: 'TableName', size: { w: 270, h: 92 }, props: { columns: defaultColumns } });
    // The weak entity's default key is a composite PK that is also an FK: 41.25 + 52.5 + 97.5 + 66 + three gaps + padding.
    expect(ER_OPTIONS[1]?.template.size).toEqual({ w: 310, h: 92 });
  });

  it('marks an entity as having exactly one editable member compartment, a class two, and a plain shape none', () => {
    expect(memberPartsOf('custom:er/entity')).toEqual(['columns']);
    expect(memberPartsOf('custom:er/weak-entity')).toEqual(['columns']);
    expect(memberPartsOf('custom:uml/class')).toEqual(['attributes', 'operations']);
    expect(memberPartsOf('custom:uml/lollipop')).toEqual([]);
    expect(memberPartsOf('rectangle')).toEqual([]);
  });
});
