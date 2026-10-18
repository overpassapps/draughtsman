/**
 * The selection overlay: an outline, eight resize handles, a rotate handle and, for selected edges, a highlighted
 * stroke. Drawn in screen space (outside the world transform) so handles stay the same size at every zoom. The
 * outline has `pointer-events: none` so selection never intercepts a press on the node beneath; the handles alone
 * take the press, and DiagramCanvas turns a drag on one into a resize (checklist item 3) using the handle index in
 * `data-handle`, or into a rotation (checklist item 4) via `data-rotate-handle`.
 * A rotated node's outline and handles turn with it, so each handle sits on the corner or edge it resizes, and the
 * rotate handle stays above its (rotated) top edge.
 * The rotate handle only draws for a single selected node (multi-selection rotation is out of scope); while a
 * rotation is in progress, `rotating` names its id and quantised angle so the label beside the handle can show it.
 * Given `edgeEndpoints`, a selected edge also shows a handle near each end of its drawn route (`data-edge-end` names
 * which), at the positions connectHandles.ts `selectedEdgeEnds` gave; DiagramCanvas computes them once, draws them
 * through here and turns a drag on one into a re-attach, hit-testing the same points.
 * A selected connector also shows a drag handle at each draggable middle segment (orthogonal) or waypoint and the
 * curve's midpoint (curved) — segmentDrag.ts `segmentHandles` — carrying its own drag cursor, so the affordance that
 * a route can be pinned is visible; the handle is drawn by `data-segment-*` and DiagramCanvas turns a press on it into
 * the same segment drag a press on the route already starts.
 * Selection is view state: nothing here reads or writes the model.
 */

import type { ReactNode } from 'react';
import type { Id, Point } from '../model/types.ts';
import { ROTATE_HANDLE_OFFSET, ROTATE_HANDLE_RADIUS, type EdgeEndHandles } from './connectHandles.ts';
import type { DocumentIndex } from './documentIndex.ts';
import type { Rect } from './geometry.ts';
import { HANDLE_ANCHORS, HANDLE_CURSORS } from './resize.ts';
import { segmentHandles, type SegmentHandle } from './segmentDrag.ts';
import { fmt } from './shapes.tsx';
import { worldToScreen, type Viewport } from './viewport.ts';

export interface SelectionOverlayProps {
  selection: ReadonlySet<Id>;
  index: DocumentIndex;
  viewport: Viewport;
  /** The node id and quantised rotation (degrees) of a rotate drag in progress; shows the angle beside the handle. */
  rotating?: { id: Id; rotation: number } | undefined;
  /** The end handles of each selected edge (connectHandles.ts `selectedEdgeEnds`), for an editor that can re-attach them. */
  edgeEndpoints?: EdgeEndHandles | undefined;
}

/** Size of a selection handle in screen pixels, at every zoom. */
const HANDLE_SIZE = 7;
/** Side of a connector segment/waypoint drag handle in screen pixels. */
const SEGMENT_HANDLE_SIZE = 7;
/** Radius of a selected edge's endpoint handle in screen pixels. */
const ENDPOINT_RADIUS = 5;

interface ScreenRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

function toScreen(viewport: Viewport, bounds: Rect): ScreenRect {
  const origin = worldToScreen(viewport, { x: bounds.x, y: bounds.y });
  return { x: origin.x, y: origin.y, w: bounds.w * viewport.zoom, h: bounds.h * viewport.zoom };
}

/** The screen position of every handle on a node's unrotated box; the rotation is applied to the group around them. */
function handleCentres(box: ScreenRect): { x: number; y: number }[] {
  return HANDLE_ANCHORS.map((anchor) => ({ x: box.x + box.w * anchor.x, y: box.y + box.h * anchor.y }));
}

/** The cursor a segment handle shows: along the drag direction, so a horizontal middle segment shows a vertical drag. */
function segmentHandleCursor(handle: SegmentHandle, points: readonly Point[]): string {
  if (handle.kind === 'curved') {
    return 'move';
  }
  const a = points[handle.segmentIndex];
  const b = points[handle.segmentIndex + 1];
  if (a === undefined || b === undefined) {
    return 'move';
  }
  return Math.abs(b.x - a.x) >= Math.abs(b.y - a.y) ? 'ns-resize' : 'ew-resize';
}

/** A selected connector's segment/waypoint drag handles, in screen space, keyed by what each drags. */
function segmentHandlesFor(id: Id, points: readonly Point[], mode: string | undefined, viewport: Viewport): ReactNode[] {
  return segmentHandles(points, mode).map((handle) => {
    const at = worldToScreen(viewport, handle.at);
    const index = handle.kind === 'orthogonal' ? handle.segmentIndex : handle.waypointIndex;
    return (
      <rect
        key={`${handle.kind}-${String(index)}`}
        className="segment-handle"
        data-segment-handle="true"
        data-edge-id={id}
        data-segment-kind={handle.kind}
        data-segment-index={index}
        style={{ cursor: segmentHandleCursor(handle, points) }}
        x={fmt(at.x - SEGMENT_HANDLE_SIZE / 2)}
        y={fmt(at.y - SEGMENT_HANDLE_SIZE / 2)}
        width={SEGMENT_HANDLE_SIZE}
        height={SEGMENT_HANDLE_SIZE}
      />
    );
  });
}

export function SelectionOverlay({ selection, index, viewport, rotating, edgeEndpoints }: SelectionOverlayProps) {
  if (selection.size === 0) {
    return null;
  }
  // A sequence diagram derives every position from element order: nothing to resize, rotate or re-route (typeCapabilities.ts).
  const derived = index.sequence !== undefined;
  const showRotateHandle = selection.size === 1 && !derived;
  const nodes: ReactNode[] = [];
  const edges: ReactNode[] = [];
  for (const id of selection) {
    const node = index.placed.get(id);
    if (node) {
      const box = toScreen(viewport, node.geometry);
      const rotation = node.geometry.rotation ?? 0;
      // Uniform zoom keeps angles, so rotating the screen-space box about its centre matches the rotated node.
      const transform = rotation % 360 === 0 ? undefined : `rotate(${String(fmt(rotation))} ${String(fmt(box.x + box.w / 2))} ${String(fmt(box.y + box.h / 2))})`;
      const topMiddle = handleCentres(box)[1] ?? { x: box.x + box.w / 2, y: box.y };
      const rotateHandleCentre = { x: topMiddle.x, y: topMiddle.y - ROTATE_HANDLE_OFFSET };
      nodes.push(
        <g key={id} className="selection-node" data-selection-id={id} transform={transform}>
          <rect className="selection-outline" x={fmt(box.x)} y={fmt(box.y)} width={fmt(box.w)} height={fmt(box.h)} />
          {!derived && handleCentres(box).map((centre, i) => (
            <rect key={i} className="selection-handle" data-handle={i} style={{ cursor: HANDLE_CURSORS[i] }} x={fmt(centre.x - HANDLE_SIZE / 2)} y={fmt(centre.y - HANDLE_SIZE / 2)} width={HANDLE_SIZE} height={HANDLE_SIZE} />
          ))}
          {showRotateHandle && (
            <>
              <line className="selection-rotate-connector" x1={fmt(topMiddle.x)} y1={fmt(topMiddle.y)} x2={fmt(rotateHandleCentre.x)} y2={fmt(rotateHandleCentre.y)} />
              <circle
                className="selection-rotate-handle"
                data-rotate-handle="true"
                cx={fmt(rotateHandleCentre.x)}
                cy={fmt(rotateHandleCentre.y)}
                r={ROTATE_HANDLE_RADIUS}
                style={{ cursor: 'grab' }}
              />
              {rotating?.id === id && (
                // Counter-rotates by the same angle and about the same point the parent `<g>` turns by, so the
                // label's position follows the handle but its text stays upright at any rotation.
                <g transform={rotation % 360 === 0 ? undefined : `rotate(${String(fmt(-rotation))} ${String(fmt(rotateHandleCentre.x))} ${String(fmt(rotateHandleCentre.y))})`}>
                  <text className="selection-rotate-label" x={fmt(rotateHandleCentre.x)} y={fmt(rotateHandleCentre.y - 10)} textAnchor="middle">
                    {`${String(Math.round(rotating.rotation))}°`}
                  </text>
                </g>
              )}
            </>
          )}
        </g>,
      );
      continue;
    }
    const path = index.edgePaths.get(id);
    if (path) {
      const points = path.points.map((p) => worldToScreen(viewport, p));
      const ends = edgeEndpoints?.get(id);
      const first = ends?.from === undefined ? undefined : worldToScreen(viewport, ends.from);
      const last = ends?.to === undefined ? undefined : worldToScreen(viewport, ends.to);
      edges.push(
        <g key={id} className="selection-edge-ends">
          <polyline className="selection-edge" data-selection-id={id} points={points.map((p) => `${String(fmt(p.x))},${String(fmt(p.y))}`).join(' ')} />
          {!derived && segmentHandlesFor(id, path.points, index.edgesById.get(id)?.routing?.mode, viewport)}
          {first !== undefined && <circle className="edge-endpoint" data-edge-id={id} data-edge-end="from" cx={fmt(first.x)} cy={fmt(first.y)} r={ENDPOINT_RADIUS} />}
          {last !== undefined && <circle className="edge-endpoint" data-edge-id={id} data-edge-end="to" cx={fmt(last.x)} cy={fmt(last.y)} r={ENDPOINT_RADIUS} />}
        </g>,
      );
    }
  }
  return (
    <g className="selection">
      {nodes}
      {edges}
    </g>
  );
}