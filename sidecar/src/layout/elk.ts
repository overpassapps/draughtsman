/**
 * Auto-layout with ELK (elkjs, bundled in-process). Positions are always recomputed for every node, so the
 * same call serves import (nodes with no geometry), "Tidy" and agent requests; sizes given in the input are
 * kept, missing or zero sizes get a default per shape, and containers are sized by ELK to fit their children.
 * Geometry in the result is absolute, including inside containers, as the document model requires.
 */
import ELK, { type ElkExtendedEdge, type ElkNode, type ElkPort, type LayoutOptions as ElkLayoutOptions } from 'elkjs/lib/elk.bundled.js';
import type { Document, Edge, Geometry, LaidOutDocument, LaidOutNode, Node, Point, Routing, RoutingMode } from '../model/types.ts';
import { ErrorCodes, RpcError } from '../rpc.ts';
import { erSizeOf } from './erSize.ts';
import { umlSizeOf } from './umlSize.ts';

export type Algorithm = 'layered' | 'stress' | 'tree';

export type Direction = 'down' | 'right' | 'up' | 'left';

export interface LayoutOptions {
  /** Defaults from the document type: stress for freeform, layered for everything else. */
  algorithm?: Algorithm;
  /** Flow direction for layered. Default down. */
  direction?: Direction;
}

export interface LayoutResult {
  document: LaidOutDocument;
  warnings: string[];
}

interface Size {
  w: number;
  h: number;
}

/** Default sizes by shape for nodes that arrive without one. Matches the DSL examples in the spec. */
const DEFAULT_SIZE: Readonly<Record<string, Size>> = {
  diamond: { w: 160, h: 100 },
  cylinder: { w: 160, h: 90 },
  pill: { w: 160, h: 56 },
  text: { w: 160, h: 40 },
  hexagon: { w: 180, h: 80 },
  parallelogram: { w: 180, h: 80 },
  image: { w: 120, h: 120 },
};

const DEFAULT_NODE_SIZE: Size = { w: 160, h: 80 };

const DEFAULT_EMPTY_CONTAINER_SIZE: Size = { w: 240, h: 160 };

/** Space inside a container: room for its title at the top, a margin elsewhere. */
const CONTAINER_PADDING = { top: 48, side: 24, bottom: 24 };

const PORT_SIZE = 4;

/** Gap between nodes in the same layer (layered) or minimum node distance (stress). */
const NODE_SPACING = 40;

/** Gap between layers under layered, and the desired edge length under stress. */
const LAYER_SPACING = 60;

/**
 * Wider gaps for a diagram whose connectors carry labels or whose nodes are tables: a label plate and a pair of
 * relationship lines need room in the gap between two boxes, or the boxes nearly abut and the labels float over them.
 */
const ROOMY_NODE_SPACING = 120;
const ROOMY_LAYER_SPACING = 140;

const STRESS_EDGE_LENGTH = 150;

/** Margin from the canvas origin to the first node. */
const CANVAS_PADDING = 40;

/** Gap between a manager's box and the assistant box the tree layout places beside it. */
const ASSISTANT_GAP = 40;

const elk = new ELK();

export function defaultAlgorithm(type: string | undefined): Algorithm {
  return type === 'freeform' ? 'stress' : 'layered';
}

/** The ELK algorithm id for our option value: ELK calls the tree algorithm `mrtree`, the other two by our own names. */
function elkAlgorithmId(algorithm: Algorithm): string {
  return algorithm === 'tree' ? 'mrtree' : algorithm;
}

export async function layoutDocument(input: Document, options: LayoutOptions): Promise<LayoutResult> {
  const warnings: string[] = [];
  const algorithm = options.algorithm ?? defaultAlgorithm(input.type);
  const graph = buildGraph(input, algorithm, options, warnings);

  let laidOut: ElkNode;
  try {
    laidOut = await elk.layout(graph.root);
    if (algorithm === 'stress') {
      // Stress places node centres and lets boxes overlap; ELK's overlap removal is a separate algorithm run as
      // a second pass over the positioned graph, which keeps the arrangement and pushes boxes apart.
      setAlgorithm(laidOut, 'sporeOverlap');
      laidOut = await elk.layout(laidOut);
    }
  } catch (error) {
    throw new RpcError(ErrorCodes.layoutFailed, `ELK ${algorithm} layout failed: ${describe(error)}`);
  }

  const geometries = new Map<string, Geometry>();
  const absolute = new Map<string, Point>();
  absolute.set(graph.root.id, { x: 0, y: 0 });
  collectGeometry(laidOut, 0, 0, graph, geometries, absolute);

  if (algorithm === 'tree') {
    // Assistant boxes sit on an offset side branch: place each beside its manager rather than below it, so the tree
    // reads as an org chart. Only when that spot is clear, so the post-pass can never create an overlap.
    placeAssistants(input, geometries);
  }

  const nodes: LaidOutNode[] = input.nodes.map((node) => {
    const geometry = geometries.get(node.id);
    if (geometry === undefined) {
      throw new RpcError(ErrorCodes.layoutFailed, `ELK ${algorithm} layout returned no geometry for node ${node.id}`);
    }
    const rotation = node.geometry?.rotation;
    return { ...node, geometry: rotation === undefined ? geometry : { ...geometry, rotation } };
  });

  const bendPoints = collectBendPoints(laidOut, graph, absolute);
  const edges: Edge[] = input.edges.map((edge) => applyRouting(edge, bendPoints.get(edge.id), algorithm));

  return { document: { ...input, nodes, edges }, warnings };
}

interface GraphIndex {
  root: ElkNode;
  /** ELK node id -> document node id. */
  nodeIds: Map<string, string>;
  /** ELK edge id -> document edge id. */
  edgeIds: Map<string, string>;
}

function buildGraph(input: Document, algorithm: Algorithm, options: LayoutOptions, warnings: string[]): GraphIndex {
  const byId = new Map<string, Node>();
  for (const node of input.nodes) {
    if (byId.has(node.id)) {
      warnings.push(`duplicate node id ${node.id}; only the first is laid out`);
      continue;
    }
    byId.set(node.id, node);
  }

  const parentOf = resolveParents(byId, warnings);
  const children = new Map<string | null, Node[]>();
  for (const node of byId.values()) {
    const parent = parentOf.get(node.id) ?? null;
    const list = children.get(parent) ?? [];
    list.push(node);
    children.set(parent, list);
  }

  // Only ports an edge actually uses are handed to ELK; a declared but unused port must not constrain the layout.
  const usedPorts = new Map<string, Set<string>>();
  for (const edge of input.edges) {
    for (const end of [edge.from, edge.to]) {
      if (end.port !== undefined) {
        const set = usedPorts.get(end.node) ?? new Set<string>();
        set.add(end.port);
        usedPorts.set(end.node, set);
      }
    }
  }

  const nodeIds = new Map<string, string>();
  const edgeIds = new Map<string, string>();
  /** ELK port ids that exist in the graph; only leaf nodes under layered get any. */
  const portIds = new Set<string>();

  const toElk = (node: Node): ElkNode => {
    const own = children.get(node.id) ?? [];
    const elkId = nodeKey(node.id);
    nodeIds.set(elkId, node.id);
    const elkNode: ElkNode = { id: elkId, layoutOptions: {} };
    if (own.length > 0) {
      elkNode.children = own.map(toElk);
      elkNode.layoutOptions = {
        'elk.algorithm': elkAlgorithmId(algorithm),
        'elk.padding': `[top=${CONTAINER_PADDING.top},left=${CONTAINER_PADDING.side},bottom=${CONTAINER_PADDING.bottom},right=${CONTAINER_PADDING.side}]`,
      };
      if (algorithm === 'stress') {
        elkNode.layoutOptions['elk.stress.desiredEdgeLength'] = String(STRESS_EDGE_LENGTH);
      }
    } else {
      const size = sizeOf(node);
      elkNode.width = size.w;
      elkNode.height = size.h;
      // Ports pin where an edge leaves a node. Only layered routes edges, and only it accepts fixed positions;
      // stress rejects them ("Port side is undefined") and would gain nothing from them.
      const ports = algorithm === 'layered' ? portsFor(node, size, usedPorts.get(node.id)) : [];
      if (ports.length > 0) {
        elkNode.ports = ports;
        elkNode.layoutOptions = { 'elk.portConstraints': 'FIXED_POS' };
        for (const port of ports) {
          portIds.add(port.id);
        }
      }
    }
    return elkNode;
  };

  const rootChildren = (children.get(null) ?? []).map(toElk);

  const edges: ElkExtendedEdge[] = [];
  for (const edge of input.edges) {
    const from = byId.get(edge.from.node);
    const to = byId.get(edge.to.node);
    if (from === undefined || to === undefined) {
      warnings.push(`edge ${edge.id} references a missing node (${from === undefined ? edge.from.node : edge.to.node}); left unrouted`);
      continue;
    }
    if (from.id === to.id) {
      warnings.push(`edge ${edge.id} is a self-loop; left unrouted`);
      continue;
    }
    if (isAncestor(from.id, to.id, parentOf) || isAncestor(to.id, from.id, parentOf)) {
      // A container connected to its own descendant: ELK cannot place such an edge, the renderer draws it directly.
      warnings.push(`edge ${edge.id} connects a container to a node inside it; left unrouted`);
      continue;
    }
    if (algorithm !== 'layered' && parentOf.get(from.id) !== parentOf.get(to.id)) {
      // ELK's stress algorithm lays out one hierarchy level at a time and has no notion of edges across levels.
      warnings.push(`edge ${edge.id} crosses containers, which the stress algorithm does not route; left straight`);
      continue;
    }
    const elkId = edgeKey(edge.id);
    if (edgeIds.has(elkId)) {
      warnings.push(`duplicate edge id ${edge.id}; only the first is routed`);
      continue;
    }
    edgeIds.set(elkId, edge.id);
    edges.push({
      id: elkId,
      sources: [endpointKey(edge.from, portIds)],
      targets: [endpointKey(edge.to, portIds)],
    });
  }

  const root: ElkNode = {
    id: 'root',
    layoutOptions: rootOptions(algorithm, options, needsRoom(input)),
    children: rootChildren,
    edges,
  };
  return { root, nodeIds, edgeIds };
}

/** Points every compound node at `algorithm` for a follow-up pass; ELK lays out each hierarchy level with its own. */
function setAlgorithm(node: ElkNode, algorithm: string): void {
  if (node.children === undefined || node.children.length === 0) {
    return;
  }
  node.layoutOptions = {
    ...node.layoutOptions,
    'elk.algorithm': algorithm,
    'elk.spacing.nodeNode': String(NODE_SPACING),
  };
  for (const child of node.children) {
    setAlgorithm(child, algorithm);
  }
}

/** Whether the diagram needs the roomy gaps: a labelled connector, or a table (`custom:er/*`) that edges are drawn between. */
function needsRoom(input: Document): boolean {
  return input.edges.some((edge) => edge.label !== undefined && edge.label !== '') || input.nodes.some((node) => node.shape.startsWith('custom:er/'));
}

function rootOptions(algorithm: Algorithm, options: LayoutOptions, roomy: boolean): ElkLayoutOptions {
  const common: ElkLayoutOptions = {
    'elk.algorithm': elkAlgorithmId(algorithm),
    'elk.padding': `[top=${CANVAS_PADDING},left=${CANVAS_PADDING},bottom=${CANVAS_PADDING},right=${CANVAS_PADDING}]`,
    'elk.spacing.nodeNode': String(roomy && algorithm === 'layered' ? ROOMY_NODE_SPACING : NODE_SPACING),
  };
  if (algorithm === 'stress') {
    return {
      ...common,
      'elk.stress.desiredEdgeLength': String(STRESS_EDGE_LENGTH),
    };
  }
  if (algorithm === 'tree') {
    // ELK's mr.tree draws a parent above its children, top-down, with no edge routing: what an org chart wants.
    return {
      ...common,
      'elk.direction': 'DOWN',
    };
  }
  return {
    ...common,
    'elk.direction': (options.direction ?? 'down').toUpperCase(),
    'elk.edgeRouting': 'ORTHOGONAL',
    'elk.hierarchyHandling': 'INCLUDE_CHILDREN',
    'elk.layered.spacing.nodeNodeBetweenLayers': String(roomy ? ROOMY_LAYER_SPACING : LAYER_SPACING),
    'elk.layered.spacing.edgeNodeBetweenLayers': '30',
    'elk.spacing.edgeNode': '25',
    'elk.spacing.edgeEdge': '15',
    // Keep the author's order where crossings do not force otherwise: the first node written is the first laid out.
    'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
  };
}

/** Parent id per node after dropping references to missing nodes, self-parenting and cycles. */
function resolveParents(byId: Map<string, Node>, warnings: string[]): Map<string, string> {
  const parentOf = new Map<string, string>();
  for (const node of byId.values()) {
    if (node.parent === undefined) {
      continue;
    }
    if (node.parent === node.id || !byId.has(node.parent)) {
      warnings.push(`node ${node.id} has parent ${node.parent} which does not exist; laid out at top level`);
      continue;
    }
    parentOf.set(node.id, node.parent);
  }
  for (const id of parentOf.keys()) {
    if (isAncestor(id, id, parentOf)) {
      warnings.push(`node ${id} is inside a container cycle; laid out at top level`);
      parentOf.delete(id);
    }
  }
  return parentOf;
}

/** True when `ancestor` is a strict ancestor of `node` (or `node` sits on a cycle through `ancestor`). */
function isAncestor(ancestor: string, node: string, parentOf: Map<string, string>): boolean {
  let current = parentOf.get(node);
  for (let hops = 0; current !== undefined && hops <= parentOf.size; hops++) {
    if (current === ancestor) {
      return true;
    }
    current = parentOf.get(current);
  }
  return false;
}

function sizeOf(node: Node): Size {
  const given = node.geometry;
  if (given !== undefined && isPositive(given.w) && isPositive(given.h)) {
    return { w: given.w, h: given.h };
  }
  if (node.kind === 'container') {
    return DEFAULT_EMPTY_CONTAINER_SIZE;
  }
  // A UML class is as large as its members need, which no per-shape constant can say.
  const uml = umlSizeOf(node);
  if (uml !== undefined) {
    return uml;
  }
  // An ER entity is as tall as its columns need and as wide as their aligned cells need.
  const er = erSizeOf(node);
  if (er !== undefined) {
    return er;
  }
  const byShape = Object.hasOwn(DEFAULT_SIZE, node.shape) ? DEFAULT_SIZE[node.shape] : undefined;
  return byShape ?? DEFAULT_NODE_SIZE;
}

function isPositive(value: number | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0;
}

function portsFor(node: Node, size: Size, used: Set<string> | undefined): ElkPort[] {
  if (used === undefined || node.ports === undefined) {
    return [];
  }
  const ports: ElkPort[] = [];
  for (const port of node.ports) {
    if (!used.has(port.id)) {
      continue;
    }
    ports.push({
      id: portKey(node.id, port.id),
      x: port.x * size.w - PORT_SIZE / 2,
      y: port.y * size.h - PORT_SIZE / 2,
      width: PORT_SIZE,
      height: PORT_SIZE,
    });
  }
  return ports;
}

/** The port when the graph has it (declared on a leaf node under layered), otherwise the node itself. */
function endpointKey(end: Edge['from'], portIds: Set<string>): string {
  if (end.port !== undefined && portIds.has(portKey(end.node, end.port))) {
    return portKey(end.node, end.port);
  }
  return nodeKey(end.node);
}

/**
 * Walks the laid-out tree accumulating parent offsets so every geometry is absolute. ELK reports children
 * relative to their parent, and edges relative to the node named in their `container` (or the root).
 */
function collectGeometry(
  elkNode: ElkNode,
  offsetX: number,
  offsetY: number,
  graph: GraphIndex,
  geometries: Map<string, Geometry>,
  absolute: Map<string, Point>,
): void {
  for (const child of elkNode.children ?? []) {
    const x = offsetX + (child.x ?? 0);
    const y = offsetY + (child.y ?? 0);
    absolute.set(child.id, { x, y });
    const documentId = graph.nodeIds.get(child.id);
    if (documentId !== undefined) {
      geometries.set(documentId, { x: round(x), y: round(y), w: round(child.width ?? 0), h: round(child.height ?? 0) });
    }
    collectGeometry(child, x, y, graph, geometries, absolute);
  }
}

/**
 * Moves every assistant node beside its manager (the other end of an edge touching it), top-aligned to the manager's
 * box with `ASSISTANT_GAP` between them. This is the org-chart convention that an assistant hangs off an offset side
 * branch rather than sitting in the reports row. A move only happens when both share a parent (so a container's kid
 * never leaves it) and the target spot overlaps no other node; otherwise the assistant keeps its tree position.
 */
function placeAssistants(input: Document, geometries: Map<string, Geometry>): void {
  const byId = new Map(input.nodes.map((node) => [node.id, node]));
  const managerOf = new Map<string, string>();
  for (const edge of input.edges) {
    const from = byId.get(edge.from.node);
    const to = byId.get(edge.to.node);
    if (from?.kind === 'assistant' && to !== undefined && !managerOf.has(from.id)) {
      managerOf.set(from.id, to.id);
    }
    if (to?.kind === 'assistant' && from !== undefined && !managerOf.has(to.id)) {
      managerOf.set(to.id, from.id);
    }
  }

  for (const node of input.nodes) {
    if (node.kind !== 'assistant') {
      continue;
    }
    const managerId = managerOf.get(node.id);
    if (managerId === undefined || (byId.get(managerId)?.parent ?? null) !== (node.parent ?? null)) {
      continue;
    }
    const manager = geometries.get(managerId);
    const self = geometries.get(node.id);
    if (manager === undefined || self === undefined) {
      continue;
    }
    const x = manager.x + manager.w + ASSISTANT_GAP;
    const y = manager.y;
    const parent = node.parent !== undefined ? geometries.get(node.parent) : undefined;
    // A moved assistant must stay inside its container (which ELK sized to fit its original spot) and clear of every
    // other node; otherwise it keeps its tree position.
    if (parent !== undefined && (x < parent.x || y < parent.y || x + self.w > parent.x + parent.w || y + self.h > parent.y + parent.h)) {
      continue;
    }
    if (!overlapsAny(node.id, x, y, self.w, self.h, geometries)) {
      geometries.set(node.id, { ...self, x: round(x), y: round(y) });
    }
  }
}

/** True when the rectangle at (x, y, w, h) overlaps any node box in `geometries` other than `exceptId`. */
function overlapsAny(exceptId: string, x: number, y: number, w: number, h: number, geometries: Map<string, Geometry>): boolean {
  for (const [id, box] of geometries) {
    if (id === exceptId) {
      continue;
    }
    if (x < box.x + box.w && box.x < x + w && y < box.y + box.h && box.y < y + h) {
      return true;
    }
  }
  return false;
}

function collectBendPoints(root: ElkNode, graph: GraphIndex, absolute: Map<string, Point>): Map<string, Point[]> {
  const result = new Map<string, Point[]>();
  const visit = (node: ElkNode): void => {
    for (const edge of node.edges ?? []) {
      const documentId = graph.edgeIds.get(edge.id);
      if (documentId === undefined) {
        continue;
      }
      const origin = absolute.get(edge.container ?? node.id) ?? { x: 0, y: 0 };
      const points: Point[] = [];
      for (const section of edge.sections ?? []) {
        for (const bend of section.bendPoints ?? []) {
          points.push({ x: round(origin.x + bend.x), y: round(origin.y + bend.y) });
        }
      }
      result.set(documentId, points);
    }
    for (const child of node.children ?? []) {
      visit(child);
    }
  };
  visit(root);
  return result;
}

/**
 * Bend points become waypoints. An edge ELK routed without bends, or one it did not route at all, loses any
 * waypoints from before the layout: they belonged to the old positions. Under layered every edge ends up orthogonal.
 */
function applyRouting(edge: Edge, bends: Point[] | undefined, algorithm: Algorithm): Edge {
  const { routing: previous, ...rest } = edge;
  // An end on an entity's column row (`col:<name>`, web/src/model/columnPorts.ts) is attached by the shared router, which
  // knows where the row is; ELK routed to the table box, so its bends would pin a route that misses the row.
  const onColumn = [edge.from, edge.to].some((end) => end.port?.startsWith('col:') === true);
  if (bends !== undefined && bends.length > 0 && !onColumn) {
    const mode: RoutingMode = previous?.mode ?? (algorithm === 'layered' ? 'orthogonal' : 'straight');
    return { ...rest, routing: { mode, waypoints: bends } };
  }
  if (previous !== undefined) {
    const routing: Routing = { mode: previous.mode };
    return { ...rest, routing };
  }
  // A layered layout is orthogonal throughout. An edge with no routing at all is drawn as a straight line between box
  // centres, which slants wherever two boxes in neighbouring layers do not share a centre line (a diamond over a box).
  return algorithm === 'layered' ? { ...rest, routing: { mode: 'orthogonal' } } : rest;
}

function nodeKey(id: string): string {
  return `n:${id}`;
}

function portKey(nodeId: string, portId: string): string {
  return `p:${nodeId}:${portId}`;
}

function edgeKey(id: string): string {
  return `e:${id}`;
}

function round(value: number): number {
  return Math.round(value);
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
