/**
 * The node kinds the shape panel groups by document type (design/atlas/shape-connector-palette.md, "Sections in
 * order"). Each entry names a kind from src/Draughtsman.Core/Schemas/<type>.json and the vocabulary's own default
 * shape for it, which nodeKinds.test.ts checks this table against, plus a display label for the panel glyph's title
 * and aria-label. Freeform has no vocabulary, so it offers none: the Shapes group (SHAPE_OPTIONS) is its only source
 * of new nodes. The list is the kinds that matter for the two real diagrams the panel targets (a client architecture
 * and a flowchart), not the full vocabulary: `text`, `image` and `sticky` stay reachable through the command palette.
 */

import type { NodeTemplate } from './create.ts';
import { LANE_SHAPE, NEW_POOL_SIZE, POOL_SHAPE } from './swimlanes.ts';
import type { DocumentType } from './types.ts';

export interface NodeKindOption {
  /** Semantic kind, from the document type's vocabulary. */
  kind: string;
  /** Display name for the panel glyph's title and aria-label. */
  label: string;
  /** The vocabulary's own default shape for this kind. */
  defaultShape: string;
}

/** Presentation shapes the panel's Shapes group offers, for freeform or for a plain box no kind fits. */
export interface ShapeOption {
  shape: string;
  label: string;
}

export const NODE_KINDS: Readonly<Record<'architecture' | 'flowchart' | 'sequence', readonly NodeKindOption[]>> = {
  sequence: [
    { kind: 'participant', label: 'Participant', defaultShape: 'rectangle' },
    { kind: 'actor', label: 'Actor', defaultShape: 'rounded' },
  ],
  flowchart: [
    { kind: 'process', label: 'Process', defaultShape: 'rounded' },
    { kind: 'decision', label: 'Decision', defaultShape: 'diamond' },
    { kind: 'terminal', label: 'Terminator', defaultShape: 'pill' },
    { kind: 'io', label: 'Data / IO', defaultShape: 'parallelogram' },
    { kind: 'subprocess', label: 'Predefined process', defaultShape: 'rectangle' },
    { kind: 'note', label: 'Note', defaultShape: 'rectangle' },
    { kind: 'container', label: 'Container', defaultShape: 'rectangle' },
  ],
  architecture: [
    { kind: 'service', label: 'Service', defaultShape: 'rounded' },
    { kind: 'database', label: 'Database', defaultShape: 'cylinder' },
    { kind: 'queue', label: 'Queue', defaultShape: 'rectangle' },
    { kind: 'actor', label: 'Client / User', defaultShape: 'rounded' },
    { kind: 'external', label: 'External system', defaultShape: 'rectangle' },
    { kind: 'container', label: 'Container / Zone', defaultShape: 'rectangle' },
    { kind: 'note', label: 'Note', defaultShape: 'rectangle' },
  ],
};

/**
 * The presentation shapes the panel's Shapes group offers, in the atlas's order. Placing one writes a plain `shape`
 * kind (unknown to the architecture and flowchart vocabularies, and so flagged by lint there, same as any other
 * unrecognised kind) rather than one of the document type's semantic kinds: the group exists to draw a box the kind
 * vocabulary has no name for, not to redraw an existing kind.
 */
export const SHAPE_OPTIONS: readonly ShapeOption[] = [
  { shape: 'rectangle', label: 'Rectangle' },
  { shape: 'rounded', label: 'Rounded rectangle' },
  { shape: 'diamond', label: 'Diamond' },
  { shape: 'cylinder', label: 'Cylinder' },
  { shape: 'pill', label: 'Pill' },
  { shape: 'hexagon', label: 'Hexagon' },
  { shape: 'image', label: 'Image' },
];

/**
 * The swimlane containers (model/swimlanes.ts), offered for flowchart and freeform documents: a pool with its title
 * band and a lane band inside it. Both carry their own kinds and shapes; pool and lane are the two container kinds
 * swimlanes add, and no new leaf shapes, so a cross-functional process is drawn with the existing flowchart symbols
 * placed inside a lane. A lane dropped on its own is a band that can later be reparented into a pool.
 */
export const SWIMLANE_OPTIONS: readonly NodeTemplate[] = [
  { kind: 'pool', shape: POOL_SHAPE, label: 'Pool', size: NEW_POOL_SIZE },
  { kind: 'lane', shape: LANE_SHAPE, label: 'Lane' },
];

/** Kind placed by a Shapes-group glyph: see SHAPE_OPTIONS. */
export const GENERIC_SHAPE_KIND = 'shape';

/** The kind group the shape panel shows for `type`, empty for freeform. */
export function nodeKindOptions(type: DocumentType): readonly NodeKindOption[] {
  return type === 'freeform' ? [] : NODE_KINDS[type];
}

/** Flowchart presentations of existing semantic kinds; vocabulary defaults remain unchanged for old DSL input. */
export const FLOWCHART_SHAPES: readonly NodeTemplate[] = [
  { kind: 'io', shape: 'document', label: 'Document' },
  { kind: 'subprocess', shape: 'predefined-process', label: 'Predefined process' },
  { kind: 'io', shape: 'manual-input', label: 'Manual input' },
  { kind: 'process', shape: 'off-page-connector', label: 'Off-page connector' },
];

/**
 * The wider ISO 5807 symbol set, in its own "More flowchart shapes" palette group so the everyday kinds stay
 * uncluttered. A symbol with a distinct semantic kind (delay, manual-operation, display, stored-data, merge, extract,
 * loop-limit) is placed with that kind; the rest are presentation-only and use GENERIC_SHAPE_KIND, exactly as
 * SHAPE_OPTIONS does. nodeKinds.test.ts checks every kind here names a vocabulary kind with its own default shape.
 */
export const MORE_FLOWCHART_SHAPES: readonly NodeTemplate[] = [
  { kind: 'delay', shape: 'delay', label: 'Delay' },
  { kind: 'manual-operation', shape: 'manual-operation', label: 'Manual operation' },
  { kind: 'display', shape: 'display', label: 'Display' },
  { kind: 'stored-data', shape: 'stored-data', label: 'Stored data' },
  { kind: 'merge', shape: 'merge', label: 'Merge' },
  { kind: 'extract', shape: 'extract', label: 'Extract' },
  { kind: 'loop-limit', shape: 'loop-limit', label: 'Loop limit' },
  { kind: GENERIC_SHAPE_KIND, shape: 'multi-document', label: 'Multiple documents' },
  { kind: GENERIC_SHAPE_KIND, shape: 'on-page-connector', label: 'On-page connector' },
  { kind: GENERIC_SHAPE_KIND, shape: 'summing-junction', label: 'Summing junction' },
  { kind: GENERIC_SHAPE_KIND, shape: 'or', label: 'Or' },
  { kind: GENERIC_SHAPE_KIND, shape: 'collate', label: 'Collate' },
  { kind: GENERIC_SHAPE_KIND, shape: 'sort', label: 'Sort' },
  { kind: GENERIC_SHAPE_KIND, shape: 'internal-storage', label: 'Internal storage' },
  { kind: GENERIC_SHAPE_KIND, shape: 'card', label: 'Card' },
];
