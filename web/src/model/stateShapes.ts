/**
 * The UML state-machine shape library, read from the same embedded file the C# side reads
 * (src/Draughtsman.Core/Schemas/state-shapes.json), so the palette, the DSL and both renderers agree on the set.
 *
 * A shape is placed as the presentation shape `custom:state/<name>`, a branch the document schema already admits, so
 * the library needs no schema change. Every shape names its own flowchart kind, whose default shape is that shape, so
 * the DSL printer elides it and get_schema teaches an agent when to use it. The drawing itself is native in both
 * renderers (canvas/shapes.tsx and SvgRenderer.cs), swept against this file's names by a test on each side.
 */

import raw from '../../../src/Draughtsman.Core/Schemas/state-shapes.json' with { type: 'json' };
import type { NodeTemplate } from './create.ts';

export interface StateShape {
  name: string;
  label: string;
  /** The flowchart kind this shape is the default shape for. */
  kind: string;
  /** The kind the palette places it with; the same as `kind`, so dropping one never trips the unknown-kind lint. */
  paletteKind: string;
}

interface StateShapeFile {
  library: string;
  description: string;
  shapes: StateShape[];
}

const file: StateShapeFile = raw;

/** The shape-name prefix every state shape carries: `custom:state/<name>`. */
export const STATE_SHAPE_PREFIX = 'custom:state/';

/** Every shape, in the library's own order (the palette's order). */
export const STATE_SHAPES: readonly StateShape[] = file.shapes;

/** The full shape name for a library entry: `custom:state/<name>`. */
export function stateShapeName(name: string): string {
  return STATE_SHAPE_PREFIX + name;
}

/** The fork/join bar, whose single-axis resize is pinned by resize.test.ts. */
export const STATE_FORK_JOIN_SHAPE = stateShapeName('fork-join');

/** The composite-state kind: a container by meaning, so anything parented to it behaves as a container child. */
export const STATE_COMPOSITE_KIND = 'composite-state';

/** The library entry a `custom:state/<name>` shape names, or undefined for any other shape. */
export function stateShapeFor(shape: string): StateShape | undefined {
  return shape.startsWith(STATE_SHAPE_PREFIX) ? STATE_SHAPES.find((candidate) => candidate.name === shape.slice(STATE_SHAPE_PREFIX.length)) : undefined;
}

/**
 * The shape panel's State group: every shape once, placed as its own flowchart kind (or, in freeform where there is
 * no vocabulary, the same kind, which freeform does not lint). A shape whose kind default is the shape itself is
 * placed by kind and shape together, matching how the panel places every other vocabulary entry.
 */
export const STATE_SHAPE_OPTIONS: readonly NodeTemplate[] = STATE_SHAPES.map((shape) => ({
  kind: shape.kind,
  shape: stateShapeName(shape.name),
  label: shape.label,
}));
