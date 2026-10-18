/**
 * Orthogonal connectors on the canvas (the M2 editor checklist, items 9 and 10; decision D4 (a): a connector's
 * waypoints are derived by the client router at render time and nothing about the route is written to the model).
 *
 * An edge whose `routing.mode` is `orthogonal` is drawn along the route routeOrthogonal computes, in the routing
 * worker, from the current node bounds; any waypoints the document stores for it (ELK's bend points after a layout)
 * are what it draws only until that route arrives. Every other edge draws exactly as documentIndex.ts builds it.
 *
 * Each end attaches at its port when it names one the node declares, leaving by the side the port sits on
 * (ports.ts). An end that names no port floats: it attaches at the midpoint of whichever side of its node faces the
 * other end, so it re-attaches to a better side as the nodes move. Containers holding either endpoint are passable
 * for that edge (a route into a container has to cross its border); every other drawn node is an obstacle.
 *
 * EdgeRouteScheduler keeps the routes current as the document changes. It re-routes only the edges a change can
 * affect: those whose endpoints moved, resized or re-attached, and those whose current route runs near a node that
 * moved (into its path, or out of it). At most one batch is in flight; changes that arrive meanwhile gather into the
 * next batch, so a drag never queues work in the worker and the previous route stays on screen until its successor
 * lands. Pure apart from the promise it awaits, so it is testable with an in-process RouteSource.
 */

import type { Edge, Id, Point, Port } from '../model/types.ts';
import type { DocumentIndex, PlacedNode } from './documentIndex.ts';
import { EDGE_BOUNDS_PADDING } from './documentIndex.ts';
import { boundsOfPoints, hasArea, padRect, rectCentre, rectsIntersect, rotatedBounds, unionRects, type Rect } from './geometry.ts';
import { defaultPorts } from '../model/connect.ts';
import { endpointPort, nudgedPortPoint, portSide, towardsPoint } from './ports.ts';
import { CORRIDOR_MARGIN, ROUTE_CLEARANCE, type OrthogonalRoute, type Side } from './routing.ts';
import type { RouteNode, RouteRequest, RouteRequestEndpoint, RouteResult } from './routingProtocol.ts';

/** What routing one edge needs, plus a signature that changes whenever any of it does. */
export interface EdgeRouteInput {
  request: RouteRequest;
  signature: string;
  /** The two endpoint nodes' bounds together: the region a route is sure to touch. */
  endpointsBox: Rect;
}

/** The routing view of one document state: the node set every batch routes around, and each orthogonal edge's input. */
export interface RoutingSnapshot {
  nodes: RouteNode[];
  bounds: ReadonlyMap<Id, Rect>;
  inputs: ReadonlyMap<Id, EdgeRouteInput>;
}

/** Past this many nodes changing at once (a Tidy, an import) every orthogonal edge re-routes rather than being tested. */
const BULK_CHANGE = 64;

function sameRect(a: Rect | undefined, b: Rect): boolean {
  return a?.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h;
}

/** The side of `geometry` that faces `towards`, judged from its centre in proportion to its width and height. */
function facingSide(bounds: Rect, towards: Point): Side {
  const centre = rectCentre(bounds);
  const dx = (towards.x - centre.x) / Math.max(bounds.w, Number.EPSILON);
  const dy = (towards.y - centre.y) / Math.max(bounds.h, Number.EPSILON);
  if (Math.abs(dx) >= Math.abs(dy)) {
    return dx >= 0 ? 'right' : 'left';
  }
  return dy >= 0 ? 'bottom' : 'top';
}

/**
 * Where an end attaches: its declared port, or the side midpoint that leaves by the side of the drawn shape facing
 * `towards`. `offset` (ports.ts `sharedPortOffsets`, Finding D) nudges a declared port sharing itself with another
 * edge's endpoint tangentially along that side, so their routes fan out from the port rather than starting the same
 * initial run on top of each other.
 */
function attachment(node: PlacedNode, portId: Id | undefined, towards: Point, offset: number): RouteRequestEndpoint {
  const port = endpointPort(node, portId, towards) ?? floatingPort(node, towards);
  const side = portSide(node.geometry, port);
  const point = nudgedPortPoint(node, port, offset);
  return { point, side, nodeId: node.id };
}

function floatingPort(node: PlacedNode, towards: Point): Pick<Port, 'x' | 'y'> {
  const side = facingSide(rotatedBounds(node.geometry), towards);
  return defaultPorts(node.shape).find((candidate) => portSide(node.geometry, candidate) === side) ?? { x: 0.5, y: 0.5 };
}

function ancestors(index: DocumentIndex, id: Id): Id[] {
  const result: Id[] = [];
  const seen = new Set<Id>([id]);
  let parent = index.nodesById.get(id)?.parent;
  while (parent !== undefined && !seen.has(parent)) {
    seen.add(parent);
    result.push(parent);
    parent = index.nodesById.get(parent)?.parent;
  }
  return result;
}

/** Whether an edge is drawn through the router: orthogonal, not pinned, drawn by the index, and not a loop onto a single point. */
function routable(edge: Edge, index: DocumentIndex, from: PlacedNode, to: PlacedNode): boolean {
  // A sequence message is a derived straight line or bracket (model/sequenceScene.ts), whatever routing it names.
  if (index.sequence !== undefined || edge.routing?.mode !== 'orthogonal' || !index.edgePaths.has(edge.id)) {
    return false;
  }
  // A pinned connector (waypoints the user dragged a segment to write) draws those waypoints directly instead of being
  // re-derived (decision D4), so it is not routed. ELK's bend points are waypoints too and keep the edge pinned the
  // same way until "reset route" clears them.
  if ((edge.routing.waypoints?.length ?? 0) > 0) {
    return false;
  }
  // A self-link routes only between two distinct declared ports; otherwise it keeps the renderer's fixed loop arc.
  if (from.id === to.id) {
    const a = endpointPort(from, edge.from.port, rectCentre(to.geometry));
    const b = endpointPort(to, edge.to.port, rectCentre(from.geometry));
    return a !== undefined && b !== undefined && (a.x !== b.x || a.y !== b.y);
  }
  return true;
}

/** Reads the routing view of a document index. */
export function routingSnapshot(index: DocumentIndex): RoutingSnapshot {
  const bounds = new Map<Id, Rect>();
  const nodes: RouteNode[] = [];
  for (const [id, node] of index.placed) {
    const drawn = index.nodeBounds.get(id);
    if (drawn === undefined || !hasArea(drawn)) {
      continue;
    }
    // The router treats a node as the axis-aligned box of its drawn shape; ports turn with the shape (ports.ts).
    const rect = rotatedBounds(node.geometry);
    bounds.set(id, rect);
    nodes.push({ id, bounds: rect });
  }

  const inputs = new Map<Id, EdgeRouteInput>();
  for (const edge of index.document.edges) {
    const from = index.placed.get(edge.from.node);
    const to = index.placed.get(edge.to.node);
    if (from === undefined || to === undefined || !routable(edge, index, from, to)) {
      continue;
    }
    const fromBounds = bounds.get(from.id);
    const toBounds = bounds.get(to.id);
    if (fromBounds === undefined || toBounds === undefined) {
      continue;
    }
    const start = attachment(from, edge.from.port, towardsPoint(from, edge.from.port, to, edge.to.port), index.portOffsets.get(`${edge.id}:from`) ?? 0);
    const end = attachment(to, edge.to.port, towardsPoint(to, edge.to.port, from, edge.from.port), index.portOffsets.get(`${edge.id}:to`) ?? 0);
    const passThrough = [...new Set([...ancestors(index, from.id), ...ancestors(index, to.id)])].filter((id) => id !== from.id && id !== to.id);
    const request: RouteRequest = passThrough.length > 0 ? { edgeId: edge.id, start, end, passThrough } : { edgeId: edge.id, start, end };
    inputs.set(edge.id, {
      request,
      signature: JSON.stringify([start, end, fromBounds, toBounds, passThrough]),
      endpointsBox: unionRects([fromBounds, toBounds]) ?? fromBounds,
    });
  }
  return { nodes, bounds, inputs };
}

/**
 * The orthogonal edges of `next` that need a new route. With no previous snapshot that is all of them. Otherwise an
 * edge is stale when its own input changed (an endpoint moved, resized or re-attached; the edge is new or its ports
 * changed), or when a node that appeared, disappeared, moved or resized overlaps the region its route occupies: its
 * current route, grown by twice the clearance the router keeps, or while it has none yet its endpoints' box grown by
 * the router's first corridor. That catches an obstacle moved into the path and one moved out of the way.
 */
export function staleEdges(previous: RoutingSnapshot | undefined, next: RoutingSnapshot, routes: ReadonlyMap<Id, OrthogonalRoute>): Set<Id> {
  const stale = new Set<Id>();
  if (previous === undefined) {
    return new Set(next.inputs.keys());
  }
  const changed: Rect[] = [];
  for (const [id, rect] of next.bounds) {
    const before = previous.bounds.get(id);
    if (!sameRect(before, rect)) {
      changed.push(rect);
      if (before !== undefined) {
        changed.push(before);
      }
    }
  }
  for (const [id, rect] of previous.bounds) {
    if (!next.bounds.has(id)) {
      changed.push(rect);
    }
  }
  const bulk = changed.length > BULK_CHANGE * 2;
  for (const [id, input] of next.inputs) {
    if (bulk || previous.inputs.get(id)?.signature !== input.signature) {
      stale.add(id);
      continue;
    }
    if (changed.length === 0) {
      continue;
    }
    const route = routes.get(id);
    const routeBox = route === undefined ? undefined : boundsOfPoints(route.points);
    const region = routeBox === undefined ? padRect(input.endpointsBox, CORRIDOR_MARGIN) : padRect(routeBox, ROUTE_CLEARANCE * 2);
    if (changed.some((rect) => rectsIntersect(rect, region))) {
      stale.add(id);
    }
  }
  return stale;
}

/** The index with each routed edge's path and culling bounds replaced by its route; other edges are untouched. */
export function withRoutes(index: DocumentIndex, routes: ReadonlyMap<Id, OrthogonalRoute>): DocumentIndex {
  if (routes.size === 0) {
    return index;
  }
  const edgePaths = new Map(index.edgePaths);
  const edgeBounds = new Map(index.edgeBounds);
  for (const [id, route] of routes) {
    const bounds = boundsOfPoints(route.points);
    if (!edgePaths.has(id) || bounds === undefined) {
      continue;
    }
    edgePaths.set(id, { points: route.points });
    edgeBounds.set(id, padRect(bounds, EDGE_BOUNDS_PADDING));
  }
  return { ...index, edgePaths, edgeBounds };
}

/** What the scheduler needs from the routing worker; RoutingClient provides it. */
export interface RouteSource {
  route(nodes: readonly RouteNode[], requests: readonly RouteRequest[]): Promise<RouteResult[]>;
}

const NO_ROUTES: ReadonlyMap<Id, OrthogonalRoute> = new Map();

export class EdgeRouteScheduler {
  private routes = new Map<Id, OrthogonalRoute>();
  private snapshot: RoutingSnapshot | undefined;
  private readonly dirty = new Set<Id>();
  private inFlight = false;
  private stopped = false;
  private readonly source: RouteSource;
  private readonly publish: (routes: ReadonlyMap<Id, OrthogonalRoute>) => void;

  constructor(source: RouteSource, publish: (routes: ReadonlyMap<Id, OrthogonalRoute>) => void) {
    this.source = source;
    this.publish = publish;
  }

  /** Takes the latest document state, marks what it made stale, and sends a batch if none is in flight. */
  update(index: DocumentIndex): void {
    if (this.stopped) {
      return;
    }
    const next = routingSnapshot(index);
    for (const id of staleEdges(this.snapshot, next, this.routes)) {
      this.dirty.add(id);
    }
    this.snapshot = next;
    // An edge that is gone or no longer orthogonal loses its route at once and draws as the index builds it.
    let dropped = false;
    for (const id of [...this.routes.keys()]) {
      if (!next.inputs.has(id)) {
        this.routes.delete(id);
        dropped = true;
      }
    }
    if (dropped) {
      this.emit();
    }
    this.flush();
  }

  /** Stops scheduling; a batch still in flight is ignored when it lands. */
  dispose(): void {
    this.stopped = true;
  }

  private emit(): void {
    this.routes = new Map(this.routes);
    this.publish(this.routes.size === 0 ? NO_ROUTES : this.routes);
  }

  private flush(): void {
    const snapshot = this.snapshot;
    if (this.inFlight || this.stopped || snapshot === undefined || this.dirty.size === 0) {
      return;
    }
    const requests: RouteRequest[] = [];
    for (const id of this.dirty) {
      const input = snapshot.inputs.get(id);
      if (input !== undefined) {
        requests.push(input.request);
      }
    }
    this.dirty.clear();
    if (requests.length === 0) {
      return;
    }
    this.inFlight = true;
    this.source.route(snapshot.nodes, requests).then(
      (results) => {
        this.inFlight = false;
        if (this.stopped) {
          return;
        }
        for (const result of results) {
          // A result for an edge deleted, or switched to another mode, while it was being routed is dropped.
          if (this.snapshot?.inputs.has(result.edgeId) === true) {
            this.routes.set(result.edgeId, result.route);
          }
        }
        this.emit();
        this.flush();
      },
      (error: unknown) => {
        // The worker is gone or a batch threw: orthogonal edges keep drawing as the index builds them rather than
        // retrying every frame. Said once, loudly, so a routing failure is never silent.
        this.inFlight = false;
        if (this.stopped) {
          return;
        }
        this.stopped = true;
        this.routes.clear();
        this.emit();
        console.warn(`Draughtsman: connector routing stopped, so orthogonal edges are drawn unrouted: ${error instanceof Error ? error.message : String(error)}`);
      },
    );
  }
}
