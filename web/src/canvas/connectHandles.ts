/**
 * Where the select tool's connector affordances sit and what a press lands on. Pure functions over canvas units, so
 * the hit-testing is unit-testable and the overlay that draws the affordances reads the same positions.
 *
 * Connection handles: a hovered node, and the node when it is the only thing selected, show one handle per port
 * (canvas/ports.ts `connectablePorts`), set out from the port along the side it leaves by. Dragging one starts a
 * connector from that port without switching to the connector tool, the way Lucidchart and draw.io work. They sit
 * outside the shape so they never cover a selected node's resize handles, whose middle four sit exactly on the default
 * ports. A lone selected node also has SelectionOverlay's rotate handle above its top edge, which is where its top
 * connection handle would be; a handle that would come within reach of it steps sideways along its side instead, so
 * the two never share a hit area and the rotate handle keeps its place. Edge endpoints: a selected edge shows a handle near each end of the route it is drawn along, set in from the
 * end along the route far enough that its hit circle clears the port's and the connection handle's there; dragging one
 * re-attaches that end. The same clearance holds against every other shape's ports and connection handles near the
 * route, so an end handle is pushed further along past a neighbour's, or hidden when no clear spot is left: a visible
 * end handle is never under a connection handle. Connection handles are hit-tested first, so a press where they meet
 * always fans out. A route too short to fit both end handles clear of those zones and of each other shows none, so
 * re-attaching a very short connector needs zooming in until it is long enough on screen.
 *
 * Every size here is in screen pixels and is divided by the zoom before it meets world coordinates, so the targets
 * stay the same size to the hand at every zoom.
 */

import type { Id, Point, Port } from '../model/types.ts';
import type { EdgeEndName } from '../model/connect.ts';
import type { DocumentIndex, PlacedNode } from './documentIndex.ts';
import { connectablePorts, nodeAt, PORT_HIT_RADIUS, portPosition, portSide } from './ports.ts';
import type { Side } from './routing.ts';

/** How far a connection handle sits out from its port, in screen pixels. */
export const CONNECT_HANDLE_OFFSET = 16;
/** How close, in screen pixels, a press must be to a connection handle or an edge endpoint to take it. */
export const CONNECT_HIT_RADIUS = PORT_HIT_RADIUS;
/** How far along its route, in screen pixels, a selected edge's end handle sits in from that end. */
export const EDGE_END_HANDLE_INSET = Math.max(40, CONNECT_HANDLE_OFFSET + 2 * CONNECT_HIT_RADIUS);
/** The shortest route, in screen pixels, with room for both end handles clear of each other: shorter shows none. */
export const EDGE_END_HANDLE_MIN_ROUTE = 2 * EDGE_END_HANDLE_INSET + 2 * CONNECT_HIT_RADIUS;

/** Where SelectionOverlay draws a lone selected node's rotate handle: this far, in screen pixels, above the middle of its top edge, turned with the node. */
export const ROTATE_HANDLE_OFFSET = 24;
/** The rotate handle's radius in screen pixels. */
export const ROTATE_HANDLE_RADIUS = 5;
/** The screen-pixel gap a connection handle's reach keeps from the rotate handle's disc. */
const ROTATE_CLEARANCE = CONNECT_HIT_RADIUS + ROTATE_HANDLE_RADIUS + 2;

const OUTWARD: Record<Side, Point> = { top: { x: 0, y: -1 }, right: { x: 1, y: 0 }, bottom: { x: 0, y: 1 }, left: { x: -1, y: 0 } };

/** The world position of a node's rotate handle at a zoom: above the middle of its top edge, turned with the node. */
export function rotateHandlePosition(node: PlacedNode, zoom: number): Point {
  const at = portPosition(node.geometry, { x: 0.5, y: 0 });
  const radians = ((node.geometry.rotation ?? 0) * Math.PI) / 180;
  const distance = ROTATE_HANDLE_OFFSET / zoom;
  return { x: at.x + Math.sin(radians) * distance, y: at.y - Math.cos(radians) * distance };
}

/** The node that shows a rotate handle: the only selected element, as SelectionOverlay draws it. */
export function rotateHandleNode(selection: ReadonlySet<Id>): Id | undefined {
  const [only] = selection;
  return selection.size === 1 ? only : undefined;
}

/**
 * The world position of the connection handle for one of a node's ports at a zoom. `rotateHandle` says the node shows
 * the rotate handle; a handle that would then come within ROTATE_CLEARANCE of it steps along its side until it does not.
 */
export function connectHandlePosition(node: PlacedNode, port: Pick<Port, 'x' | 'y'>, zoom: number, rotateHandle = false): Point {
  const at = portPosition(node.geometry, port);
  const out = OUTWARD[portSide(node.geometry, port)];
  const distance = CONNECT_HANDLE_OFFSET / zoom;
  const handle = { x: at.x + out.x * distance, y: at.y + out.y * distance };
  if (!rotateHandle) {
    return handle;
  }
  const rotator = rotateHandlePosition(node, zoom);
  const clearance = ROTATE_CLEARANCE / zoom;
  const dx = handle.x - rotator.x;
  const dy = handle.y - rotator.y;
  if (Math.hypot(dx, dy) >= clearance) {
    return handle;
  }
  // Split the offset from the rotate handle into along-the-side (a quarter-turn clockwise of outward) and outward parts.
  const along = { x: -out.y, y: out.x };
  const alongPart = dx * along.x + dy * along.y;
  const outPart = dx * out.x + dy * out.y;
  // Step to the nearer side of the rotate handle, clockwise on an exact tie, just far enough to clear it.
  const step = (alongPart < 0 ? -1 : 1) * Math.sqrt(clearance * clearance - outPart * outPart) - alongPart;
  return { x: handle.x + along.x * step, y: handle.y + along.y * step };
}

/**
 * The node whose connection handles the pointer is among: the topmost node whose box, grown far enough that the pointer
 * can travel from the shape out to a handle without the handles vanishing on the way, contains it, when that node is
 * the one under the pointer or inside it (a child in the container the pointer is over); otherwise the node under the
 * pointer, so a neighbour's grown box never takes the shape actually under it.
 */
export function handleHoverNode(index: DocumentIndex, at: Point, zoom: number): PlacedNode | undefined {
  const under = nodeAt(index, at, 0);
  const padded = nodeAt(index, at, (CONNECT_HANDLE_OFFSET + CONNECT_HIT_RADIUS) / zoom);
  return under === undefined || (padded !== undefined && isWithin(index, padded, under.id)) ? padded : under;
}

/** Whether a node is `ancestor` itself or nested, at any depth, inside it. */
function isWithin(index: DocumentIndex, node: PlacedNode, ancestor: Id): boolean {
  const seen = new Set<Id>();
  let current: PlacedNode | undefined = node;
  while (current !== undefined && !seen.has(current.id)) {
    if (current.id === ancestor) {
      return true;
    }
    seen.add(current.id);
    current = current.parent === undefined ? undefined : index.placed.get(current.parent);
  }
  return false;
}

/**
 * The node and port of the connection handle within reach of a world point, nearest first, among `nodes`. `rotating` is
 * the node showing the rotate handle, whose handles step clear of it (connectHandlePosition). `hitRadius` (screen
 * pixels, CONNECT_HIT_RADIUS by default) is the caller's touch-aware override: DiagramCanvas passes TOUCH_HIT_RADIUS
 * for a pen or touch press, leaving a mouse's reach unchanged.
 */
export function connectHandleAt(nodes: readonly PlacedNode[], at: Point, zoom: number, rotating?: Id, hitRadius: number = CONNECT_HIT_RADIUS): { node: PlacedNode; port: Port } | undefined {
  const reach = hitRadius / zoom;
  let best: { node: PlacedNode; port: Port; distance: number } | undefined;
  for (const node of nodes) {
    for (const port of connectablePorts(node)) {
      const p = connectHandlePosition(node, port, zoom, node.id === rotating);
      const distance = Math.hypot(p.x - at.x, p.y - at.y);
      if (distance <= reach && (best === undefined || distance < best.distance)) {
        best = { node, port, distance };
      }
    }
  }
  return best === undefined ? undefined : { node: best.node, port: best.port };
}

/** The point a distance along a polyline from its first point, or its last point when the polyline is shorter. */
function along(points: readonly Point[], distance: number): Point | undefined {
  let left = distance;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1];
    const b = points[i];
    if (a === undefined || b === undefined) {
      break;
    }
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    if (length >= left && length > 0) {
      const t = left / length;
      return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
    }
    left -= length;
  }
  return points[points.length - 1];
}

/**
 * The world positions of every port and connection handle, on any node, whose hit circle could meet an end handle
 * somewhere on a route at a zoom: the zones an end handle must keep clear of (edgeEndHandlePositions). Any node can
 * show its connection handles, since hovering near it does, and they win a press, so a neighbouring shape counts as much
 * as the edge's own ends.
 */
export function connectZones(index: DocumentIndex, points: readonly Point[], zoom: number): Point[] {
  if (points.length === 0) {
    return [];
  }
  const pad = (CONNECT_HANDLE_OFFSET + 2 * CONNECT_HIT_RADIUS) / zoom;
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  const minX = Math.min(...xs) - pad;
  const maxX = Math.max(...xs) + pad;
  const minY = Math.min(...ys) - pad;
  const maxY = Math.max(...ys) + pad;
  const zones: Point[] = [];
  for (const node of index.placed.values()) {
    const bounds = index.nodeBounds.get(node.id);
    if (bounds === undefined || bounds.x > maxX || bounds.x + bounds.w < minX || bounds.y > maxY || bounds.y + bounds.h < minY) {
      continue;
    }
    for (const port of connectablePorts(node)) {
      zones.push(portPosition(node.geometry, port), connectHandlePosition(node, port, zoom));
    }
  }
  return zones;
}

/** A polyline's length. */
function routeLength(points: readonly Point[]): number {
  let total = 0;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1];
    const b = points[i];
    if (a !== undefined && b !== undefined) {
      total += Math.hypot(b.x - a.x, b.y - a.y);
    }
  }
  return total;
}

/**
 * Where a drawn route's end handles sit at a zoom. `from` is at least EDGE_END_HANDLE_INSET screen pixels along the
 * route from its first point, and further along if that is what it takes for its hit circle to clear every port and
 * connection handle hit circle in `zones` (connectZones); `to` likewise back from its last point. An end handle that
 * finds no clear spot, or would meet the other end's, is left out: it is not drawn and a press there never re-attaches.
 * A route under EDGE_END_HANDLE_MIN_ROUTE screen pixels, or with neither handle clear, has none (undefined).
 */
export function edgeEndHandlePositions(points: readonly Point[], zoom: number, zones: readonly Point[] = []): Partial<Record<EdgeEndName, Point>> | undefined {
  const total = routeLength(points);
  if (total < EDGE_END_HANDLE_MIN_ROUTE / zoom) {
    return undefined;
  }
  const clearance = (2 * CONNECT_HIT_RADIUS) / zoom;
  const step = 1 / zoom;
  // How far along the route every point is still inside some zone's clearance: 0 when `p` itself is clear. A point that
  // far along is no closer to that zone than its clearance, so nothing clear is skipped.
  const blocked = (p: Point): number => zones.reduce((most, z) => Math.max(most, clearance - Math.hypot(z.x - p.x, z.y - p.y)), 0);
  // The distance along `route` of the first clear spot from the inset on, or undefined when there is none.
  const place = (route: readonly Point[]): { distance: number; at: Point } | undefined => {
    for (let distance = EDGE_END_HANDLE_INSET / zoom; distance <= total - EDGE_END_HANDLE_INSET / zoom; ) {
      const at = along(route, distance);
      if (at === undefined) {
        return undefined;
      }
      const skip = blocked(at);
      if (skip <= 0) {
        return { distance, at };
      }
      distance += Math.max(skip, step);
    }
    return undefined;
  };
  const from = place(points);
  const to = place([...points].reverse());
  if (from !== undefined && to !== undefined && total - from.distance - to.distance < clearance) {
    return undefined;
  }
  if (from === undefined && to === undefined) {
    return undefined;
  }
  return { ...(from === undefined ? {} : { from: from.at }), ...(to === undefined ? {} : { to: to.at }) };
}

/** A selected edge's end handles, as edgeEndHandlePositions places them; an edge with none is absent. */
export type EdgeEndHandles = ReadonlyMap<Id, Partial<Record<EdgeEndName, Point>>>;

/**
 * The end handles of every selected edge at a zoom, each along the route `pointsOf` says it is drawn along and clear of
 * connectZones. Computed once per selection, route and zoom change, and shared by the overlay that draws them and the
 * press that hit-tests them (endHandleAt), so a pan costs nothing.
 */
export function selectedEdgeEnds(index: DocumentIndex, selection: ReadonlySet<Id>, zoom: number, pointsOf: (id: Id) => readonly Point[] | undefined): EdgeEndHandles {
  const out = new Map<Id, Partial<Record<EdgeEndName, Point>>>();
  for (const id of selection) {
    const points = index.edgesById.has(id) ? pointsOf(id) : undefined;
    const handles = points === undefined ? undefined : edgeEndHandlePositions(points, zoom, connectZones(index, points, zoom));
    if (handles !== undefined) {
      out.set(id, handles);
    }
  }
  return out;
}

/** Which of an edge's end handles a world point is on at a zoom, if either. `hitRadius` is as connectHandleAt's. */
export function endHandleAt(handles: Partial<Record<EdgeEndName, Point>>, at: Point, zoom: number, hitRadius: number = CONNECT_HIT_RADIUS): EdgeEndName | undefined {
  const reach = hitRadius / zoom;
  if (handles.from !== undefined && Math.hypot(handles.from.x - at.x, handles.from.y - at.y) <= reach) {
    return 'from';
  }
  return handles.to !== undefined && Math.hypot(handles.to.x - at.x, handles.to.y - at.y) <= reach ? 'to' : undefined;
}

/** Which of a drawn route's end handles (edgeEndHandlePositions, over the same `zones`) a world point is on, if either. */
export function edgeEndAt(points: readonly Point[], at: Point, zoom: number, zones: readonly Point[] = [], hitRadius: number = CONNECT_HIT_RADIUS): EdgeEndName | undefined {
  const handles = edgeEndHandlePositions(points, zoom, zones);
  return handles === undefined ? undefined : endHandleAt(handles, at, zoom, hitRadius);
}

/** The nodes whose connection handles show: the hovered node, then the only selected element when it is a node. */
export function handleNodes(index: DocumentIndex, hovered: Id | undefined, selection: ReadonlySet<Id>): PlacedNode[] {
  const out: PlacedNode[] = [];
  const hoveredNode = hovered === undefined ? undefined : index.placed.get(hovered);
  if (hoveredNode !== undefined) {
    out.push(hoveredNode);
  }
  const [only] = selection;
  const selected = selection.size === 1 && only !== undefined ? index.placed.get(only) : undefined;
  if (selected !== undefined && selected !== hoveredNode) {
    out.push(selected);
  }
  return out;
}
