/**
 * Creating a connector (the M2 editor checklist, item 8) as one local edit on the editor's Y.Doc. The canvas's
 * connector tool reports which port of which node the gesture started and ended on; this writes the edge.
 *
 * The new edge is `orthogonal`, so it is drawn by the router (canvas/edgeRoutes.ts) and nothing about its route is
 * stored (decision D4 (a)). Its kind is `flow`, the first edge kind of both the architecture and the flowchart
 * vocabularies and the one the palette's quick-add uses as the document's default. When an end attaches to one of a
 * node's default side ports (the node declares no ports of its own; canvas/ports.ts), the default set is declared on
 * that node in the same transaction, so the edge names a port the node really has and the node keeps offering all
 * four afterwards. Everything happens in one LOCAL_ORIGIN transaction: one undo step, and invisible to anyone else's
 * undo.
 */

import * as Y from 'yjs';
import { randomIdGenerator, type IdGenerator } from './clipboard.ts';
import { columnRowOf, isColumnPortId } from './columnPorts.ts';
import { EDGES_MAP, LOCAL_ORIGIN, NODES_MAP, ORDER_FIELD } from './editor.ts';
import type { Edge, Id, Port, Props } from './types.ts';
import { basicPorts, basicShape } from './basicShapes.ts';
import { iconForShape, iconPortFractions } from './icons.ts';
import { UML_METRICS, umlShapeName } from './umlShapes.ts';

/** The four side midpoints a node without declared ports offers. Their ids are also the side each one leaves by. */
export const DEFAULT_PORTS: readonly Port[] = [
  { id: 'top', x: 0.5, y: 0 },
  { id: 'right', x: 1, y: 0.5 },
  { id: 'bottom', x: 0.5, y: 1 },
  { id: 'left', x: 0, y: 0.5 },
];

/** One end of a connection the connector tool is making: a node and the port on it. */
export interface ConnectionEnd {
  node: Id;
  port: Port;
}

/** A finished connector gesture: the canvas reports it and the editor writes the edge. */
export interface Connection {
  from: ConnectionEnd;
  to: ConnectionEnd;
}

/** The kind a connector drawn with the connector tool is given. */
export const NEW_EDGE_KIND = 'flow';

/** How many colliding proposals one new id tolerates before deciding the generator is stuck. */
const MAX_ID_ATTEMPTS = 1000;

type ElementMap = Y.Map<unknown>;

/** Side ports on the outline of the presentation shape; declared coordinates always remain authoritative. */
export function defaultPorts(shape: string): readonly Port[] {
  if (shape === 'document') {
    return DEFAULT_PORTS.map((port) => port.id === 'bottom' ? { ...port, y: 0.85 } : port);
  }
  if (shape === 'manual-input') {
    return DEFAULT_PORTS.map((port) => port.id === 'top' ? { ...port, y: 0.125 } : port);
  }
  if (shape === 'manual-operation') {
    // Trapezoid sloped sides: at half height the left/right edge sits at 0.1 / 0.9 of the width.
    return DEFAULT_PORTS.map((port) => (port.id === 'left' ? { ...port, x: 0.1 } : port.id === 'right' ? { ...port, x: 0.9 } : port));
  }
  if (shape === 'merge' || shape === 'extract') {
    // Triangles: the side midpoints land on the sloped edges a quarter and three quarters across.
    return DEFAULT_PORTS.map((port) => (port.id === 'left' ? { ...port, x: 0.25 } : port.id === 'right' ? { ...port, x: 0.75 } : port));
  }
  if (shape === 'multi-document') {
    // The front (bottom-most) page is inset, so its wavy bottom crosses the box's own centre below the line: the
    // bottom port sits at the front page's centre (0.4 across, 0.88 down).
    return DEFAULT_PORTS.map((port) => port.id === 'bottom' ? { ...port, x: 0.4, y: 0.88 } : port);
  }
  const basic = basicShape(shape);
  if (basic !== undefined) {
    // A basic shape lists the ports it has, on its own outline (basic-shapes.json): a brace offers only its tip.
    return basicPorts(basic);
  }
  const icon = iconForShape(shape);
  if (icon !== undefined && icon.boundary !== true) {
    // An icon node is a glyph over a label, not its box: its connection points sit on the drawing (icons.ts).
    return iconPortFractions();
  }
  if (shape === 'collate') {
    // Bowtie: only the full-width top and bottom edges carry a side midpoint; left/right are the crossing point.
    return DEFAULT_PORTS.filter((port) => port.id === 'top' || port.id === 'bottom');
  }
  if (shape === umlShapeName('lollipop') || shape === umlShapeName('socket')) {
    // A provided or required interface symbol fills the top band of its box (umlShapes.ts): the stem starts at the left
    // edge and the ball or arc ends at the right, both at the band's mid-height. The band below holds the label, so no
    // top or bottom port.
    const y = UML_METRICS.symbolBand / 2;
    return DEFAULT_PORTS.filter((port) => port.id === 'left' || port.id === 'right').map((port) => ({ ...port, y }));
  }
  return DEFAULT_PORTS;
}

function isDefaultPort(port: Port, shape: string): boolean {
  return defaultPorts(shape).some((candidate) => candidate.id === port.id && candidate.x === port.x && candidate.y === port.y);
}

/**
 * Checks an end against its node and returns the node map when the default ports must be declared on it, or
 * undefined when the node already declares the port. Throws before anything is written: a Yjs transaction is not
 * rolled back by a throw, so every check happens first.
 */
function needsDefaultPorts(nodes: Y.Map<ElementMap>, end: ConnectionEnd): ElementMap | undefined {
  const node = nodes.get(end.node);
  if (node === undefined) {
    throw new Error(`Cannot connect to node ${end.node}: it is not in the document.`);
  }
  if (isColumnPortId(end.port.id)) {
    // A column of an entity is a derived port (columnPorts.ts): nothing is declared on the node for it, but the row must exist.
    if (columnRowOf({ shape: node.get('shape') as string, props: node.get('props') as Props | undefined }, end.port.id) === undefined) {
      throw new Error(`Cannot connect to port ${end.port.id} of node ${end.node}: the node has no such column.`);
    }
    return undefined;
  }
  const declared = node.get('ports') as Port[] | undefined;
  if (declared !== undefined && declared.length > 0) {
    if (!declared.some((port) => port.id === end.port.id)) {
      throw new Error(`Cannot connect to port ${end.port.id} of node ${end.node}: the node does not declare it.`);
    }
    return undefined;
  }
  if (!isDefaultPort(end.port, node.get('shape') as string)) {
    throw new Error(`Cannot connect to port ${end.port.id} of node ${end.node}: the node declares no ports and it is not a default one.`);
  }
  return node;
}

/**
 * Adds an orthogonal `flow` edge from one port to another in one local transaction and returns its id. The id is
 * proposed by `newId` and asked again while it collides with any node or edge id already in the document.
 */
export function connect(doc: Y.Doc, connection: Connection, newId: IdGenerator = randomIdGenerator): Id {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edges = doc.getMap<ElementMap>(EDGES_MAP);
  let id: Id | undefined;
  for (let attempt = 0; attempt < MAX_ID_ATTEMPTS && id === undefined; attempt++) {
    const proposal = newId('edge');
    if (!nodes.has(proposal) && !edges.has(proposal)) {
      id = proposal;
    }
  }
  if (id === undefined) {
    throw new Error(`Could not generate a free edge id in ${String(MAX_ID_ATTEMPTS)} attempts.`);
  }
  const edgeId = id;
  const declare = [needsDefaultPorts(nodes, connection.from), needsDefaultPorts(nodes, connection.to)];

  doc.transact(() => {
    for (const node of declare) {
      if (node !== undefined) {
        node.set('ports', defaultPorts(node.get('shape') as string).map((port) => ({ ...port })));
      }
    }
    let order = -1;
    edges.forEach((edge) => {
      const value = edge.get(ORDER_FIELD);
      if (typeof value === 'number' && value > order) {
        order = value;
      }
    });
    const edge: Edge = {
      id: edgeId,
      kind: NEW_EDGE_KIND,
      from: { node: connection.from.node, port: connection.from.port.id },
      to: { node: connection.to.node, port: connection.to.port.id },
      routing: { mode: 'orthogonal' },
    };
    const map = new Y.Map<unknown>();
    edges.set(edgeId, map);
    map.set('id', edge.id);
    map.set('kind', edge.kind);
    map.set('from', edge.from);
    map.set('to', edge.to);
    map.set('routing', edge.routing);
    map.set(ORDER_FIELD, order + 1);
  }, LOCAL_ORIGIN);
  return edgeId;
}

/** Which end of an edge a re-attach moves: `from` is the tail, `to` the end the arrow points at by default. */
export type EdgeEndName = 'from' | 'to';

/** A finished re-attach gesture: one end of an existing edge dragged onto a port. */
export interface Reconnection {
  edge: Id;
  end: EdgeEndName;
  to: ConnectionEnd;
}

/**
 * Moves one end of an existing edge onto another port, of the same node or another one, in one local transaction.
 * Only that end's `from` or `to` changes: kind, label, style and routing stay as they were, and an orthogonal edge's
 * route is derived again from the new ports (decision D4 (a)), so nothing about it is written. A default port is
 * declared on a portless node exactly as `connect` does. Returns whether it wrote: re-attaching an end to the port it
 * is already on writes nothing. Throws before writing when the edge or node is gone, the port is not on the node, or
 * the move would join an edge to itself.
 */
export function reconnect(doc: Y.Doc, reconnection: Reconnection): boolean {
  const nodes = doc.getMap<ElementMap>(NODES_MAP);
  const edge = doc.getMap<ElementMap>(EDGES_MAP).get(reconnection.edge);
  if (edge === undefined) {
    throw new Error(`Cannot re-attach edge ${reconnection.edge}: it is not in the document.`);
  }
  const { end, to } = reconnection;
  const other = edge.get(end === 'from' ? 'to' : 'from') as Edge['from'] | undefined;
  if (other?.node === to.node) {
    throw new Error(`Cannot re-attach edge ${reconnection.edge} to node ${to.node}: its other end is already there.`);
  }
  const declare = needsDefaultPorts(nodes, to);
  const current = edge.get(end) as Edge['from'] | undefined;
  if (current?.node === to.node && current.port === to.port.id) {
    return false;
  }
  doc.transact(() => {
    declare?.set('ports', defaultPorts(declare.get('shape') as string).map((port) => ({ ...port })));
    edge.set(end, { node: to.node, port: to.port.id });
  }, LOCAL_ORIGIN);
  return true;
}
