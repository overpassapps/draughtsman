/**
 * The org-chart box library, read from the same embedded file the C# renderer reads
 * (src/Draughtsman.Core/Schemas/org-shapes.json), so the web canvas and the server SVG renderer cannot drift.
 *
 * A box is placed as the presentation shape `custom:org/<name>` (canvas/shapes.tsx draws it, its server twin is
 * SvgRenderer.cs). Each box names an architecture kind it is the default shape for, so the palette places it with a
 * kind the vocabulary knows; `defaultLabel` carries the two lines (name and title) a role box shows, since the label
 * is the only free text the model gives a node. Everything here is data; the box chrome is drawn from the resolved
 * node style and names no literal colour.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/org-shapes.json' with { type: 'json' };

/** One decorative primitive of a box, in fractions of the node box (0..1 on each axis). */
export interface OrgShapeElement {
  tag: 'line';
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

/** How a box's outline is drawn: a single stroke, an inset second stroke, or a dashed single stroke. */
export type OrgOutline = 'solid' | 'double' | 'dashed';

export interface OrgShape {
  name: string;
  label: string;
  /** The architecture kind this box is the default shape for. */
  kind: string;
  /** The two-line label (name and title) a palette glyph places the box with. */
  defaultLabel: string;
  outline: OrgOutline;
  elements: OrgShapeElement[];
}

interface OrgShapeFile {
  library: string;
  description: string;
  shapes: OrgShape[];
}

const file = raw as unknown as OrgShapeFile;

/** The shape-name prefix every org box carries: `custom:org/<name>`. */
export const ORG_SHAPE_PREFIX = 'custom:org/';

/** Every box, in the library's own order (the palette's order). */
export const ORG_SHAPES: readonly OrgShape[] = file.shapes;

/** The shape name for a box: `custom:org/<name>`. */
export function orgShapeName(name: string): string {
  return ORG_SHAPE_PREFIX + name;
}

/** The box a `custom:org/<name>` shape names, or undefined when the shape is not in the library. */
export function orgShape(shape: string): OrgShape | undefined {
  return shape.startsWith(ORG_SHAPE_PREFIX) ? ORG_SHAPES.find((box) => box.name === shape.slice(ORG_SHAPE_PREFIX.length)) : undefined;
}

/** A palette entry placing one org box. */
export interface OrgOption {
  name: string;
  label: string;
  kind: string;
  defaultLabel: string;
  shape: string;
}

/** The Org chart group's entries: every box once, with the architecture kind the palette places it with. */
export const ORG_OPTIONS: readonly OrgOption[] = ORG_SHAPES.map((box) => ({
  name: box.name,
  label: box.label,
  kind: box.kind,
  defaultLabel: box.defaultLabel,
  shape: orgShapeName(box.name),
}));
