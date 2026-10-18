/**
 * The size an ER entity needs when a node arrives without one (a DSL or agent-written table has no geometry): a name
 * band plus one row per column, as wide as the aligned columns need. This mirrors `layoutEntity` in
 * web/src/model/erShapes.ts and `ErShapeLibrary.LayoutEntity` in Draughtsman.Core; the metrics and the column line
 * parser are copied here because the sidecar image is built from sidecar/ alone, and test/erSize.test.ts fails if the
 * metrics drift from src/Draughtsman.Core/Schemas/er-shapes.json or the parser from that file's examples.
 */
import type { Node } from '../model/types.ts';

export const ER_SHAPE_PREFIX = 'custom:er/';

export const ER_METRICS = { rowHeight: 18, padX: 8, padY: 5, charWidth: 7.5, boldWidth: 1.1, columnGap: 12, minWidth: 120 } as const;

/** The shapes of the library, by name (entity and weak-entity share one size rule). */
const ER_SHAPE_NAMES: readonly string[] = ['entity', 'weak-entity'];

export interface ErColumn {
  keys: ('PK' | 'FK')[];
  name: string;
  type: string;
  nullable: 'null' | 'notnull' | undefined;
}

const KEY_MARKER = /^(PK|FK)[ \t]+(?=[^ \t])/i;
const NOT_NULL = /(?:^|[ \t])not[ \t]+null$/i;
const NULL = /(?:^|[ \t])null$/i;

/** Splits a column line into what is drawn; the grammar is er-shapes.json's `description`. */
export function parseColumn(line: string): ErColumn {
  let rest = line.trim();
  const found = new Set<'PK' | 'FK'>();
  for (;;) {
    const marker = KEY_MARKER.exec(rest);
    const key = marker?.[1]?.toUpperCase() as 'PK' | 'FK' | undefined;
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
  let nullable: ErColumn['nullable'];
  const notNull = NOT_NULL.exec(rest);
  const tail = notNull ?? NULL.exec(rest);
  if (tail !== null) {
    nullable = notNull === null ? 'null' : 'notnull';
    rest = rest.slice(0, tail.index).trim();
  }
  return { keys: (['PK', 'FK'] as const).filter((key) => found.has(key)), name, type: rest.replace(/[ \t]+/g, ' '), nullable };
}

function columns(props: Node['props']): ErColumn[] {
  const list = props?.['columns'];
  if (!Array.isArray(list)) {
    return [];
  }
  return list.filter((item): item is string => typeof item === 'string' && item.trim().length > 0).map(parseColumn);
}

/** The natural size of a `custom:er/<name>` node, or undefined when the node is not an ER shape. */
export function erSizeOf(node: Node): { w: number; h: number } | undefined {
  if (!node.shape.startsWith(ER_SHAPE_PREFIX) || !ER_SHAPE_NAMES.includes(node.shape.slice(ER_SHAPE_PREFIX.length))) {
    return undefined;
  }
  const m = ER_METRICS;
  const rows = columns(node.props);
  const widest = (measure: (row: ErColumn) => number): number => rows.reduce((max, row) => Math.max(max, measure(row)), 0);
  const widths = [
    widest((row) => row.keys.join(' ').length) * m.charWidth * m.boldWidth,
    widest((row) => row.name.length) * m.charWidth,
    widest((row) => row.type.length) * m.charWidth,
    widest((row) => (row.nullable === undefined ? 0 : row.nullable === 'null' ? 4 : 8)) * m.charWidth * m.boldWidth,
  ].filter((width) => width > 0);
  const content = widths.reduce((sum, width) => sum + width, 0) + m.columnGap * Math.max(0, widths.length - 1);
  const needed = Math.max(content + m.padX * 2, node.label.length * m.charWidth * m.boldWidth + m.padX * 2);
  return { w: Math.max(m.minWidth, Math.ceil(needed)), h: m.padY * 2 + m.rowHeight + (m.padY * 2 + m.rowHeight * Math.max(1, rows.length)) };
}
