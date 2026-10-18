/**
 * Where connectors attach (the M2 editor checklist, items 8 and 10). Pure functions over canvas units.
 *
 * A node offers its declared `ports`. A node that declares none offers defaultPorts(shape), the four side attachments on its outline,
 * so a freshly drawn or imported shape can be connected without first being given ports; connecting to one of those
 * declares the default set on the node (model/connect.ts), so the edge names a port that exists and the node keeps
 * offering all four afterwards. Port positions are fractions of the node's box and follow it when it moves, resizes or
 * rotates, which is what re-attaches a connector without the edge changing. A rotated node's ports turn with it about
 * its centre, so they stay on the drawn shape, and each leaves by the axis side its rotated outward normal is nearest.
 * The port markers, the connector tool's hit-testing and the router's attachments all read portPosition and portSide,
 * so a marker, a click target and a route end always coincide.
 *
 * Two or more connectors declaring the same (node, port) endpoint would otherwise leave by the exact same point in
 * the exact same direction and paint on top of each other for their shared initial run (M2 final-check Finding D).
 * sharedPortOffsets groups edges by their declared endpoints and gives every one but the first (document order) a
 * small tangential nudge; offsetAlongSide applies it to a port point without moving it off the node's edge. This
 * never touches portPosition/portSide themselves (a marker, hover target or new connection still lands on the true
 * port), only the fixed end a drawn line or route starts or ends from.
 */

import { basicShapeName } from '../model/basicShapes.ts';
import { bpmnShape } from '../model/bpmnShapes.ts';
import { columnPort, columnPortAt, columnRowOf, columnSideFacing, isColumnPortId } from '../model/columnPorts.ts';
import { defaultPorts, DEFAULT_PORTS } from '../model/connect.ts';
import type { Edge, Geometry, Id, Node, Point, Port } from '../model/types.ts';
import type { DocumentIndex, PlacedNode } from './documentIndex.ts';
import { rectCentre, type Rect } from './geometry.ts';
import type { Side } from './routing.ts';

export { DEFAULT_PORTS };

/**
 * How close, in screen pixels, the pointer must be to a port to be on it rather than on the node's body. Twenty pixels
 * across, the size of the connection handles and edge ends (connectHandles.ts), so every connector target is the same
 * size to the hand.
 */
export const PORT_HIT_RADIUS = 10;

/**
 * How close, in screen pixels, a pen or touch press must be to a port, connection handle or edge end handle to be on
 * it: 12, so the target is at least 24 CSS pixels across (WCAG 2.5.8's minimum), since a finger or stylus has nothing
 * like a mouse's pointer precision. Never used for a mouse press, whose PORT_HIT_RADIUS is unchanged.
 */
export const TOUCH_HIT_RADIUS = 12;

/** The ports a node offers the connector tool: its own, or the shape’s default side attachments when it declares none. */
export function connectablePorts(node: Node): readonly Port[] {
  return node.ports !== undefined && node.ports.length > 0 ? node.ports : defaultPorts(node.shape);
}

/**
 * The port an edge endpoint names on a node: one the node declares, or else a column of an ER entity (`col:<name>`,
 * model/columnPorts.ts), which is derived from the entity's rows and never stored. A column port sits on the side of
 * the table facing `towards`; undefined for no port, and for a name the node neither declares nor has a row for, so
 * the caller falls back to the node itself.
 */
export function endpointPort(node: PlacedNode, portId: Id | undefined, towards: Point): Port | undefined {
  if (portId === undefined) {
    return undefined;
  }
  return node.ports?.find((port) => port.id === portId) ?? columnPort(node, portId, columnSideFacing(node.geometry, towards));
}

/**
 * The point an edge's other end is aimed from: the declared port's position, else the node's centre. A column port
 * aims from the centre too, since which side of the table it sits on is decided by this very point.
 */
export function aimPoint(node: PlacedNode, portId: Id | undefined): Point {
  const port = portId === undefined ? undefined : node.ports?.find((candidate) => candidate.id === portId);
  return port !== undefined ? portPosition(node.geometry, port) : rectCentre(node.geometry);
}

/**
 * The point `self`'s end is aimed at when it attaches to `other`: `other`'s aim point (`aimPoint`), except between two
 * column ports whose tables overlap horizontally. There the facing side is the wrong one for both ends (the tables are
 * stacked, each centre lies on the far side of the other's, so the ends leave by opposite sides and the connector hooks
 * round the table), so both ends take the same side, the one where the two tables' edges lie closest together (a tie is
 * the right side), which makes a short C-shaped bracket. Symmetric, so each end reaches the same side on its own. The
 * returned point is only ever read for its side (`columnSideFacing`); a rule mirrored by SvgRenderer.ColumnAimX.
 */
export function towardsPoint(self: PlacedNode, selfPort: Id | undefined, other: PlacedNode, otherPort: Id | undefined): Point {
  const aim = aimPoint(other, otherPort);
  const declared = (node: PlacedNode, port: Id | undefined): boolean => node.ports?.some((candidate) => candidate.id === port) === true;
  if (declared(self, selfPort) || declared(other, otherPort) || columnRowOf(self, selfPort) === undefined || columnRowOf(other, otherPort) === undefined) {
    return aim;
  }
  const a = self.geometry;
  const b = other.geometry;
  if (a.x >= b.x + b.w || b.x >= a.x + a.w) {
    return aim;
  }
  const side = Math.abs(a.x + a.w - (b.x + b.w)) <= Math.abs(a.x - b.x) ? 1 : -1;
  return { x: rectCentre(a).x + side, y: aim.y };
}

/**
 * The port a connector release or press at a world point attaches to on `node`: the column row under the point when the
 * node is an entity and the point is over its rows, else the declared or default port within `radius`, else the nearest.
 */
export function attachmentAt(node: PlacedNode, at: Point, radius: number): Port {
  return columnPortAt(node, at) ?? portAt(node, at, radius) ?? nearestPort(node, at);
}

/**
 * What a connector press or release at a world point lands on: `pinned` when the pointer is on the port itself (inside
 * its ring, or over an entity's column row), so the user chose it; otherwise the pointer is on the node's body and
 * `port` is only the nearest one, a placeholder `pairPorts` replaces by the one that gives the cleanest route.
 */
export interface PortHit {
  port: Port;
  pinned: boolean;
}

/** `attachmentAt`, saying whether the port was chosen (pinned) or is only the nearest to a drop on the body. */
export function attachmentHit(node: PlacedNode, at: Point, radius: number): PortHit {
  const chosen = columnPortAt(node, at) ?? portAt(node, at, radius);
  return chosen === undefined ? { port: nearestPort(node, at), pinned: false } : { port: chosen, pinned: true };
}

/**
 * The ports a connector between two nodes uses. A pinned end keeps its port; a body end takes whichever of its node's
 * ports lies closest to the other end's port (the pinned one, or the best of the other node's), so a drag from body to
 * body leaves by the side facing the target and arrives at the side facing the source, never round the back. A tie
 * goes to the earlier port, source first. Two pinned ends are returned as they are.
 */
export function pairPorts(a: { node: PlacedNode; hit: PortHit }, b: { node: PlacedNode; hit: PortHit }): { a: Port; b: Port } {
  if (a.hit.pinned && b.hit.pinned) {
    return { a: a.hit.port, b: b.hit.port };
  }
  const ofA = a.hit.pinned ? [a.hit.port] : connectablePorts(a.node);
  const ofB = b.hit.pinned ? [b.hit.port] : connectablePorts(b.node);
  let best = { a: a.hit.port, b: b.hit.port, distance: Infinity };
  for (const pa of ofA) {
    const from = portPosition(a.node.geometry, pa);
    for (const pb of ofB) {
      const to = portPosition(b.node.geometry, pb);
      const distance = Math.hypot(from.x - to.x, from.y - to.y);
      if (distance < best.distance) {
        best = { a: pa, b: pb, distance };
      }
    }
  }
  return { a: best.a, b: best.b };
}

/** The port of a body-dropped end that lies closest to a fixed point: where a re-attached end lands, `towards` being the end that stays put. */
export function portFacing(node: PlacedNode, towards: Point): Port {
  return nearestPort(node, towards);
}

/** The sides in clockwise order, so turning a side by a quarter-turn clockwise is one step along this list. */
const SIDES: readonly Side[] = ['top', 'right', 'bottom', 'left'];

/** cos and sin of a clockwise rotation in degrees, exact for quarter-turns so a 90-degree port lands on whole units. */
function turn(degrees: number): { cos: number; sin: number } {
  if (degrees % 90 === 0) {
    const quarter = (((degrees / 90) % 4) + 4) % 4;
    return { cos: [1, 0, -1, 0][quarter] ?? 1, sin: [0, 1, 0, -1][quarter] ?? 0 };
  }
  const radians = (degrees * Math.PI) / 180;
  return { cos: Math.cos(radians), sin: Math.sin(radians) };
}

/** A port's absolute position on a node's drawn shape: on its box, turned about the centre by the node's rotation. */
export function portPosition(geometry: Geometry, port: Pick<Port, 'x' | 'y'>): Point {
  const x = geometry.x + geometry.w * port.x;
  const y = geometry.y + geometry.h * port.y;
  const rotation = geometry.rotation ?? 0;
  if (rotation % 360 === 0) {
    return { x, y };
  }
  const cx = geometry.x + geometry.w / 2;
  const cy = geometry.y + geometry.h / 2;
  const { cos, sin } = turn(rotation);
  return { x: cx + (x - cx) * cos - (y - cy) * sin, y: cy + (x - cx) * sin + (y - cy) * cos };
}

/**
 * The side of its node a port leaves by. On the unrotated box that is the nearest edge, in fractions; a port equally
 * near two edges (a corner, or the centre) takes the first of top, right, bottom, left, so the answer is the same on
 * every render. The node's rotation then turns that side's outward normal, snapped to the nearest axis; a normal
 * exactly between two axes takes the clockwise one, so the four sides of a node always leave by four different sides.
 */
export function portSide(geometry: Geometry, port: Pick<Port, 'x' | 'y'>): Side {
  const top = port.y;
  const right = 1 - port.x;
  const bottom = 1 - port.y;
  const left = port.x;
  const nearest = Math.min(top, right, bottom, left);
  const local = top === nearest ? 0 : right === nearest ? 1 : bottom === nearest ? 2 : 3;
  const quarters = Math.round((geometry.rotation ?? 0) / 90);
  return SIDES[(((local + quarters) % 4) + 4) % 4] ?? 'top';
}

/**
 * How far apart, in canvas units, two connectors sharing a port fan out (M2 final Finding D). Sixteen keeps two
 * arrowheads clear of each other (QA-07: at 6 they sat on top of one another); nudgedPortPoint keeps the fan on a
 * short side. SvgRenderer's `EdgeAttachment.SharedPortStep` mirrors it.
 */
export const SHARED_PORT_STEP = 16;

/** The fan step for connectors sharing a column port (model/columnPorts.ts), which is one table row high. */
const COLUMN_PORT_STEP = 6;

/** How close, in canvas units, a fanned connector may come to the end of the side it leaves by. */
const SIDE_MARGIN = 4;

/** `offset` limited so the fanned point stays on the side it leaves (the local frame's `span`), keeping SIDE_MARGIN from each end. */
function withinSide(offset: number, span: number): number {
  const limit = Math.max(0, span / 2 - SIDE_MARGIN);
  return Math.max(-limit, Math.min(limit, offset));
}

/**
 * Every edge endpoint that shares a declared (node, port) with at least one other, keyed `${edge.id}:from` or
 * `${edge.id}:to`, mapped to its tangential offset: 0 for the first sharer in document order, then alternating
 * sides at growing distance for the rest (+STEP, -STEP, +2*STEP, -2*STEP, ...), the same fan-out shape
 * labelLayout.ts uses for overlapping labels. An endpoint with no declared port, or sharing its port with no one,
 * is left out of the map entirely (an absent entry and an explicit 0 both mean "don't move it"). A self-loop (both
 * ends on the same node) never contributes or is offset: its exact-coincidence and two-distinct-ports cases
 * (documentIndex.ts `edgePolyline`) depend on its own ports resolving to precisely the position they declare.
 */
export function sharedPortOffsets(edges: readonly Edge[]): ReadonlyMap<string, number> {
  const groups = new Map<string, string[]>();
  const add = (node: Id, port: Id | undefined, key: string): void => {
    if (port === undefined) {
      return;
    }
    const groupKey = `${node}#${port}`;
    const list = groups.get(groupKey);
    if (list) {
      list.push(key);
    } else {
      groups.set(groupKey, [key]);
    }
  };
  for (const edge of edges) {
    if (edge.from.node === edge.to.node) {
      continue;
    }
    add(edge.from.node, edge.from.port, `${edge.id}:from`);
    add(edge.to.node, edge.to.port, `${edge.id}:to`);
  }
  const offsets = new Map<string, number>();
  for (const [groupKey, list] of groups) {
    if (list.length < 2) {
      continue;
    }
    // A column port is one row of an entity table, about a row high: a full step would land in the next row.
    const step = isColumnPortId(groupKey.slice(groupKey.indexOf('#') + 1)) ? COLUMN_PORT_STEP : SHARED_PORT_STEP;
    list.forEach((key, i) => {
      if (i === 0) {
        return;
      }
      const sign = i % 2 === 1 ? 1 : -1;
      const magnitude = Math.ceil(i / 2) * step;
      offsets.set(key, sign * magnitude);
    });
  }
  return offsets;
}

/** Nudges a port point tangentially along its node's side (never away from it) by `offset` canvas units. */
export function offsetAlongSide(point: Point, side: Side, offset: number): Point {
  if (offset === 0) {
    return point;
  }
  return side === 'top' || side === 'bottom' ? { x: point.x + offset, y: point.y } : { x: point.x, y: point.y + offset };
}

/** The basic library's ellipse, whose outline is the inscribed ellipse like a BPMN event's. */
const ELLIPSE_SHAPE = basicShapeName('ellipse');

/**
 * Whether a shape's drawn outline is an ellipse or a diamond inscribed in its box. The box's side midpoints are on
 * such an outline but nothing else along the side is, so a point nudged along the side or clipped to the box leaves a
 * gap (QA-01). `diamond` is the decision shape; BPMN events and the basic ellipse (ellipse) and gateways (diamond) are the same outlines.
 * SvgRenderer's `EdgeAttachment.OutlineKind` mirrors this.
 */
export function outlineKind(shape: string): 'diamond' | 'ellipse' | undefined {
  if (shape === 'diamond') {
    return 'diamond';
  }
  if (shape === ELLIPSE_SHAPE) {
    return 'ellipse';
  }
  const family = bpmnShape(shape)?.family;
  return family === 'gateway' ? 'diamond' : family === 'event' ? 'ellipse' : undefined;
}

/** The fraction of the half-height (or half-width) still inside the outline at `t` of the half-width (or half-height). */
function outlineReach(kind: 'diamond' | 'ellipse', t: number): number {
  return kind === 'diamond' ? 1 - Math.abs(t) : Math.sqrt(Math.max(0, 1 - t * t));
}

/**
 * Where a connector leaves an ellipse or diamond filling `geometry`, heading for `toward`. The line from the centre
 * crosses the outline there; with `aligned` (an orthogonal connector whose next point is a routed bend) it instead
 * leaves along the axis the bend lies on, straight out of the outline, so the first segment stays horizontal or
 * vertical instead of skewing from the box. A target inside the outline is returned unchanged, and an aligned line
 * whose bend lies outside the outline's shadow falls back to the centre ray.
 */
export function outlineExit(geometry: Geometry, kind: 'diamond' | 'ellipse', toward: Point, aligned: boolean): Point {
  const centre = rectCentre(geometry);
  const { cos, sin } = turn(geometry.rotation ?? 0);
  const worldX = toward.x - centre.x;
  const worldY = toward.y - centre.y;
  const halfW = geometry.w / 2;
  const halfH = geometry.h / 2;
  const nx = (worldX * cos + worldY * sin) / halfW;
  const ny = (-worldX * sin + worldY * cos) / halfH;
  let lx: number;
  let ly: number;
  if (aligned && Math.abs(ny) >= Math.abs(nx) && Math.abs(nx) < 1) {
    lx = nx;
    ly = Math.sign(ny) * Math.min(Math.abs(ny), outlineReach(kind, nx));
  } else if (aligned && Math.abs(nx) > Math.abs(ny) && Math.abs(ny) < 1) {
    lx = Math.sign(nx) * Math.min(Math.abs(nx), outlineReach(kind, ny));
    ly = ny;
  } else {
    const reach = kind === 'diamond' ? Math.abs(nx) + Math.abs(ny) : Math.hypot(nx, ny);
    const scale = reach === 0 ? 1 : Math.min(1, 1 / reach);
    lx = nx * scale;
    ly = ny * scale;
  }
  const x = lx * halfW;
  const y = ly * halfH;
  return { x: centre.x + x * cos - y * sin, y: centre.y + x * sin + y * cos };
}

/**
 * A declared port of an ellipse or diamond node, nudged `offset` units tangentially but kept on the outline (QA-01:
 * sliding along the box's side leaves the outline, so a second edge from a diamond's vertex started in mid-air). Done
 * in the node's own frame, then turned with it.
 */
export function outlinePortPoint(geometry: Geometry, kind: 'diamond' | 'ellipse', port: Pick<Port, 'x' | 'y'>, offset: number): Point {
  const local = portSide({ ...geometry, rotation: 0 }, port);
  const halfW = geometry.w / 2;
  const halfH = geometry.h / 2;
  let x = (port.x - 0.5) * geometry.w;
  let y = (port.y - 0.5) * geometry.h;
  if (local === 'top' || local === 'bottom') {
    x = Math.max(-halfW, Math.min(halfW, x + withinSide(offset, geometry.w)));
    y = (local === 'top' ? -1 : 1) * halfH * outlineReach(kind, x / halfW);
  } else {
    y = Math.max(-halfH, Math.min(halfH, y + withinSide(offset, geometry.h)));
    x = (local === 'left' ? -1 : 1) * halfW * outlineReach(kind, y / halfH);
  }
  const centre = rectCentre(geometry);
  const { cos, sin } = turn(geometry.rotation ?? 0);
  return { x: centre.x + x * cos - y * sin, y: centre.y + x * sin + y * cos };
}

/**
 * A port's point for an edge end: where it sits, nudged `offset` units along its side when another edge shares the
 * port (`sharedPortOffsets`), and kept on the drawn outline for an ellipse or diamond.
 */
export function nudgedPortPoint(node: PlacedNode, port: Pick<Port, 'x' | 'y'>, offset: number): Point {
  const kind = offset === 0 ? undefined : outlineKind(node.shape);
  if (kind !== undefined) {
    return outlinePortPoint(node.geometry, kind, port, offset);
  }
  const point = portPosition(node.geometry, port);
  if (offset === 0) {
    return point;
  }
  const along = portSide({ ...node.geometry, rotation: 0 }, port);
  return offsetAlongSide(point, portSide(node.geometry, port), withinSide(offset, along === 'top' || along === 'bottom' ? node.geometry.w : node.geometry.h));
}

/** The port nearest a world point: where a release on the node's body attaches. */
export function nearestPort(node: PlacedNode, at: Point): Port {
  const away = (port: Port): number => {
    const p = portPosition(node.geometry, port);
    return Math.hypot(p.x - at.x, p.y - at.y);
  };
  // connectablePorts never returns an empty list; on a tie the first port listed wins.
  return connectablePorts(node).reduce((best, port) => (away(port) < away(best) ? port : best));
}

/** The port within `radius` world units of a point, nearest first, or undefined when the point is on no port. */
export function portAt(node: PlacedNode, at: Point, radius: number): Port | undefined {
  const port = nearestPort(node, at);
  const p = portPosition(node.geometry, port);
  return Math.hypot(p.x - at.x, p.y - at.y) <= radius ? port : undefined;
}

function contains(rect: Rect, p: Point, pad: number): boolean {
  return p.x >= rect.x - pad && p.x <= rect.x + rect.w + pad && p.y >= rect.y - pad && p.y <= rect.y + rect.h + pad;
}

/**
 * The topmost drawn node under a world point, grown by `pad` so a port sitting on the boundary is still on its node
 * when the pointer is just outside it. Topmost is the render order: document order, children after their container.
 */
export function nodeAt(index: DocumentIndex, at: Point, pad: number): PlacedNode | undefined {
  let hit: PlacedNode | undefined;
  const visit = (nodes: readonly PlacedNode[]): void => {
    for (const node of nodes) {
      const bounds = index.nodeBounds.get(node.id);
      if (bounds !== undefined && bounds.w > 0 && bounds.h > 0 && contains(bounds, at, pad)) {
        hit = node;
      }
      const children = index.childrenOf.get(node.id);
      if (children !== undefined) {
        visit(children);
      }
    }
  };
  visit(index.rootNodes);
  return hit;
}
