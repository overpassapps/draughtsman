/**
 * The general-purpose shape library (ellipse, triangle, trapezoid, chevron, cross, arrows, note, callout, brace, cube, folder,
 * component), read from the same embedded file the C# renderer reads (src/Draughtsman.Core/Schemas/basic-shapes.json), so the
 * web canvas and the server SVG renderer cannot drift.
 *
 * A shape is placed as the presentation shape `custom:basic/<name>`, a branch the document schema already admits, so the
 * library needs no schema change and no kind of its own: the palette places it on an existing kind for the document type
 * (`basicKind`). Everything about a shape is data: its outline is path data in the node's box, resolved here to absolute
 * coordinates; `resolvePath` is mirrored by BasicShapeGeometry in Draughtsman.Core, and a test on each side pins the same literal output.
 *
 * A coordinate is a fraction of the box's extent on its axis, optionally plus or minus a fixed length in pixels (`1-16`, `0.5+4`);
 * the fixed length never exceeds half the extent, so a box smaller than the shape's features squeezes them rather than
 * turning the outline inside out.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/basic-shapes.json' with { type: 'json' };
import type { NodeTemplate } from './create.ts';
import type { DocumentType, Port } from './types.ts';

/** One drawn part of a shape: path data in the shape's coordinates, and whether the node's fill paints it too. */
export interface BasicShapePart {
  d: string;
  fill?: boolean;
}

export interface BasicShape {
  name: string;
  label: string;
  /** Other names the shape panel's search matches (a use case is an ellipse, a package a folder). */
  keywords?: string[];
  /** A mark that comments on the diagram rather than taking part in it; the palette places it on the note kind. */
  annotation?: boolean;
  /** The box the palette places the shape at. */
  size: { w: number; h: number };
  /** The first part is the outline; every later one is a detail. */
  parts: BasicShapePart[];
  /** The label's rectangle as [left, top, right, bottom] coordinate tokens. */
  labelBox: [string, string, string, string];
  /** Where a connector lands, by side id, as fractions of the box. */
  ports: Record<string, [number, number]>;
}

interface BasicShapeFile {
  library: string;
  description: string;
  shapes: BasicShape[];
}

const file = raw as unknown as BasicShapeFile;

/** The shape-name prefix every basic shape carries: `custom:basic/<name>`. */
export const BASIC_SHAPE_PREFIX = 'custom:basic/';

/** Every shape, in the library's own order (the palette's order). */
export const BASIC_SHAPES: readonly BasicShape[] = file.shapes;

/** The full shape name for a library entry: `custom:basic/<name>`. */
export function basicShapeName(name: string): string {
  return BASIC_SHAPE_PREFIX + name;
}

/** The library entry a `custom:basic/<name>` shape names, or undefined for any other shape. */
export function basicShape(shape: string): BasicShape | undefined {
  return shape.startsWith(BASIC_SHAPE_PREFIX) ? BASIC_SHAPES.find((candidate) => candidate.name === shape.slice(BASIC_SHAPE_PREFIX.length)) : undefined;
}

/** The longest fixed length a coordinate may add, as a fraction of the box's extent on that axis. */
export const FIXED_LENGTH_LIMIT = 0.5;

/** Two decimals, half up, so attribute strings stay short; the same rounding the C# twin does by hand. */
function round(value: number): number {
  return Math.round(value * 100) / 100;
}

/** The index of the sign that starts a token's fixed length, or -1 for a bare fraction. A leading sign belongs to the fraction. */
function signIndex(token: string): number {
  for (let i = 1; i < token.length; i++) {
    if (token[i] === '+' || token[i] === '-') {
      return i;
    }
  }
  return -1;
}

/** The value of one coordinate token on an axis whose extent is `extent` (the box's width for x, its height for y). */
export function resolveCoordinate(token: string, extent: number): number {
  const split = signIndex(token);
  const fraction = Number(split < 0 ? token : token.slice(0, split));
  let value = fraction * extent;
  if (split >= 0) {
    const fixedLength = Math.min(Number(token.slice(split + 1)), extent * FIXED_LENGTH_LIMIT);
    value = token[split] === '-' ? value - fixedLength : value + fixedLength;
  }
  return value;
}

/** The label rectangle of a shape in a box of the given size, relative to the box's top-left. */
export function resolveBox(tokens: readonly [string, string, string, string], w: number, h: number): { x: number; y: number; w: number; h: number } {
  const left = resolveCoordinate(tokens[0], w);
  const top = resolveCoordinate(tokens[1], h);
  return { x: left, y: top, w: Math.max(0, resolveCoordinate(tokens[2], w) - left), h: Math.max(0, resolveCoordinate(tokens[3], h) - top) };
}

const COMMAND_ARITY: Record<string, number> = { M: 2, L: 2, C: 6, H: 1, V: 1, Z: 0 };

/**
 * A part's path data resolved for a box of `w` by `h` with its top-left at the origin. Only the commands M, L, C, H, V and Z
 * are understood; anything else throws, so a typo in the library fails its test instead of drawing half an outline.
 */
export function resolvePath(d: string, w: number, h: number): string {
  const tokens = d.replace(/([A-Za-z])/g, ' $1 ').split(/[\s,]+/).filter((token) => token.length > 0);
  const out: string[] = [];
  let command: string | undefined;
  let numbers: string[] = [];
  const flush = (): void => {
    if (command === undefined) {
      return;
    }
    const arity = COMMAND_ARITY[command];
    if (arity === undefined || (arity === 0 ? numbers.length !== 0 : numbers.length === 0 || numbers.length % arity !== 0)) {
      throw new Error(`Path command ${command} takes ${String(arity)} numbers, found ${String(numbers.length)}.`);
    }
    if (command === 'Z') {
      out.push('Z');
    }
    for (let start = 0; arity > 0 && start < numbers.length; start += arity) {
      if (command === 'H') {
        out.push(`H${String(round(resolveCoordinate(numbers[start] ?? '0', w)))}`);
      } else if (command === 'V') {
        out.push(`V${String(round(resolveCoordinate(numbers[start] ?? '0', h)))}`);
      } else {
        const points: string[] = [];
        for (let i = 0; i < arity; i += 2) {
          points.push(`${String(round(resolveCoordinate(numbers[start + i] ?? '0', w)))},${String(round(resolveCoordinate(numbers[start + i + 1] ?? '0', h)))}`);
        }
        out.push(`${command}${points.join(' ')}`);
      }
    }
    numbers = [];
  };
  for (const token of tokens) {
    if (/^[A-Za-z]$/.test(token)) {
      flush();
      command = token;
    } else {
      numbers.push(token);
    }
  }
  flush();
  return out.join(' ');
}

/** The ports a shape declares, as the model's `Port` records, in the library's order. */
export function basicPorts(shape: BasicShape): readonly Port[] {
  return Object.entries(shape.ports).map(([id, [x, y]]) => ({ id, x, y }));
}

/**
 * The kind the palette places a basic shape with in a document of `type`: an annotation (note, callout, brace) goes on
 * the `note` kind, anything else on the type's plain box kind (`process` in a flowchart, `service` in an architecture
 * diagram, `shape` in freeform, which has no vocabulary), so dropping one never trips the unknown-kind lint.
 */
export function basicKind(shape: BasicShape, type: DocumentType): string {
  if (shape.annotation === true) {
    return 'note';
  }
  return type === 'flowchart' ? 'process' : type === 'architecture' ? 'service' : 'shape';
}

/** The template that places one basic shape in a document of `type`: its own size, on the kind `basicKind` names. */
export function basicTemplate(shape: BasicShape, type: DocumentType): NodeTemplate {
  return { kind: basicKind(shape, type), shape: basicShapeName(shape.name), label: shape.label, size: { w: shape.size.w, h: shape.size.h } };
}
