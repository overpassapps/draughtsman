/**
 * The BPMN 2.0 shape library, read from the same embedded file the C# side reads
 * (src/Draughtsman.Core/Schemas/bpmn-shapes.json), so the palette, the DSL and both renderers agree on the set.
 *
 * A shape is placed as the presentation shape `custom:bpmn/<name>`, a branch the document schema already admits, and
 * names its own flowchart kind `bpmn-<name>` whose default shape it is, so the DSL prints by kind and get_schema
 * teaches an agent when to use it. Events, activities and gateways are typed the way the generic icons are: a native
 * outline (canvas/shapes.tsx `bpmnRender`, SvgRenderer.cs `BpmnMarkup`) plus an inner glyph whose line art is data in
 * that file. Pools and lanes are not here; they are model/swimlanes.ts. Nothing here names a colour.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/bpmn-shapes.json' with { type: 'json' };
import type { Rect } from '../canvas/geometry.ts';
import { rotatedBounds } from '../canvas/geometry.ts';
import type { GenericIconElement } from './icons.ts';
import type { NodeTemplate } from './create.ts';
import type { Geometry } from './types.ts';

export type BpmnFamily = 'event' | 'activity' | 'gateway' | 'data' | 'annotation';

/** A glyph primitive: an icon element plus the two flags a filled (throwing) glyph reads. */
export interface BpmnGlyphElement extends GenericIconElement {
  /** Always filled with the stroke colour, throwing or not. */
  solid?: boolean;
  /** Drawn in the node's fill colour when the glyph is filled, so an envelope's flap still reads. */
  detail?: boolean;
  /** Any other SVG presentation attribute of the primitive, copied through (`stroke-width`). */
  [attribute: string]: unknown;
}

export interface BpmnShape {
  name: string;
  label: string;
  /** The flowchart kind this shape is the default shape for: `bpmn-<name>`. */
  kind: string;
  family: BpmnFamily;
  /** Events only: where in a process the event sits. */
  position?: 'start' | 'intermediate' | 'end';
  /** single (start event), double (intermediate event) or thick (end event, call activity). */
  border: 'single' | 'double' | 'thick';
  /** The glyph drawn inside, a key of `glyphs`. */
  glyph?: string;
  placement?: 'centre' | 'corner' | 'bottom';
  /** The glyph is filled: a throw event or a send task. */
  throwing?: boolean;
  /** The size the palette places the shape at. */
  size: { w: number; h: number };
}

/** The numbers both renderers share: label placement, border weight and glyph and marker sizes. */
export interface BpmnLayout {
  labelGap: number;
  labelBand: number;
  labelMinWidth: number;
  eventRingInset: number;
  thickBorder: number;
  eventGlyph: number;
  gatewayGlyph: number;
  markerSize: number;
  markerInset: number;
  cornerRadius: number;
  dataFold: number;
  dataFoldMax: number;
  annotationBracket: number;
  annotationBracketMax: number;
}

interface BpmnShapeFile {
  library: string;
  description: string;
  viewBox: number;
  layout: BpmnLayout;
  glyphs: Record<string, BpmnGlyphElement[]>;
  shapes: BpmnShape[];
}

const file = raw as unknown as BpmnShapeFile;

/** The shape-name prefix every BPMN shape carries: `custom:bpmn/<name>`. */
export const BPMN_SHAPE_PREFIX = 'custom:bpmn/';

/** The 24x24 grid every glyph is authored on. */
export const BPMN_VIEWBOX = file.viewBox;

export const BPMN_LAYOUT: Readonly<BpmnLayout> = file.layout;

export const BPMN_GLYPHS: Readonly<Record<string, readonly BpmnGlyphElement[]>> = file.glyphs;

/** Every shape, in the library's own order (the palette's order). */
export const BPMN_SHAPES: readonly BpmnShape[] = file.shapes;

/** The full shape name for a library entry: `custom:bpmn/<name>`. */
export function bpmnShapeName(name: string): string {
  return BPMN_SHAPE_PREFIX + name;
}

const SHAPES_BY_NAME = new Map(BPMN_SHAPES.map((shape) => [bpmnShapeName(shape.name), shape]));

/** The library entry a `custom:bpmn/<name>` shape names, or undefined for any other shape. */
export function bpmnShape(shape: string): BpmnShape | undefined {
  return SHAPES_BY_NAME.get(shape);
}

/** Whether a shape draws its label in a band below the outline (events, gateways, data) rather than inside it. */
export function labelOutside(shape: BpmnShape): boolean {
  return shape.family === 'event' || shape.family === 'gateway' || shape.family === 'data';
}

/** The annotation bracket's width for a box of width `w`. Mirrors SvgRenderer.cs `BpmnBracket`. */
export function bpmnBracket(w: number): number {
  return Math.min(w * BPMN_LAYOUT.annotationBracket, BPMN_LAYOUT.annotationBracketMax);
}

/**
 * The world-space rectangle a node's outside label can occupy, for fit-to-content and export cropping, or undefined for
 * a shape that keeps its label inside. A rotated node gets the box that covers the label wherever the rotation puts
 * it, as the server renderer's bounds do.
 */
export function outsideLabelBounds(shape: string, geometry: Geometry): Rect | undefined {
  const bpmn = bpmnShape(shape);
  if (bpmn === undefined || !labelOutside(bpmn)) {
    return undefined;
  }
  const { labelGap, labelBand, labelMinWidth } = BPMN_LAYOUT;
  const labelWidth = Math.max(geometry.w, labelMinWidth);
  if ((geometry.rotation ?? 0) % 360 === 0) {
    return { x: geometry.x - (labelWidth - geometry.w) / 2, y: geometry.y + geometry.h, w: labelWidth, h: labelGap + labelBand };
  }
  const reach = geometry.h / 2 + labelGap + labelBand;
  return rotatedBounds({ x: geometry.x + geometry.w / 2 - labelWidth / 2, y: geometry.y + geometry.h / 2 - reach, w: labelWidth, h: reach * 2, rotation: geometry.rotation ?? 0 });
}

/**
 * The shape panel's BPMN group: every shape once, placed as its own flowchart kind at the size the library gives it (a
 * circle for an event, a square for a gateway), since these outlines fill their box and only read right when square.
 */
export const BPMN_OPTIONS: readonly NodeTemplate[] = BPMN_SHAPES.map((shape) => ({
  kind: shape.kind,
  shape: bpmnShapeName(shape.name),
  label: shape.label,
  size: shape.size,
}));
