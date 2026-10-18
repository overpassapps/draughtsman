/**
 * The entity-relationship (database table) shape library, read from the same embedded file the C# renderer and the
 * layout sidecar read (src/Draughtsman.Core/Schemas/er-shapes.json), so the web canvas, the server SVG renderer and the
 * layout cannot drift. It is the UML class mechanism (umlShapes.ts, docs/uml-class-compartments.md) applied to a table:
 * ONE node whose label is the table name and whose rows are the node's `props.columns`, an array of strings with one
 * column per string. A column line is `[PK] [FK] name type [not null | null]` (the library's `description` is the full
 * grammar, its `examples` the conformance suite). The line is stored exactly as typed, so it round-trips through JSON
 * and the DSL; `parseColumn` only derives what is drawn. Nothing but the box is stored: `layoutEntity` derives the name
 * band, the aligned column offsets and the key divider from that content, and a box taller than its content gives the
 * extra to the bottom of the row area. Mirrors `ErShapeLibrary` in Draughtsman.Core.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/er-shapes.json' with { type: 'json' };
import type { Rect } from '../canvas/geometry.ts';
import type { NodeTemplate } from './create.ts';
import type { Geometry, Props } from './types.ts';
import { memberLines } from './umlShapes.ts';

export interface ErMetrics {
  fontSize: number;
  rowHeight: number;
  padX: number;
  padY: number;
  charWidth: number;
  /** Width of a bold or upper-case character relative to an average one: the name, the key markers and NULL / NOT NULL. */
  boldWidth: number;
  /** Space between two neighbouring row columns (marker, name, type, nullability). */
  columnGap: number;
  minWidth: number;
  /** How far a weak entity's inner border sits inside the outer one. */
  doubleInset: number;
}

export interface ErShape {
  name: string;
  label: string;
  /** The architecture kind this shape is the default shape for. */
  kind: string;
  defaultLabel: string;
  /** A weak entity draws a double border. */
  double: boolean;
  defaultProps: Props;
}

/** One line the library promises to parse a particular way; a test in each implementation replays them all. */
export interface ErExample {
  line: string;
  keys: ErKey[];
  name: string;
  type: string;
  nullable: Nullability | null;
}

interface ErShapeFile {
  library: string;
  description: string;
  metrics: ErMetrics;
  shapes: ErShape[];
  examples: ErExample[];
}

const file = raw as unknown as ErShapeFile;

/** The shape-name prefix every ER shape carries: `custom:er/<name>`. */
export const ER_SHAPE_PREFIX = 'custom:er/';

export const COLUMNS_PROP = 'columns';

export const ER_METRICS: ErMetrics = file.metrics;

/** Every shape, in the library's own order (the palette's order). */
export const ER_SHAPES: readonly ErShape[] = file.shapes;

/** The parser's conformance examples (er-shapes.json), for the tests. */
export const ER_EXAMPLES: readonly ErExample[] = file.examples;

/** The shape name for an ER shape: `custom:er/<name>`. */
export function erShapeName(name: string): string {
  return ER_SHAPE_PREFIX + name;
}

/** The shape a `custom:er/<name>` presentation names, or undefined when it is not in the library. */
export function erShape(shape: string): ErShape | undefined {
  return shape.startsWith(ER_SHAPE_PREFIX) ? ER_SHAPES.find((candidate) => candidate.name === shape.slice(ER_SHAPE_PREFIX.length)) : undefined;
}

/** The parts of an entity a person edits: the name (the node label) and the column list. */
export type ErPart = 'name' | 'columns';

export type ErKey = 'PK' | 'FK';

export type Nullability = 'null' | 'notnull';

/** One column as drawn: its key markers (PK before FK), name, data type and nullability, when the line says. */
export interface ErColumn {
  keys: ErKey[];
  name: string;
  type: string;
  nullable: Nullability | undefined;
}

const KEY_MARKER = /^(PK|FK)[ \t]+(?=[^ \t])/i;
const NOT_NULL = /(?:^|[ \t])not[ \t]+null$/i;
const NULL = /(?:^|[ \t])null$/i;

/**
 * Splits a column line into what is drawn. Key markers `PK` and `FK` (any case, each at most once, in either order) are
 * taken from the front while something else follows them; then the name (one word, or double quoted when it holds
 * spaces); then a trailing `not null` or `null`; whatever is left, spaces collapsed, is the data type.
 */
export function parseColumn(line: string): ErColumn {
  let rest = line.trim();
  const found = new Set<ErKey>();
  for (;;) {
    const marker = KEY_MARKER.exec(rest);
    const key = marker?.[1]?.toUpperCase() as ErKey | undefined;
    if (marker === null || key === undefined || found.has(key)) {
      break;
    }
    found.add(key);
    rest = rest.slice(marker[0].length);
  }
  let name: string;
  if (rest.startsWith('"')) {
    const close = rest.indexOf('"', 1);
    name = close < 0 ? rest.slice(1) : rest.slice(1, close);
    rest = close < 0 ? '' : rest.slice(close + 1);
  } else {
    name = /^[^ \t]*/.exec(rest)?.[0] ?? '';
    rest = rest.slice(name.length);
  }
  rest = rest.trim();
  let nullable: Nullability | undefined;
  const notNull = NOT_NULL.exec(rest);
  const nullOnly = notNull === null ? NULL.exec(rest) : null;
  const tail = notNull ?? nullOnly;
  if (tail !== null) {
    nullable = notNull === null ? 'null' : 'notnull';
    rest = rest.slice(0, tail.index).trim();
  }
  return { keys: (['PK', 'FK'] as const).filter((key) => found.has(key)), name, type: rest.replace(/[ \t]+/g, ' '), nullable };
}

/** The column lines of `props.columns`: the non-blank strings in order; any other value in the list, or a non-list, is ignored. */
export function columnLines(props: Props | undefined): string[] {
  return memberLines(props, COLUMNS_PROP);
}

/** The text a row draws in its key column: `PK`, `FK`, `PK FK`, or nothing. */
export function keyText(column: ErColumn): string {
  return column.keys.join(' ');
}

/** The text a row draws in its nullability column: `NULL`, `NOT NULL`, or nothing when the line does not say. */
export function nullText(column: ErColumn): string {
  return column.nullable === undefined ? '' : column.nullable === 'null' ? 'NULL' : 'NOT NULL';
}

/** The node-local left edge of each row column that has any content; a column no row fills is absent. */
export interface ErColumnOffsets {
  key?: number;
  name: number;
  type?: number;
  nullable?: number;
}

export interface ErLayout {
  headerHeight: number;
  /** The row area under the name band: its top edge inside the box and its height. */
  body: { top: number; height: number };
  rows: ErColumn[];
  offsets: ErColumnOffsets;
  /** The node-local y of the divider between the leading primary-key rows and the rest, when there is one to draw. */
  keyDivider: number | undefined;
  /** The height the content needs; the box is exactly this tall when it was sized from its content. */
  naturalHeight: number;
  naturalWidth: number;
}

/** The row area's height for `rows` rows: an empty entity keeps one row. */
function bodyHeight(rows: number): number {
  return ER_METRICS.padY * 2 + ER_METRICS.rowHeight * Math.max(1, rows);
}

/** The aligned columns of an entity of height `h` holding `label` and the columns in `props`. */
export function layoutEntity(label: string, props: Props | undefined, h: number): ErLayout {
  const m = ER_METRICS;
  const rows = columnLines(props).map(parseColumn);
  const headerHeight = m.padY * 2 + m.rowHeight;
  const bodyNatural = bodyHeight(rows.length);
  const widest = (measure: (row: ErColumn) => number): number => rows.reduce((max, row) => Math.max(max, measure(row)), 0);
  const widths = {
    key: widest((row) => keyText(row).length) * m.charWidth * m.boldWidth,
    name: widest((row) => row.name.length) * m.charWidth,
    type: widest((row) => row.type.length) * m.charWidth,
    // Upper case is wider than the average glyph the character width is measured on, so it gets the bold factor too.
    nullable: widest((row) => nullText(row).length) * m.charWidth * m.boldWidth,
  };
  const offsets: ErColumnOffsets = { name: m.padX };
  let cursor = m.padX;
  for (const column of ['key', 'name', 'type', 'nullable'] as const) {
    if (widths[column] > 0) {
      offsets[column] = cursor;
      cursor += widths[column] + m.columnGap;
    }
  }
  const content = cursor - m.columnGap - m.padX;
  const wide = Math.max(content, 0) + m.padX * 2;
  const keyRun = rows.findIndex((row) => !row.keys.includes('PK'));
  const leadingKeys = keyRun < 0 ? rows.length : keyRun;
  return {
    headerHeight,
    body: { top: headerHeight, height: Math.max(bodyNatural, h - headerHeight) },
    rows,
    offsets,
    keyDivider: leadingKeys > 0 && leadingKeys < rows.length ? headerHeight + m.padY + m.rowHeight * leadingKeys : undefined,
    naturalHeight: headerHeight + bodyNatural,
    naturalWidth: Math.max(m.minWidth, Math.ceil(Math.max(wide, label.length * m.charWidth * m.boldWidth + m.padX * 2))),
  };
}

/** The row the name is drawn in, node-local. */
export function nameRow(w: number): Rect {
  return { x: 0, y: ER_METRICS.padY, w, h: ER_METRICS.rowHeight };
}

/** The node-local box of one part of the entity: the name band, or the whole row area. */
export function partBox(layout: ErLayout, part: ErPart, w: number): Rect {
  return part === 'name' ? { x: 0, y: 0, w, h: layout.headerHeight } : { x: 0, y: layout.body.top, w, h: layout.body.height };
}

/** Which part of the box a node-local y falls in. */
export function partAt(layout: ErLayout, localY: number): ErPart {
  return localY < layout.body.top ? 'name' : 'columns';
}

/** The geometry that fits the content: exactly as tall as it needs, and no narrower than it needs (never shrunk in width). */
export function fitEntity(label: string, props: Props | undefined, geometry: Geometry): Geometry {
  const layout = layoutEntity(label, props, 0);
  return { ...geometry, w: Math.max(geometry.w, layout.naturalWidth), h: layout.naturalHeight };
}

/** The size a shape is placed at: as large as its default columns need. */
export function defaultSize(shape: ErShape): { w: number; h: number } {
  const layout = layoutEntity(shape.defaultLabel, shape.defaultProps, 0);
  return { w: layout.naturalWidth, h: layout.naturalHeight };
}

/** A palette entry placing one ER shape. */
export interface ErOption {
  name: string;
  label: string;
  kind: string;
  shape: string;
  template: NodeTemplate;
}

/** The Entity group's entries: every shape once, sized and populated the way it is placed. */
export const ER_OPTIONS: readonly ErOption[] = ER_SHAPES.map((shape) => ({
  name: shape.name,
  label: shape.label,
  kind: shape.kind,
  shape: erShapeName(shape.name),
  template: {
    kind: shape.kind,
    shape: erShapeName(shape.name),
    label: shape.defaultLabel,
    size: defaultSize(shape),
    props: shape.defaultProps,
  },
}));
