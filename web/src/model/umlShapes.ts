/**
 * The UML class shape library, read from the same embedded file the C# renderer and the layout sidecar read
 * (src/Draughtsman.Core/Schemas/uml-shapes.json), so the web canvas, the server SVG renderer and the layout cannot
 * drift. docs/uml-class-compartments.md is the note on the model this file implements.
 *
 * A class or interface is ONE node: its label is the name, and its members are the node's `props.attributes` and
 * `props.operations`, arrays of strings with one member per string. A member's leading `+ - # ~` is its visibility
 * glyph, so plain text round-trips. The compartments are not stored: `layoutClass` derives them from that content
 * (name rows, then one row per member, an empty compartment keeping a single row), and a box taller than its content
 * gives the extra to the operations compartment. `fitClass` is the size that content needs. Lollipop and socket are
 * small fixed symbols. Mirrors `UmlShapeLibrary` in Draughtsman.Core.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/uml-shapes.json' with { type: 'json' };
import type { Rect } from '../canvas/geometry.ts';
import type { NodeTemplate } from './create.ts';
import type { Geometry, Props } from './types.ts';

export interface UmlMetrics {
  fontSize: number;
  rowHeight: number;
  padX: number;
  padY: number;
  charWidth: number;
  glyphColumn: number;
  minWidth: number;
  symbolBand: number;
  symbolRadius: number;
}

export type UmlForm = 'compartments' | 'symbol';

export interface UmlShape {
  name: string;
  label: string;
  /** The architecture kind this shape is the default shape for. */
  kind: string;
  form: UmlForm;
  defaultLabel: string;
  /** The stereotype line the shape shows by default; empty for none. */
  stereotype: string;
  defaultSize?: { w: number; h: number };
  defaultProps?: Props;
}

interface UmlShapeFile {
  library: string;
  description: string;
  metrics: UmlMetrics;
  shapes: UmlShape[];
}

const file = raw as unknown as UmlShapeFile;

/** The shape-name prefix every UML shape carries: `custom:uml/<name>`. */
export const UML_SHAPE_PREFIX = 'custom:uml/';

export const ATTRIBUTES_PROP = 'attributes';
export const OPERATIONS_PROP = 'operations';
export const STEREOTYPE_PROP = 'stereotype';

export const UML_METRICS: UmlMetrics = file.metrics;

/** Every shape, in the library's own order (the palette's order). */
export const UML_SHAPES: readonly UmlShape[] = file.shapes;

/** The shape name for a UML shape: `custom:uml/<name>`. */
export function umlShapeName(name: string): string {
  return UML_SHAPE_PREFIX + name;
}

/** The shape a `custom:uml/<name>` presentation names, or undefined when it is not in the library. */
export function umlShape(shape: string): UmlShape | undefined {
  return shape.startsWith(UML_SHAPE_PREFIX) ? UML_SHAPES.find((candidate) => candidate.name === shape.slice(UML_SHAPE_PREFIX.length)) : undefined;
}

/** The parts of a class box a person edits: the name (the node label) and the two member lists. */
export type UmlPart = 'name' | 'attributes' | 'operations';

/** The visibility glyphs, with what each means; `~` is package visibility. */
export const VISIBILITY_GLYPHS: Readonly<Record<string, string>> = { '+': 'public', '-': 'private', '#': 'protected', '~': 'package' };

/** One class member as drawn: its visibility glyph (empty when the line has none) and the text after it. */
export interface UmlMember {
  visibility: string;
  text: string;
}

/** Splits a member line: `+ save()` is `{ '+', 'save()' }`, `id: int` is `{ '', 'id: int' }`. */
export function parseMember(line: string): UmlMember {
  const trimmed = line.trim();
  const first = trimmed.charAt(0);
  return first !== '' && Object.hasOwn(VISIBILITY_GLYPHS, first) ? { visibility: first, text: trimmed.slice(1).trimStart() } : { visibility: '', text: trimmed };
}

/** The non-blank strings of a props list (`attributes`, `operations`, or an entity's `columns`), in order; any other value in the list, or a non-list, is ignored. */
export function memberLines(props: Props | undefined, key: string): string[] {
  const list = props?.[key];
  return Array.isArray(list) ? list.filter((item): item is string => typeof item === 'string' && item.trim().length > 0) : [];
}

/** The stereotype a box shows above its name: `props.stereotype` when set (empty hides it), else the shape's own. */
export function stereotypeOf(shape: UmlShape, props: Props | undefined): string | undefined {
  const custom = props?.[STEREOTYPE_PROP];
  const text = typeof custom === 'string' ? custom.trim() : shape.stereotype;
  return text.length === 0 ? undefined : text;
}

/** A compartment as laid out: its top edge inside the box, its height, and the members it draws. */
export interface UmlCompartment {
  top: number;
  height: number;
  members: UmlMember[];
  /** Whether any member has a glyph; if so every member's text sits after the glyph column so the names line up. */
  hasGlyph: boolean;
}

export interface UmlClassLayout {
  stereotype: string | undefined;
  headerHeight: number;
  attributes: UmlCompartment;
  operations: UmlCompartment;
  /** The height the content needs; the box is exactly this tall when it was sized from its content. */
  naturalHeight: number;
  naturalWidth: number;
}

function compartmentHeight(members: number): number {
  return UML_METRICS.padY * 2 + UML_METRICS.rowHeight * Math.max(1, members);
}

/** The compartments of a class or interface box of height `h` holding `label` and the members in `props`. */
export function layoutClass(shape: UmlShape, label: string, props: Props | undefined, h: number): UmlClassLayout {
  const m = UML_METRICS;
  const stereotype = stereotypeOf(shape, props);
  const headerHeight = m.padY * 2 + m.rowHeight * (stereotype === undefined ? 1 : 2);
  const attributes = memberLines(props, ATTRIBUTES_PROP).map(parseMember);
  const operations = memberLines(props, OPERATIONS_PROP).map(parseMember);
  const attributesHeight = compartmentHeight(attributes.length);
  const operationsNatural = compartmentHeight(operations.length);
  const hasGlyph = (members: UmlMember[]): boolean => members.some((member) => member.visibility !== '');

  let widest = label.length * m.charWidth * 1.1 + m.padX * 2;
  if (stereotype !== undefined) {
    widest = Math.max(widest, (stereotype.length + 4) * m.charWidth + m.padX * 2);
  }
  for (const members of [attributes, operations]) {
    const column = hasGlyph(members) ? m.glyphColumn : 0;
    for (const member of members) {
      widest = Math.max(widest, m.padX * 2 + column + member.text.length * m.charWidth);
    }
  }
  return {
    stereotype,
    headerHeight,
    attributes: { top: headerHeight, height: attributesHeight, members: attributes, hasGlyph: hasGlyph(attributes) },
    operations: { top: headerHeight + attributesHeight, height: Math.max(operationsNatural, h - headerHeight - attributesHeight), members: operations, hasGlyph: hasGlyph(operations) },
    naturalHeight: headerHeight + attributesHeight + operationsNatural,
    naturalWidth: Math.max(m.minWidth, Math.ceil(widest)),
  };
}

/** A shape that has compartments: a class or interface. */
export function isClassShape(shape: UmlShape | undefined): shape is UmlShape {
  return shape?.form === 'compartments';
}

/** The row the name is drawn in, node-local; a stereotype row sits above it. */
export function nameRow(layout: UmlClassLayout, w: number): Rect {
  const m = UML_METRICS;
  return { x: 0, y: m.padY + (layout.stereotype === undefined ? 0 : m.rowHeight), w, h: m.rowHeight };
}

/** The node-local box of one part of the class: the whole name band, or a member compartment. */
export function partBox(layout: UmlClassLayout, part: UmlPart, w: number): Rect {
  if (part === 'name') {
    return { x: 0, y: 0, w, h: layout.headerHeight };
  }
  const compartment = part === 'attributes' ? layout.attributes : layout.operations;
  return { x: 0, y: compartment.top, w, h: compartment.height };
}

/** Which part of the box a node-local y falls in: above the attributes is the name band, at or below the operations line is the operations. */
export function partAt(layout: UmlClassLayout, localY: number): UmlPart {
  if (localY < layout.attributes.top) {
    return 'name';
  }
  return localY < layout.operations.top ? 'attributes' : 'operations';
}

/** The geometry that fits the content: exactly as tall as it needs, and no narrower than it needs (never shrunk in width). */
export function fitClass(shape: UmlShape, label: string, props: Props | undefined, geometry: Geometry): Geometry {
  const layout = layoutClass(shape, label, props, 0);
  return { ...geometry, w: Math.max(geometry.w, layout.naturalWidth), h: layout.naturalHeight };
}

/** The size a shape is placed at: a class or interface as large as its default members need, a symbol its own default. */
export function defaultSize(shape: UmlShape): { w: number; h: number } {
  if (shape.form === 'symbol' && shape.defaultSize !== undefined) {
    return shape.defaultSize;
  }
  const layout = layoutClass(shape, shape.defaultLabel, shape.defaultProps, 0);
  return { w: layout.naturalWidth, h: layout.naturalHeight };
}

/** The fraction of a lollipop or socket's height the symbol fills; its label sits in the band below. */
export function symbolBandHeight(h: number): number {
  return h * UML_METRICS.symbolBand;
}

/** A palette entry placing one UML shape. */
export interface UmlOption {
  name: string;
  label: string;
  kind: string;
  shape: string;
  template: NodeTemplate;
}

/** The UML group's entries: every shape once, sized and populated the way it is placed. */
export const UML_OPTIONS: readonly UmlOption[] = UML_SHAPES.map((shape) => ({
  name: shape.name,
  label: shape.label,
  kind: shape.kind,
  shape: umlShapeName(shape.name),
  template: {
    kind: shape.kind,
    shape: umlShapeName(shape.name),
    label: shape.defaultLabel,
    size: defaultSize(shape),
    ...(shape.defaultProps === undefined ? {} : { props: shape.defaultProps }),
  },
}));
