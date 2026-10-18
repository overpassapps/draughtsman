/**
 * A read-only index over a Document: nodes and edges by id, parent/child structure, absolute bounds per element
 * and the content bounds. Built once per document (memoised by the canvas) so culling and rendering are O(n) per
 * frame with no repeated lookups.
 */

import { outsideLabelBounds } from '../model/bpmnShapes.ts';
import type { Document, Edge, Geometry, Id, Node, Point } from '../model/types.ts';
import { participantShape, sequenceScene, SEQUENCE_METRICS, type SequenceScene } from '../model/sequenceScene.ts';
import { STATE_COMPOSITE_KIND } from '../model/stateShapes.ts';
import { isSwimlaneKind } from '../model/swimlaneKinds.ts';
import { endpointPort, nudgedPortPoint, outlineExit, outlineKind, sharedPortOffsets, towardsPoint } from './ports.ts';
import { boundsOfPoints, distance, hasArea, intersectRects, padRect, rectBoundaryPoint, rectCentre, rectContainsPoint, rotatedBounds, selfLoopPoints, unionRects, type Rect } from './geometry.ts';

/** Drawable geometry may come from the document or from the index’s provisional placement. */
export type PlacedNode = Node & { geometry: Geometry };

/** Narrows to existing geometry; provisional placement is supplied separately by indexDocument. */
export function isPlaced(node: Node): node is PlacedNode {
  return node.geometry !== undefined;
}

/** A node draws as a container, its label top-left rather than centred, when its kind says so or it has children. */
export function isContainer(index: DocumentIndex, node: Node): boolean {
  return node.kind === 'container' || node.kind === STATE_COMPOSITE_KIND || isSwimlaneKind(node.kind) || (index.childrenOf.get(node.id)?.length ?? 0) > 0;
}

/** Padding added around an edge's geometry so labels and arrowheads are not culled while still on screen. */
export const EDGE_BOUNDS_PADDING = 24;

/**
 * How close two endpoints must be to count as the same point. A port's position and the boundary point computed back
 * towards it are the same place reached by different arithmetic, so they can differ by an ulp; anything within this
 * distance rounds to one coordinate pair at the two decimals the renderer emits, leaving no line to draw.
 */
const COINCIDENT_TOLERANCE = 0.01;

export interface EdgePath {
  /** Full polyline from source boundary to target boundary, including waypoints. */
  points: Point[];
}

export interface DocumentIndex {
  document: Document;
  nodesById: ReadonlyMap<Id, Node>;
  edgesById: ReadonlyMap<Id, Edge>;
  /** Top-level placed nodes and, per container, its placed children, in document order. */
  rootNodes: readonly PlacedNode[];
  childrenOf: ReadonlyMap<Id, readonly PlacedNode[]>;
  /** Every drawable node's geometry, provisional placement included, so interaction has one place to read it. */
  placed: ReadonlyMap<Id, PlacedNode>;
  /** Rotated axis-aligned bounds of each node, clipped to its rendered ancestor containers. */
  nodeBounds: ReadonlyMap<Id, Rect>;
  /** Polyline each edge draws; an edge whose endpoint the index does not draw is absent here and never drawn. */
  edgePaths: ReadonlyMap<Id, EdgePath>;
  /** Padded bounds of each drawable edge. */
  edgeBounds: ReadonlyMap<Id, Rect>;
  /** `${edge.id}:from`/`${edge.id}:to` -> tangential nudge for an endpoint sharing its declared port with another edge (ports.ts `sharedPortOffsets`, Finding D). */
  portOffsets: ReadonlyMap<string, number>;
  /** Union of every node and edge; undefined for an empty document. */
  contentBounds: Rect | undefined;
  /** Nodes the document left without a position: rendered at a provisional spot, never persisted back. */
  unplaced: ReadonlySet<Id>;
  /**
   * For a `sequence` document, the derived scene every position above was read from (model/sequenceScene.ts): its
   * lifelines and activation bars are drawn by DiagramSvg, its label positions replace the label placement. Undefined
   * for every other type.
   */
  sequence: SequenceScene | undefined;
}

/** A provisional size for a node the document left without geometry; the layout step replaces it with the real one. */
const PROVISIONAL_SIZE = { w: 160, h: 48 };
/** Vertical gap between provisional nodes stacked below the placed content. */
const PROVISIONAL_GAP = 16;

/**
 * The provisional position and size for every node still waiting for layout, in document order. The stack is anchored
 * below the placed content (or at the origin when nothing is placed) so it sits near what the user is looking at
 * without disturbing an arrangement already made. Deterministic for a given document, but never written back into the
 * model: `Node.geometry` stays absent, so a provisional position cannot quietly become real data.
 */
function provisionalGeometry(document: Document, placed: ReadonlyMap<Id, PlacedNode>, unplaced: ReadonlySet<Id>): Map<Id, Geometry> {
  const result = new Map<Id, Geometry>();
  if (unplaced.size === 0) {
    return result;
  }
  const placedBounds = unionRects([...placed.values()].map((node) => rotatedBounds(node.geometry)));
  const anchor = placedBounds ? { x: placedBounds.x, y: placedBounds.y + placedBounds.h + PROVISIONAL_GAP } : { x: 0, y: 0 };
  let y = anchor.y;
  for (const node of document.nodes) {
    if (!unplaced.has(node.id)) {
      continue;
    }
    result.set(node.id, { x: anchor.x, y, w: PROVISIONAL_SIZE.w, h: PROVISIONAL_SIZE.h });
    y += PROVISIONAL_SIZE.h + PROVISIONAL_GAP;
  }
  return result;
}

/**
 * The index of a `sequence` document: `placed`, `nodeBounds`, `edgePaths` and `contentBounds` come from the derived scene
 * (a header box per participant, a polyline per message) and nothing is read from, or warned about in, the stored
 * geometry - which a sequence document does not carry. There are no containers, routes, ports or provisional
 * placement; a message whose end is not a participant is not drawn, as an edge to an undrawn node never is.
 */
function indexSequence(document: Document, nodesById: ReadonlyMap<Id, Node>, edgesById: ReadonlyMap<Id, Edge>): DocumentIndex {
  const scene = sequenceScene(document);
  const placed = new Map<Id, PlacedNode>();
  const nodeBounds = new Map<Id, Rect>();
  for (const header of scene.headers) {
    const node = nodesById.get(header.id);
    if (node) {
      const geometry = { x: header.x, y: header.y, w: header.w, h: header.h };
      placed.set(node.id, { ...node, shape: participantShape(node), geometry });
      nodeBounds.set(node.id, geometry);
    }
  }
  // A combined fragment is placed (so it selects and reports bounds like a node) but is not a root the node renderer
  // draws: the sequence layer draws its frame.
  for (const fragment of scene.fragments) {
    const node = nodesById.get(fragment.id);
    if (node) {
      const geometry = { x: fragment.x, y: fragment.y, w: fragment.w, h: fragment.h };
      placed.set(node.id, { ...node, geometry });
      nodeBounds.set(node.id, geometry);
    }
  }
  const rootNodes = scene.headers.flatMap((header) => placed.get(header.id) ?? []);
  const edgePaths = new Map<Id, EdgePath>();
  const edgeBounds = new Map<Id, Rect>();
  for (const message of scene.messages) {
    const points = message.points;
    edgePaths.set(message.id, { points });
    const line = padRect(boundsOfPoints(points) ?? { x: 0, y: 0, w: 0, h: 0 }, EDGE_BOUNDS_PADDING);
    // The label plate sits above the line and may run wider than it, so it counts toward the edge's box.
    const plate = message.labelAt === undefined || message.labelWidth === undefined ? undefined : { x: message.labelAt.x - message.labelWidth / 2 - SEQUENCE_METRICS.plateGap, y: message.labelAt.y - 12, w: message.labelWidth + SEQUENCE_METRICS.plateGap * 2, h: 24 };
    edgeBounds.set(message.id, unionRects(plate === undefined ? [line] : [line, plate]) ?? line);
  }
  return {
    document,
    nodesById,
    edgesById,
    rootNodes,
    childrenOf: new Map(),
    placed,
    nodeBounds,
    edgePaths,
    edgeBounds,
    portOffsets: new Map(),
    contentBounds: scene.bounds,
    unplaced: new Set(),
    sequence: scene,
  };
}

export function indexDocument(document: Document, draggedNodes?: ReadonlySet<Id>): DocumentIndex {
  const nodesById = new Map<Id, Node>();
  for (const node of document.nodes) {
    nodesById.set(node.id, node);
  }
  const edgesById = new Map<Id, Edge>();
  for (const edge of document.edges) {
    edgesById.set(edge.id, edge);
  }
  if (document.type === 'sequence') {
    return indexSequence(document, nodesById, edgesById);
  }

  // A node without geometry would be invisible and take every edge touching it down too, with nothing to say why.
  // Give each a provisional place and say so through the same channel the canvas uses for an undrawable edge, so the
  // loss is impossible to miss. The provisional position is derived, never persisted.
  const unplaced = new Set<Id>();
  const placed = new Map<Id, PlacedNode>();
  for (const node of document.nodes) {
    if (isPlaced(node)) {
      placed.set(node.id, node);
    } else {
      unplaced.add(node.id);
    }
  }
  for (const [id, geometry] of provisionalGeometry(document, placed, unplaced)) {
    const node = nodesById.get(id);
    if (node) {
      placed.set(id, { ...node, geometry });
      console.warn(`Draughtsman: node ${id} has no position (layout has not placed it), so it is drawn at a provisional spot and marked as not yet placed.`);
    }
  }

  const rootNodes: PlacedNode[] = [];
  const childrenOf = new Map<Id, PlacedNode[]>();
  for (const node of document.nodes) {
    const drawable = placed.get(node.id);
    if (!drawable) {
      continue;
    }
    const parent = node.parent !== undefined ? nodesById.get(node.parent) : undefined;
    // Nest only when both nodes have authored geometry: provisional boxes must not clip away children.
    // A dangling, self or cyclic parent is also treated as no parent; lint reports it later.
    const lifted = draggedNodes?.has(node.id) === true && !draggedNodes.has(parent?.id ?? '');
    if (!lifted && parent && isPlaced(parent) && isPlaced(node) && parent.id !== node.id && !isAncestor(nodesById, parent, node.id)) {
      const siblings = childrenOf.get(parent.id) ?? [];
      siblings.push(drawable);
      childrenOf.set(parent.id, siblings);
    } else {
      rootNodes.push(drawable);
    }
  }

  const nodeBounds = new Map<Id, Rect>();
  const visit = (node: PlacedNode, clip: Rect | undefined): void => {
    const own = rotatedBounds(node.geometry);
    const bounds = clip ? intersectRects(own, clip) : own;
    nodeBounds.set(node.id, bounds);
    // Children are clipped to the container's unrotated box, which is also the clipPath the renderer uses.
    const childClip = clip ? intersectRects(node.geometry, clip) : { ...node.geometry };
    for (const child of childrenOf.get(node.id) ?? []) {
      visit(child, childClip);
    }
  };
  for (const node of rootNodes) {
    visit(node, undefined);
  }

  // A node the index did not draw — clipped away to nothing by its container — takes its edges with it: the line
  // would run to a corner where nothing is rendered and widen the rectangle fit-to-screen works from. Unplaced nodes
  // carry a provisional position, so they still draw and their edges still reach them.
  const isDrawn = (node: PlacedNode | undefined): node is PlacedNode => {
    const bounds = node === undefined ? undefined : nodeBounds.get(node.id);
    return node !== undefined && bounds !== undefined && hasArea(bounds);
  };

  const portOffsets = sharedPortOffsets(document.edges);
  const edgePaths = new Map<Id, EdgePath>();
  const edgeBounds = new Map<Id, Rect>();
  for (const edge of document.edges) {
    const from = placed.get(edge.from.node);
    const to = placed.get(edge.to.node);
    if (!isDrawn(from) || !isDrawn(to)) {
      continue;
    }
    const points = edgePolyline(edge, from, to, portOffsets);
    const bounds = boundsOfPoints(points);
    if (!bounds || (bounds.w === 0 && bounds.h === 0)) {
      // Every point coincides, so there is no line to draw: coincident nodes at the same position. An unrouted
      // self-link no longer lands here — the renderer draws its own fixed arc, since ELK leaves self-loops unrouted.
      console.warn(`Draughtsman: edge ${edge.id} has nothing to draw (${edge.from.node} to ${edge.to.node} with no waypoints), so it is not rendered.`);
      continue;
    }
    edgePaths.set(edge.id, { points });
    edgeBounds.set(edge.id, padRect(bounds, EDGE_BOUNDS_PADDING));
  }

  // A node clipped away to nothing is not drawn, so it must not stretch the rectangle fit-to-screen works from.
  // A BPMN event, gateway or data shape draws its label in a band below its box, so that band counts as content too.
  const labelBounds = [...placed.values()].flatMap((node) => (isDrawn(node) ? (outsideLabelBounds(node.shape, node.geometry) ?? []) : []));
  const contentBounds = unionRects([...[...nodeBounds.values()].filter(hasArea), ...edgeBounds.values(), ...labelBounds]);

  return { document, nodesById, edgesById, rootNodes, childrenOf, placed, nodeBounds, edgePaths, edgeBounds, portOffsets, contentBounds, unplaced, sequence: undefined };
}

function isAncestor(nodesById: ReadonlyMap<Id, Node>, start: Node, candidate: Id): boolean {
  const seen = new Set<Id>();
  let current: Node | undefined = start;
  while (current?.parent !== undefined && !seen.has(current.id)) {
    seen.add(current.id);
    if (current.parent === candidate) {
      return true;
    }
    current = nodesById.get(current.parent);
  }
  return false;
}

/**
 * The absolute position of a named port on a node, or undefined when the node has no such port. The port is one the
 * node declares or an entity's column (`col:<name>`, model/columnPorts.ts), which sits on the side of the table that
 * faces `towards`. `offset` (ports.ts `sharedPortOffsets`, Finding D) nudges it tangentially along the node's side for
 * an endpoint sharing that port with another edge; 0 (every caller but `edgePolyline`'s own) leaves it exactly where
 * markers, hover and the connector tool's hit-testing expect it.
 */
export function portPoint(node: PlacedNode, portId: Id | undefined, offset = 0, towards: Point = rectCentre(node.geometry)): Point | undefined {
  const port = endpointPort(node, portId, towards);
  if (!port) {
    return undefined;
  }
  return nudgedPortPoint(node, port, offset);
}

/**
 * Builds the polyline for an edge: through `routing.waypoints` when present, otherwise straight centre to centre.
 * An endpoint with a port attaches exactly there (nudged a few units along the node's side when `portOffsets` says
 * this endpoint shares its port with another edge, Finding D); without one the line starts and ends on the node's
 * boundary so the arrowhead is visible rather than buried under the shape. An unrouted self-referencing edge whose
 * two endpoints resolve to the same point has no line to draw, so it takes the renderer's fixed loop arc anchored
 * there, since ELK leaves self-loops unrouted and never supplies their points. A self-link between two distinct
 * ports keeps its line.
 *
 * A pinned connector's waypoints (checklist item 10) are never re-derived by a move or resize (decision D4), but a
 * waypoint an endpoint's new bounds has grown to enclose would otherwise leave a segment cutting through the drawn
 * shape: such waypoints are dropped from each end before the boundary point is computed, so only the end segment
 * (not the pinned interior of the route) follows the endpoint.
 */
export function edgePolyline(edge: Edge, from: PlacedNode, to: PlacedNode, portOffsets?: ReadonlyMap<string, number>): Point[] {
  const waypoints = trimEnclosedWaypoints(edge.routing?.waypoints ?? [], from, to);
  // A column port sits on the side facing the far end: its first waypoint if pinned, else the far node's aim point.
  const fromPort = portPoint(from, edge.from.port, portOffsets?.get(`${edge.id}:from`) ?? 0, waypoints[0] ?? towardsPoint(from, edge.from.port, to, edge.to.port));
  const toPort = portPoint(to, edge.to.port, portOffsets?.get(`${edge.id}:to`) ?? 0, waypoints[waypoints.length - 1] ?? towardsPoint(to, edge.to.port, from, edge.from.port));
  const fromCentre = rectCentre(from.geometry);
  const toCentre = rectCentre(to.geometry);

  const firstTarget = waypoints[0] ?? toPort ?? toCentre;
  // An orthogonal connector with routed bends (ELK's) leaves its node straight towards the first one.
  const bent = edge.routing?.mode === 'orthogonal' && waypoints.length > 0;
  const start = fromPort ?? nodeBoundaryPoint(from, firstTarget, bent);
  const lastSource = waypoints[waypoints.length - 1] ?? fromPort ?? fromCentre;
  const end = toPort ?? nodeBoundaryPoint(to, lastSource, bent);
  if (from.id === to.id && waypoints.length === 0 && distance(start, end) < COINCIDENT_TOLERANCE) {
    return selfLoopPoints(from.geometry, fromPort ?? toPort);
  }
  return [start, ...waypoints, end];
}

/** Drops waypoints from each end that a move or resize has pulled inside that end's own node, keeping the rest untouched. */
function trimEnclosedWaypoints(waypoints: readonly Point[], from: PlacedNode, to: PlacedNode): Point[] {
  if (waypoints.length === 0) {
    return [];
  }
  const fromBounds = rotatedBounds(from.geometry);
  const toBounds = rotatedBounds(to.geometry);
  let start = 0;
  while (start < waypoints.length) {
    const point = waypoints[start];
    if (point === undefined || !rectContainsPoint(fromBounds, point)) {
      break;
    }
    start++;
  }
  let end = waypoints.length;
  while (end > start) {
    const point = waypoints[end - 1];
    if (point === undefined || !rectContainsPoint(toBounds, point)) {
      break;
    }
    end--;
  }
  return waypoints.slice(start, end);
}

/**
 * Where a connector leaves a node that has no port there, heading for `toward`. A diamond (a decision, a BPMN gateway)
 * or ellipse (a BPMN event) stops at its drawn outline rather than at the box corner (QA-01); `aligned` is an
 * orthogonal connector's first or last routed bend, which the connector leaves straight towards (ports.ts
 * `outlineExit`). The server renderer's `EdgeAttachment.Clip` does the same.
 */
function nodeBoundaryPoint(node: PlacedNode, toward: Point, aligned = false): Point {
  const g = node.geometry;
  const centre = rectCentre(g);
  const outline = outlineKind(node.shape);
  if (outline !== undefined) {
    return outlineExit(g, outline, toward, aligned);
  }
  if (!['document', 'predefined-process', 'manual-input', 'off-page-connector'].includes(node.shape)) {
    return rectBoundaryPoint(g, centre, toward);
  }
  const angle = (g.rotation ?? 0) * Math.PI / 180;
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  const worldX = toward.x - centre.x;
  const worldY = toward.y - centre.y;
  const dx = (worldX * cos + worldY * sin) / g.w;
  const dy = (-worldX * sin + worldY * cos) / g.h;
  let scale = Math.min(1, dx === 0 ? Infinity : 0.5 / Math.abs(dx), dy === 0 ? Infinity : 0.5 / Math.abs(dy));
  if (node.shape === 'manual-input' && dy + 0.25 * dx < 0) {
    scale = Math.min(scale, -0.375 / (dy + 0.25 * dx));
  }
  if (node.shape === 'off-page-connector' && dy + 0.7 * Math.abs(dx) > 0) {
    scale = Math.min(scale, 0.5 / (dy + 0.7 * Math.abs(dx)));
  }
  if (node.shape === 'document' && dy > 0 && Math.abs(dx / dy) <= 0.5 / 0.35) {
    let low = 0;
    let high = 1;
    for (let i = 0; i < 48; i++) {
      const u = (low + high) / 2;
      const x = 0.75 * u + 0.75 * u * u - 0.5 * u * u * u - 0.5;
      const y = 0.35 + 0.9 * u - 2.7 * u * u + 1.8 * u * u * u;
      if (x * dy < y * dx) low = u;
      else high = u;
    }
    const u = (low + high) / 2;
    scale = Math.min(scale, (0.35 + 0.9 * u - 2.7 * u * u + 1.8 * u * u * u) / dy);
  }
  return { x: centre.x + worldX * scale, y: centre.y + worldY * scale };
}
