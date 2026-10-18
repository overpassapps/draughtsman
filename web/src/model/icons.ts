/**
 * The generic-technology icon library, read from the same embedded file the C# renderer reads
 * (src/Draughtsman.Core/Schemas/generic-icons.json), so the web canvas and the server SVG renderer cannot drift.
 *
 * An icon is placed as the presentation shape `custom:generic/<name>` (canvas/shapes.tsx draws it, its server twin is
 * SvgRenderer.cs). Every icon names a real architecture kind as its `paletteKind`, so the palette places it with a
 * kind the vocabulary knows (never the unknown `shape` placeholder, which would trip the lint); an icon that also
 * carries a distinct meaning names an architecture kind as its own `defaultShape` too. Everything here is data; the
 * icon set is drawn from scratch and uses the diagram theme's stroke and text tokens, never a literal.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/generic-icons.json' with { type: 'json' };

/** One SVG primitive of an icon, on the library's viewBox grid. Only the fields its `tag` needs are present. */
export interface GenericIconElement {
  tag: 'path' | 'rect' | 'circle' | 'ellipse' | 'line' | 'polygon' | 'polyline';
  d?: string;
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  /** Corner radius (rect) or x-radius (ellipse). */
  rx?: number;
  ry?: number;
  cx?: number;
  cy?: number;
  r?: number;
  x1?: number;
  y1?: number;
  x2?: number;
  y2?: number;
  points?: string;
}

export interface GenericIcon {
  name: string;
  label: string;
  /** The architecture kind this icon is the default shape for, or null when it is presentation only. */
  kind: string | null;
  /** The real architecture kind the palette places the icon with, so dropping it never trips the unknown-kind lint. */
  paletteKind: string;
  /** True for a container boundary (VPC, subnet, availability zone), drawn as a labelled box with the artwork inset. */
  boundary?: boolean;
  elements: GenericIconElement[];
}

interface GenericIconFile {
  library: string;
  viewBox: number;
  description: string;
  icons: GenericIcon[];
}

const file = raw as unknown as GenericIconFile;

/** The shape-name prefix every generic icon carries: `custom:generic/<name>`. */
export const GENERIC_ICON_PREFIX = 'custom:generic/';

/** The 24x24 coordinate grid every icon's primitives are authored on. */
export const GENERIC_ICON_VIEWBOX = file.viewBox;

/** Every icon, in the library's own order (the palette's order). */
export const GENERIC_ICONS: readonly GenericIcon[] = file.icons;

/** The shape name for an icon: `custom:generic/<name>`. */
export function genericIconShape(name: string): string {
  return GENERIC_ICON_PREFIX + name;
}

/** The icon a `custom:generic/<name>` shape names, or undefined when the shape is not in the library. */
export function genericIcon(name: string): GenericIcon | undefined {
  return GENERIC_ICONS.find((icon) => icon.name === name);
}

/** The icon a shape names, or undefined for any other shape (including an unknown `custom:generic/*` one). */
export function iconForShape(shape: string): GenericIcon | undefined {
  return shape.startsWith(GENERIC_ICON_PREFIX) ? genericIcon(shape.slice(GENERIC_ICON_PREFIX.length)) : undefined;
}

/** The fraction of an icon node's box height the label sits in; the icon fills the band above it (canvas/shapes.tsx). */
export const ICON_LABEL_BAND = 0.7;
/** How much of the icon band the icon's square fills, leaving it a margin (canvas/shapes.tsx). */
export const ICON_SIZE_RATIO = 0.8;
/**
 * The size a palette-placed icon lands at: nearly square (a 160 x 80 box leaves the glyph, a square in the top band, 57 px
 * from each side), and wide enough for a two-word label like "API gateway" on one line. A connector that attaches to the
 * box is then close to the glyph (QA P2). Resized, an icon node is still a valid box; its connection points then sit a
 * little off the drawing.
 */
export const ICON_PLACE_SIZE = { w: 120, h: 96 } as const;

/**
 * Where a connector meets an icon node: on the visible glyph, not the invisible box around it (QA P2). Fractions of the
 * box for a node at its placed size (canvas/shapes.tsx draws the glyph as a square of `ICON_SIZE_RATIO` of the shorter of
 * the box's width and its top band, centred in that band): the glyph's top edge, its left and right edges at the band's
 * mid-height, and the foot of the box below the label. A boundary icon (VPC, subnet) is a labelled box and keeps the side
 * midpoints.
 */
export function iconPortFractions(size: { w: number; h: number } = ICON_PLACE_SIZE): { id: 'top' | 'right' | 'bottom' | 'left'; x: number; y: number }[] {
  const band = size.h * ICON_LABEL_BAND;
  const side = Math.min(size.w, band) * ICON_SIZE_RATIO;
  const inset = (size.w - side) / 2 / size.w;
  return [
    { id: 'top', x: 0.5, y: (band - side) / 2 / size.h },
    { id: 'right', x: 1 - inset, y: band / 2 / size.h },
    { id: 'bottom', x: 0.5, y: 1 },
    { id: 'left', x: inset, y: band / 2 / size.h },
  ];
}

/** A palette entry placing one icon. */
export interface IconOption {
  name: string;
  label: string;
  kind: string;
  shape: string;
  /** The size it lands at; absent for a boundary icon, which keeps the everyday box. */
  size?: { w: number; h: number };
}

/** The Icons group's entries: every icon once, with the real kind the palette places it with. */
export const ICON_OPTIONS: readonly IconOption[] = GENERIC_ICONS.map((icon) => ({
  name: icon.name,
  label: icon.label,
  kind: icon.paletteKind,
  shape: genericIconShape(icon.name),
  ...(icon.boundary === true ? {} : { size: { ...ICON_PLACE_SIZE } }),
}));
