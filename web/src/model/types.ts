/**
 * The Draughtsman document model.
 *
 * Mirrors the C# records in src/Draughtsman.Core/Models and the JSON Schema in docs/schema/document.schema.json
 * field for field. Change all three together; the contract tests on both sides validate docs/examples against the schema.
 * Geometry is in canvas units (CSS pixels at zoom 1) and is always absolute, including for nodes inside containers.
 * A node without geometry has arrived without a position (DSL or Mermaid import) and is waiting for the layout step.
 */

export const CURRENT_SCHEMA_VERSION = 1;

export type DocumentType = 'flowchart' | 'architecture' | 'freeform' | 'sequence';

export type RoutingMode = 'orthogonal' | 'straight' | 'curved';

export type Dash = 'solid' | 'dashed' | 'dotted';

export type ArrowHead =
  | 'none'
  | 'arrow'
  | 'open'
  | 'diamond'
  | 'circle'
  | 'one'
  | 'many'
  | 'zeroOrOne'
  | 'oneOrMany'
  | 'zeroOrMany'
  | 'oneAndOnlyOne'
  | 'inheritance'
  | 'aggregation'
  | 'composition';

export type AiAction = 'created' | 'updated' | 'removed';

/** How the camera travels to a keyframe: `cut` jumps, the rest ease the progress of a smooth fly (canvas/camera.ts). */
export type KeyframeEasing = 'cut' | 'linear' | 'easeIn' | 'easeOut' | 'easeInOut';

/** Stable short id such as n_api, e_12, c_vpc. Never regenerated on load. */
export type Id = string;

/** Free key/values preserved verbatim. */
export type Props = Record<string, unknown>;

export interface Geometry {
  x: number;
  y: number;
  w: number;
  h: number;
  /** Degrees clockwise about the node centre. Absent means 0. */
  rotation?: number;
}

/** Sparse: absent means inherit from the diagram theme plus the kind default. */
export interface NodeStyle {
  fill?: string;
  stroke?: string;
  strokeWidth?: number;
  textColor?: string;
  font?: string;
  dash?: Dash;
  fontSize?: number;
  bold?: boolean;
}

/** Sparse: absent means inherit from the diagram theme plus the edge kind default. */
export interface EdgeStyle {
  stroke?: string;
  strokeWidth?: number;
  dash?: Dash;
  arrowStart?: ArrowHead;
  arrowEnd?: ArrowHead;
}

/** A named attachment point; x and y are fractions of the node's width and height, 0 to 1. */
export interface Port {
  id: Id;
  x: number;
  y: number;
}

export interface Node {
  id: Id;
  /** Semantic kind from the document type's vocabulary. Unknown kinds are allowed but flagged by lint. */
  kind: string;
  label: string;
  /** Presentation only: rectangle | rounded | diamond | cylinder | pill | hexagon | parallelogram | document | predefined-process | manual-input | off-page-connector | delay | manual-operation | display | stored-data | multi-document | merge | extract | on-page-connector | summing-junction | or | collate | sort | loop-limit | internal-storage | card | text | image | custom:<lib>/<name>. */
  shape: string;
  /** Absent until layout has placed the node. */
  geometry?: Geometry;
  style?: NodeStyle;
  props?: Props;
  /** Id of the container node this node sits inside. */
  parent?: Id;
  ports?: Port[];
}

export interface Endpoint {
  node: Id;
  port?: Id;
}

export interface Point {
  x: number;
  y: number;
}

export interface Routing {
  mode: RoutingMode;
  waypoints?: Point[];
}

export interface Edge {
  id: Id;
  /** flow | data | dependency | association, per the document type's vocabulary. */
  kind: string;
  label?: string;
  from: Endpoint;
  to: Endpoint;
  /** Absent means the theme's default routing. */
  routing?: Routing;
  style?: EdgeStyle;
  props?: Props;
}

/** Which agent and model created or last changed which elements. */
export interface AiProvenance {
  agent: string;
  model?: string;
  /** ISO 8601 UTC timestamp as produced by Date.prototype.toISOString(), e.g. 2026-09-20T22:00:00.000Z. */
  at: string;
  action: AiAction;
  elementIds: Id[];
}

export interface Meta {
  /** ISO 8601 UTC timestamp as produced by Date.prototype.toISOString(), e.g. 2026-09-20T22:00:00.000Z. */
  createdAt: string;
  /** ISO 8601 UTC timestamp as produced by Date.prototype.toISOString(), e.g. 2026-09-20T22:00:00.000Z. */
  updatedAt: string;
  createdBy: string;
  aiProvenance?: AiProvenance[];
}

/** A rectangle of the canvas in canvas units, absolute like a geometry; width and height are above zero. */
export interface ViewRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * One stop of a flythrough: a camera framing `view`, an optional caption, and how the camera arrives and how long it
 * stays. Timings and easing are sparse: absent means the player's default (model/flythrough.ts).
 */
export interface Keyframe {
  id: Id;
  /** The region of the canvas the camera fits into the window. */
  view: ViewRect;
  /** Shown over the diagram while the camera is here. */
  caption?: string;
  /** Milliseconds to travel here from the previous keyframe; on the first, from the last when the tour loops. */
  durationMs?: number;
  /** Milliseconds an autoplaying tour stays here. */
  holdMs?: number;
  easing?: KeyframeEasing;
}

/**
 * A camera path over the diagram, in tour order. A presentation of the graph, never part of it: nothing refers to it,
 * every renderer and the Mermaid export ignore it, and a tour with no keyframes is absent, not empty (docs/flythrough.md).
 */
export interface Flythrough {
  keyframes: Keyframe[];
  /** Whether the tour returns from the last keyframe to the first and plays on. Absent means it stops at the last. */
  loop?: boolean;
}

export interface Document {
  id: Id;
  schemaVersion: typeof CURRENT_SCHEMA_VERSION;
  title: string;
  type: DocumentType;
  /** Document-level diagram-theme token overrides, token name to value. */
  theme?: Record<string, string>;
  nodes: Node[];
  edges: Edge[];
  meta: Meta;
  /** The camera path Present plays; absent when the document has none. */
  flythrough?: Flythrough;
}
