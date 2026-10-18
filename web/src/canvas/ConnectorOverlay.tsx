/**
 * What connecting shows (the M2 editor checklist, item 8; design/atlas/editor-shell.md,
 * design/atlas/shape-connector-palette.md): in the connector tool the ports of the node under the pointer, with the
 * port a press or release would attach to filled in; in the select tool the connection handles of the hovered or
 * selected node (connectHandles.ts), set out from its ports; and while a connection is being drawn or an edge end
 * re-attached, a dashed line from where it starts to where it would land, the target node outlined and the port it
 * would snap to ringed. Drawn in screen space like the selection overlay, so the markers stay the same size at every
 * zoom, and with no pointer events, so it never takes a press; DiagramCanvas hit-tests the same positions itself.
 * View state only: nothing here reads or writes the model.
 */

import type { ReactNode } from 'react';
import { columnRowOf } from '../model/columnPorts.ts';
import type { Id, Point } from '../model/types.ts';
import type { DocumentIndex } from './documentIndex.ts';
import { connectHandlePosition } from './connectHandles.ts';
import { connectablePorts, endpointPort, portPosition } from './ports.ts';
import { fmt } from './shapes.tsx';
import { worldToScreen, type Viewport } from './viewport.ts';

/** A node the overlay marks the ports of, and which one of them (if any) is where the gesture would attach. */
export interface PortHighlight {
  node: Id;
  port?: Id | undefined;
  /** For connection handles: the node shows the rotate handle, so its handles step clear of it (connectHandles.ts). */
  rotateHandle?: boolean | undefined;
}

export interface ConnectorOverlayState {
  /** The source node while dragging, or the node under the pointer while hovering. */
  source?: PortHighlight | undefined;
  /** While dragging: the node under the pointer that a release would connect to. */
  target?: PortHighlight | undefined;
  /** While dragging: the pointer, in world units, where the preview line ends when there is no target port. */
  pointer?: Point | undefined;
  /** While re-attaching an edge end: the world point the preview starts from, the end that stays where it is. */
  origin?: Point | undefined;
  /** The select tool's connection handles: the nodes that show them, and the handle under the pointer, if any. */
  handles?: readonly PortHighlight[] | undefined;
}

/** Radius of a port marker in screen pixels. */
const PORT_MARKER_RADIUS = 5;
/** Radius of a select-tool connection handle in screen pixels. */
const HANDLE_RADIUS = 5;
/** Radius of the ring around the port a release would snap to, in screen pixels. */
const SNAP_RING_RADIUS = 10;

export interface ConnectorOverlayProps {
  state: ConnectorOverlayState;
  index: DocumentIndex;
  viewport: Viewport;
}

function portPoint(index: DocumentIndex, highlight: PortHighlight | undefined, towards?: Point): Point | undefined {
  if (highlight?.port === undefined) {
    return undefined;
  }
  const node = index.placed.get(highlight.node);
  if (node === undefined) {
    return undefined;
  }
  const port = connectablePorts(node).find((candidate) => candidate.id === highlight.port) ?? endpointPort(node, highlight.port, towards ?? { x: node.geometry.x + node.geometry.w, y: node.geometry.y });
  return port === undefined ? undefined : portPosition(node.geometry, port);
}

/** The band of an entity's column row a connector would attach to, in screen space and turned with the table; nothing for any other port. */
function columnBand(index: DocumentIndex, viewport: Viewport, highlight: PortHighlight | undefined): ReactNode {
  const node = highlight === undefined ? undefined : index.placed.get(highlight.node);
  const row = node === undefined ? undefined : columnRowOf(node, highlight?.port);
  if (node === undefined || row === undefined) {
    return null;
  }
  const g = node.geometry;
  const origin = worldToScreen(viewport, { x: g.x, y: g.y + row.top });
  const rotation = g.rotation ?? 0;
  const pivot = worldToScreen(viewport, { x: g.x + g.w / 2, y: g.y + g.h / 2 });
  const transform = rotation % 360 === 0 ? undefined : `rotate(${String(fmt(rotation))} ${String(fmt(pivot.x))} ${String(fmt(pivot.y))})`;
  return <rect className="connector-column-row" data-connector-node={node.id} data-port-id={highlight?.port} x={fmt(origin.x)} y={fmt(origin.y)} width={fmt(g.w * viewport.zoom)} height={fmt(row.height * viewport.zoom)} transform={transform} />;
}

function markers(index: DocumentIndex, viewport: Viewport, highlight: PortHighlight | undefined, role: string): ReactNode {
  const node = highlight === undefined ? undefined : index.placed.get(highlight.node);
  if (highlight === undefined || node === undefined) {
    return null;
  }
  return (
    <g className={`connector-ports connector-ports-${role}`} data-connector-node={node.id}>
      {columnBand(index, viewport, highlight)}
      {connectablePorts(node).map((port) => {
        const at = worldToScreen(viewport, portPosition(node.geometry, port));
        const active = port.id === highlight.port;
        return (
          <circle
            key={port.id}
            className={active ? 'connector-port is-active' : 'connector-port'}
            data-port-id={port.id}
            cx={fmt(at.x)}
            cy={fmt(at.y)}
            r={active ? PORT_MARKER_RADIUS + 1 : PORT_MARKER_RADIUS}
          />
        );
      })}
    </g>
  );
}

function handles(index: DocumentIndex, viewport: Viewport, highlight: PortHighlight): ReactNode {
  const node = index.placed.get(highlight.node);
  if (node === undefined) {
    return null;
  }
  return (
    <g key={node.id} className="connect-handles" data-connector-node={node.id}>
      {connectablePorts(node).map((port) => {
        const at = worldToScreen(viewport, connectHandlePosition(node, port, viewport.zoom, highlight.rotateHandle === true));
        const active = port.id === highlight.port;
        return <circle key={port.id} className={active ? 'connect-handle is-active' : 'connect-handle'} data-port-id={port.id} cx={fmt(at.x)} cy={fmt(at.y)} r={active ? HANDLE_RADIUS + 1 : HANDLE_RADIUS} />;
      })}
    </g>
  );
}

/** The outline of the node a release would connect to, turned with it, so the snap target is unmistakable. */
function targetOutline(index: DocumentIndex, viewport: Viewport, target: PortHighlight | undefined): ReactNode {
  const node = target === undefined ? undefined : index.placed.get(target.node);
  if (node === undefined) {
    return null;
  }
  const origin = worldToScreen(viewport, node.geometry);
  const w = node.geometry.w * viewport.zoom;
  const h = node.geometry.h * viewport.zoom;
  const rotation = node.geometry.rotation ?? 0;
  const transform = rotation % 360 === 0 ? undefined : `rotate(${String(fmt(rotation))} ${String(fmt(origin.x + w / 2))} ${String(fmt(origin.y + h / 2))})`;
  return <rect className="connector-target-outline" data-connector-node={node.id} x={fmt(origin.x)} y={fmt(origin.y)} width={fmt(w)} height={fmt(h)} transform={transform} />;
}

export function ConnectorOverlay({ state, index, viewport }: ConnectorOverlayProps) {
  const from = state.origin ?? portPoint(index, state.source, state.pointer);
  const snapped = portPoint(index, state.target, from);
  const to = snapped ?? state.pointer;
  const line = state.pointer !== undefined && from !== undefined && to !== undefined ? { a: worldToScreen(viewport, from), b: worldToScreen(viewport, to) } : undefined;
  const ring = snapped === undefined ? undefined : worldToScreen(viewport, snapped);
  const drawingTarget = state.target !== undefined && state.target.node !== state.source?.node;
  return (
    <g className="connector-overlay">
      {drawingTarget && targetOutline(index, viewport, state.target)}
      {line !== undefined && <line className="connector-preview" x1={fmt(line.a.x)} y1={fmt(line.a.y)} x2={fmt(line.b.x)} y2={fmt(line.b.y)} />}
      {state.handles?.map((highlight) => handles(index, viewport, highlight))}
      {markers(index, viewport, state.source, 'source')}
      {drawingTarget && markers(index, viewport, state.target, 'target')}
      {ring !== undefined && <circle className="connector-snap-ring" cx={fmt(ring.x)} cy={fmt(ring.y)} r={SNAP_RING_RADIUS} />}
    </g>
  );
}
