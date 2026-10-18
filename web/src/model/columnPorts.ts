/**
 * Column ports: an ER entity's rows as connector attachment points (docs/er-entities.md `Column ports`). A row is
 * addressed by the derived port id `col:<encoded column name>` on an edge endpoint and is NEVER stored on the node: a
 * stored port is a fixed fraction of the box, which goes stale the moment a column is added, removed or reordered, so
 * the port is resolved here against the entity's own derived layout (erShapes.ts `layoutEntity`) every time it is used.
 * The canvas index and router (canvas/ports.ts, documentIndex.ts, edgeRoutes.ts), the client export and, through the
 * sidecar's router bundle, the server render all ask this one module; the C# twin is `ErShapeLibrary.ColumnPort`.
 *
 * An end attaches at the vertical middle of its row on the left or right edge of the table, whichever faces the other
 * end (`columnSideFacing`). The resolved port is an ordinary `Port` (fractions of the box), so rotation, sides and the
 * router's attachments need no special case. A name that no row carries resolves to nothing, and the caller draws the
 * end at the table exactly as an edge with no port would, never a failure.
 *
 * The id is `col:` then the name with every byte outside [A-Za-z0-9_-] written `%XX` (UTF-8, upper-case hex), so it
 * fits the schema's port pattern, the DSL's bare endpoint token and the id length limit without a quoting rule; an
 * over-long name has no id and so cannot be addressed (`columnPortId` is undefined).
 */

import type { Geometry, Id, Point, Port, Props } from './types.ts';
import { columnLines, ER_METRICS, erShape, parseColumn } from './erShapes.ts';

/** What a column port needs of a node: its shape and its props (whose `columns` are the rows). */
interface ColumnHost {
  shape: string;
  props?: Props | undefined;
}

/** The prefix that marks a port id as a column of the entity it is on. */
export const COLUMN_PORT_PREFIX = 'col:';

/** The longest id the schema admits. */
const MAX_ID_LENGTH = 128;

const SAFE = /[A-Za-z0-9_-]/;

/** Which edge of the table a column port sits on. */
export type ColumnSide = 'left' | 'right';

/** Whether a port id names a column (it may still name one the entity does not have). */
export function isColumnPortId(portId: Id | undefined): boolean {
  return portId?.startsWith(COLUMN_PORT_PREFIX) === true;
}

/** The port id addressing the column called `name`, or undefined when the name is empty or its id would exceed 128 characters. */
export function columnPortId(name: string): Id | undefined {
  if (name.length === 0) {
    return undefined;
  }
  let encoded = '';
  for (const char of name) {
    if (SAFE.test(char)) {
      encoded += char;
    } else {
      for (const byte of new TextEncoder().encode(char)) {
        encoded += `%${byte.toString(16).toUpperCase().padStart(2, '0')}`;
      }
    }
  }
  const id = COLUMN_PORT_PREFIX + encoded;
  return id.length > MAX_ID_LENGTH ? undefined : id;
}

/** The column name a `col:` port id addresses, or undefined when the id is not one (or its escapes do not decode). */
export function columnPortName(portId: Id | undefined): string | undefined {
  if (portId === undefined || !isColumnPortId(portId) || portId.length === COLUMN_PORT_PREFIX.length) {
    return undefined;
  }
  const body = portId.slice(COLUMN_PORT_PREFIX.length);
  if (/%(?![0-9A-Fa-f]{2})/.test(body)) {
    return undefined;
  }
  const bytes: number[] = [];
  for (const part of body.split(/(%[0-9A-Fa-f]{2})/)) {
    if (/^%[0-9A-Fa-f]{2}$/.test(part)) {
      bytes.push(parseInt(part.slice(1), 16));
    } else {
      bytes.push(...new TextEncoder().encode(part));
    }
  }
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(Uint8Array.from(bytes));
  } catch {
    return undefined;
  }
}

/** One column row of an entity, node-local: its name, index in the list and the vertical extent of its row. */
export interface ColumnRow {
  name: string;
  index: number;
  /** Top of the row's band and its height; the band is one `rowHeight` tall. */
  top: number;
  height: number;
  /** The row's vertical middle, where a connector attaches. */
  centre: number;
}

/** The node-local y where the first column row starts: the name band, then the row area's padding. */
function firstRowTop(): number {
  return ER_METRICS.padY * 2 + ER_METRICS.rowHeight + ER_METRICS.padY;
}

/** Whether a node is an ER entity or weak entity (the shapes that have column rows). */
export function isEntityNode(node: { shape: string }): boolean {
  return erShape(node.shape) !== undefined;
}

/** The rows of an entity in order, node-local; empty for a node that is not an entity or has no columns. */
export function columnRows(node: ColumnHost): ColumnRow[] {
  if (!isEntityNode(node)) {
    return [];
  }
  const top = firstRowTop();
  return columnLines(node.props).map((line, index) => {
    const rowTop = top + ER_METRICS.rowHeight * index;
    return { name: parseColumn(line).name, index, top: rowTop, height: ER_METRICS.rowHeight, centre: rowTop + ER_METRICS.rowHeight / 2 };
  });
}

/** The row a column port id names on a node, or undefined when the id is not a column port, the node is not an entity, or no row has that name. */
export function columnRowOf(node: ColumnHost, portId: Id | undefined): ColumnRow | undefined {
  const name = columnPortName(portId);
  return name === undefined ? undefined : columnRows(node).find((row) => row.name === name);
}

/** The side of a box that faces a point: right when it is to the right of the box's middle, else left. */
export function columnSideFacing(geometry: Geometry, towards: Point): ColumnSide {
  return towards.x >= geometry.x + geometry.w / 2 ? 'right' : 'left';
}

/**
 * The port for a column row, on `side`, as fractions of the node's box (a taller box gives its extra to the bottom of
 * the row area, so the row's y is a fixed distance from the top and its fraction moves with the height). Undefined when
 * the id names no row of this node.
 */
export function columnPort(node: ColumnHost & { geometry: Geometry }, portId: Id | undefined, side: ColumnSide): Port | undefined {
  const row = columnRowOf(node, portId);
  if (row === undefined || portId === undefined || node.geometry.h <= 0) {
    return undefined;
  }
  return { id: portId, x: side === 'left' ? 0 : 1, y: Math.min(1, row.centre / node.geometry.h) };
}

/**
 * The column port under a world point, or undefined when the point is not over the row area of an entity. Every point
 * inside the box from the name band's bottom edge down belongs to some row (the first row takes its own padding, the last
 * takes the space under it), and the side is the nearer edge; the pointer is taken into the node's own frame, so a
 * rotated entity is hit the way it is drawn.
 */
export function columnPortAt(node: ColumnHost & { geometry: Geometry }, at: Point): Port | undefined {
  const rows = columnRows(node);
  if (rows.length === 0) {
    return undefined;
  }
  const g = node.geometry;
  const radians = (-(g.rotation ?? 0) * Math.PI) / 180;
  const dx = at.x - (g.x + g.w / 2);
  const dy = at.y - (g.y + g.h / 2);
  const x = g.w / 2 + dx * Math.cos(radians) - dy * Math.sin(radians);
  const y = g.h / 2 + dx * Math.sin(radians) + dy * Math.cos(radians);
  if (x < 0 || x > g.w || y < firstRowTop() - ER_METRICS.padY || y > g.h) {
    return undefined;
  }
  const index = Math.min(rows.length - 1, Math.max(0, Math.floor((y - firstRowTop()) / ER_METRICS.rowHeight)));
  const row = rows[index];
  const name = row === undefined ? undefined : columnPortId(row.name);
  return row === undefined || name === undefined ? undefined : columnPort(node, name, x < g.w / 2 ? 'left' : 'right');
}

/**
 * The column names an edit of a column list renamed, old name to new: a name the new list lacks paired, in list order, with
 * a name the old list lacked, when the two counts match (one row retyped, or several at once). Adding, removing or
 * reordering rows renames nothing; a name that stays anywhere in the list stays as it is. A mismatched count is
 * ambiguous (a row was also added or removed), so it renames nothing and an edge on a vanished column falls back to the table.
 */
export function columnRenames(before: readonly string[], after: readonly string[]): Map<string, string> {
  const namesOf = (lines: readonly string[]): string[] => lines.map((line) => parseColumn(line).name);
  const oldNames = namesOf(before);
  const newNames = namesOf(after);
  const removed = oldNames.filter((name) => !newNames.includes(name));
  const added = newNames.filter((name) => !oldNames.includes(name));
  const renames = new Map<string, string>();
  if (removed.length === added.length) {
    removed.forEach((name, i) => {
      const next = added[i];
      if (next !== undefined && next !== name) {
        renames.set(name, next);
      }
    });
  }
  return renames;
}
