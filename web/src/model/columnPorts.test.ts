import { describe, expect, it } from 'vitest';
import { COLUMN_PORT_PREFIX, columnPort, columnPortAt, columnPortId, columnPortName, columnRenames, columnRowOf, columnRows, columnSideFacing, isColumnPortId } from './columnPorts.ts';
import type { Geometry } from './types.ts';

const ENTITY = 'custom:er/entity';
const props = { columns: ['PK Id int not null', 'FK "Customer Id" int not null', 'Placed datetime2 not null'] };
const geometry: Geometry = { x: 100, y: 50, w: 270, h: 92 };
const table = { shape: ENTITY, props, geometry };

describe('column port ids', () => {
  it('spell a plain name as col:<name>', () => {
    expect(columnPortId('CustomerId')).toBe('col:CustomerId');
    expect(columnPortName('col:CustomerId')).toBe('CustomerId');
  });

  it('escape everything outside letters, digits, underscore and hyphen, so the id fits the schema and the DSL', () => {
    expect(columnPortId('Customer Id')).toBe('col:Customer%20Id');
    expect(columnPortId('a.b')).toBe('col:a%2Eb');
    expect(columnPortId('100%')).toBe('col:100%25');
    expect(columnPortId('café')).toBe('col:caf%C3%A9');
    for (const name of ['Customer Id', 'a.b', '100%', 'café', 'x:y', '日本']) {
      const id = columnPortId(name);
      expect(id).toMatch(/^col:[A-Za-z0-9_%-]+$/);
      expect(columnPortName(id)).toBe(name);
    }
  });

  it('have no id for an empty name or one whose id would pass the 128-character limit', () => {
    expect(columnPortId('')).toBeUndefined();
    expect(columnPortId('x'.repeat(124))?.length).toBe(128);
    expect(columnPortId('x'.repeat(125))).toBeUndefined();
    // Escapes count: 42 spaces are 126 characters after `col:`.
    expect(columnPortId(' '.repeat(42))).toBeUndefined();
  });

  it('are told apart from an ordinary port, and a malformed one names no column', () => {
    expect(isColumnPortId('col:Id')).toBe(true);
    expect(isColumnPortId('left')).toBe(false);
    expect(isColumnPortId(undefined)).toBe(false);
    expect(columnPortName('left')).toBeUndefined();
    expect(columnPortName(COLUMN_PORT_PREFIX)).toBeUndefined();
    expect(columnPortName('col:bad%2')).toBeUndefined();
    expect(columnPortName('col:%FF')).toBeUndefined();
  });
});

describe('column rows', () => {
  it('sit below the 28-unit name band, an 18-unit row each, the first inset by the 5-unit padding', () => {
    expect(columnRows({ shape: ENTITY, props }).map((row) => [row.name, row.top, row.centre])).toEqual([
      ['Id', 33, 42],
      ['Customer Id', 51, 60],
      ['Placed', 69, 78],
    ]);
  });

  it('exist only on an entity', () => {
    expect(columnRows({ shape: 'rectangle', props })).toEqual([]);
    expect(columnRows({ shape: 'custom:er/weak-entity', props }).length).toBe(3);
    expect(columnRows({ shape: ENTITY })).toEqual([]);
  });

  it('are found by the decoded name, and a name no row has finds nothing', () => {
    expect(columnRowOf({ shape: ENTITY, props }, 'col:Customer%20Id')?.index).toBe(1);
    expect(columnRowOf({ shape: ENTITY, props }, 'col:Missing')).toBeUndefined();
    expect(columnRowOf({ shape: ENTITY, props }, 'left')).toBeUndefined();
  });
});

describe('column ports', () => {
  it('put the end at the middle of the row, on the side asked for', () => {
    expect(columnPort(table, 'col:Id', 'right')).toEqual({ id: 'col:Id', x: 1, y: 42 / 92 });
    expect(columnPort(table, 'col:Placed', 'left')).toEqual({ id: 'col:Placed', x: 0, y: 78 / 92 });
  });

  it('stay on the same row when the box grows, because the extra height goes below the rows', () => {
    const taller = { ...table, geometry: { ...geometry, h: 184 } };
    expect(columnPort(taller, 'col:Placed', 'right')?.y).toBe(78 / 184);
  });

  it('resolve to nothing for a column the table lacks, or a node that is not a table', () => {
    expect(columnPort(table, 'col:Missing', 'right')).toBeUndefined();
    expect(columnPort({ shape: 'rectangle', props, geometry }, 'col:Id', 'right')).toBeUndefined();
  });

  it('face the side the other end is on', () => {
    expect(columnSideFacing(geometry, { x: 900, y: 0 })).toBe('right');
    expect(columnSideFacing(geometry, { x: 0, y: 0 })).toBe('left');
  });
});

describe('column port hit-testing', () => {
  it('names the row under a point and the nearer side', () => {
    expect(columnPortAt(table, { x: 110, y: 50 + 60 })).toMatchObject({ id: 'col:Customer%20Id', x: 0 });
    expect(columnPortAt(table, { x: 360, y: 50 + 78 })).toMatchObject({ id: 'col:Placed', x: 1 });
  });

  it('gives the padding above the first row to the first, and the space under the last to the last', () => {
    expect(columnPortAt(table, { x: 200, y: 50 + 29 })?.id).toBe('col:Id');
    expect(columnPortAt(table, { x: 200, y: 50 + 91 })?.id).toBe('col:Placed');
  });

  it('is nothing over the name band, outside the box or on a table with no rows', () => {
    expect(columnPortAt(table, { x: 200, y: 50 + 10 })).toBeUndefined();
    expect(columnPortAt(table, { x: 99, y: 50 + 60 })).toBeUndefined();
    expect(columnPortAt(table, { x: 371, y: 50 + 60 })).toBeUndefined();
    expect(columnPortAt(table, { x: 200, y: 50 + 93 })).toBeUndefined();
    expect(columnPortAt({ shape: ENTITY, props: {}, geometry }, { x: 200, y: 50 + 60 })).toBeUndefined();
  });

  it('follows a rotated table: a point is read in the table’s own frame', () => {
    // A quarter turn about the centre (235, 96): the table's local point (x, y) lands at (235 - (y - 46), 96 + (x - 135)).
    const turned = { ...table, geometry: { ...geometry, rotation: 90 } };
    const local = { x: 20, y: 60 };
    const world = { x: 235 - (local.y - 46), y: 96 + (local.x - 135) };
    expect(columnPortAt(turned, world)).toMatchObject({ id: 'col:Customer%20Id', x: 0 });
    expect(columnPortAt(table, world)?.id).not.toBe('col:Customer%20Id');
  });
});

describe('column renames', () => {
  it('pair a retyped name with the name it replaced', () => {
    expect(columnRenames(['PK Id int', 'Name text'], ['PK Id int', 'FullName text'])).toEqual(new Map([['Name', 'FullName']]));
  });

  it('pair several renames in list order, and ignore an edit that keeps every name', () => {
    expect(columnRenames(['A int', 'B int'], ['X int', 'Y int'])).toEqual(new Map([['A', 'X'], ['B', 'Y']]));
    expect(columnRenames(['A int'], ['A bigint'])).toEqual(new Map());
  });

  it('rename nothing when rows are added, removed or reordered, or the counts do not match', () => {
    expect(columnRenames(['A int'], ['A int', 'B int'])).toEqual(new Map());
    expect(columnRenames(['A int', 'B int'], ['A int'])).toEqual(new Map());
    expect(columnRenames(['A int', 'B int'], ['B int', 'A int'])).toEqual(new Map());
    expect(columnRenames(['A int', 'B int'], ['A int', 'C int', 'D int'])).toEqual(new Map());
  });
});
