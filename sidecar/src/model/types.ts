/**
 * The Draughtsman document model as the sidecar sees it.
 *
 * A copy of web/src/model/types.ts, field for field, because sidecar/ and web/ are separate Node projects
 * (ADR 0002 records the duplication as accepted tech debt until a packages/model workspace earns its keep).
 * The JSON Schema in docs/schema/document.schema.json and the C# records in src/Draughtsman.Core/Models
 * are the other two copies; change all of them together.
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

export type KeyframeEasing = 'cut' | 'linear' | 'easeIn' | 'easeOut' | 'easeInOut';

export type Id = string;

export type Props = Record<string, unknown>;

export interface Geometry {
  x: number;
  y: number;
  w: number;
  h: number;
  rotation?: number;
}

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

export interface EdgeStyle {
  stroke?: string;
  strokeWidth?: number;
  dash?: Dash;
  arrowStart?: ArrowHead;
  arrowEnd?: ArrowHead;
}

export interface Port {
  id: Id;
  x: number;
  y: number;
}

export interface Node {
  id: Id;
  kind: string;
  label: string;
  /** Presentation shape, including document, predefined-process, manual-input, off-page-connector and the wider ISO 5807 set; see document.schema.json. */
  shape: string;
  /** Absent on a node that has arrived without a position (DSL or Mermaid import) and is waiting for layout. */
  geometry?: Geometry;
  style?: NodeStyle;
  props?: Props;
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
  kind: string;
  label?: string;
  from: Endpoint;
  to: Endpoint;
  routing?: Routing;
  style?: EdgeStyle;
  props?: Props;
}

export interface AiProvenance {
  agent: string;
  model?: string;
  at: string;
  action: AiAction;
  elementIds: Id[];
}

export interface Meta {
  createdAt: string;
  updatedAt: string;
  createdBy: string;
  aiProvenance?: AiProvenance[];
}

export interface ViewRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Keyframe {
  id: Id;
  view: ViewRect;
  caption?: string;
  durationMs?: number;
  holdMs?: number;
  easing?: KeyframeEasing;
}

export interface Flythrough {
  keyframes: Keyframe[];
  loop?: boolean;
}

export interface Document {
  id: Id;
  schemaVersion: typeof CURRENT_SCHEMA_VERSION;
  title: string;
  type: DocumentType;
  theme?: Record<string, string>;
  nodes: Node[];
  edges: Edge[];
  meta: Meta;
  flythrough?: Flythrough;
}

/** A document on its way out of layout: every node leaves with geometry, whatever it arrived with. */
export type LaidOutNode = Node & { geometry: Geometry };

export type LaidOutDocument = Omit<Document, 'nodes'> & { nodes: LaidOutNode[] };
