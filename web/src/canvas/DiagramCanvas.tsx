/**
 * The interactive canvas: owns the viewport, measures its box and wires pan and zoom onto DiagramSvg.
 *
 * Pan: plain wheel and two-finger trackpad scroll; space+drag anywhere; middle mouse button; one finger on touch; and,
 * in the connector tool or a read-only view, a mouse drag on empty canvas.
 * Select and move (design/atlas/editor-shell.md, checklist item 1): a mouse press on a node or edge selects it, Shift
 * adds or removes, a press on empty canvas clears. Dragging a selected node moves the whole selection; each animation
 * frame hands the latest absolute geometry to onMoveNodes, and onMoveEnd marks the end of the gesture so the caller
 * can close its undo step. Escape during a move, resize or rotate cancels it: the frames it already wrote are taken back
 * through onMoveCancel (which leaves no undo or redo entry, unlike onMoveEnd), a frame not yet written is dropped, the
 * insertion line and snap guides go, pointer capture is released, and the release that follows does nothing. In a
 * derived-layout diagram (a sequence) a drag writes nothing until release, so Escape there only abandons the reorder.
 * Marquee (checklist item 2): in the select tool a mouse drag on empty canvas draws a marquee (marquee.ts) and its
 * release selects every node it intersects, Alt only those it fully encloses; Shift adds them to the selection the
 * press started from. Escape abandons it with the selection as the press left it.
 * Resize (checklist item 3): dragging one of a selected node's eight handles resizes that node from the opposite anchor
 * (resize.ts), Shift keeping the aspect ratio, through the same onMoveNodes frames and one onMoveEnd on release.
 * Rotate (checklist item 4): with exactly one node selected, dragging its rotate handle turns it about its own centre
 * in 1° steps (15° with Shift, rotate.ts), through the same onMoveNodes frames and one onMoveEnd on release; the
 * quantised angle is shown beside the handle (`rotating` state) until then. Resize handles, connected edges and the
 * selection outline already turn with a node's rotation (SelectionOverlay.tsx, ports.ts), so nothing else here
 * changes for a rotated node.
 * Snapping (checklist items 5 to 7): every drag and resize frame goes through dragSnap.ts before it is written, so the
 * node lands on the grid (while `grid` is on), a neighbour's edge or centre, or an equal spacing, and the guides it
 * snapped to are drawn until release. Snapping changes only what a frame writes, never how many frames or steps.
 * Containers (items 26 and 27): a drag or nudge of a container moves its contents and the waypoints of every edge with
 * both ends moving (containerDrag.ts), through the same onMoveNodes frames. While a node is dragged, the container it
 * would drop into is outlined; on release each dragged root whose container changed goes to onReparentNodes, before
 * onMoveEnd, so the move and the reparent are one gesture. Absolute geometry is never adjusted by a reparent.
 * Group (item 22): Cmd/Ctrl+G hands the selection to onGroup to group, Cmd/Ctrl+Shift+G to ungroup; the selection
 * becomes whatever it returns (the new container, or the released children).
 * Keyboard (items 20 and 21), while the canvas has focus and no text field inside it does: an arrow key nudges the
 * selection 1px (Shift: 10px) as one onMoveNodes plus onMoveEnd per keypress, so each press is its own undo step;
 * Delete or Backspace hands the selection to onDeleteElements and clears it; Cmd/Ctrl+] and Cmd/Ctrl+[ (Alt: to the
 * front, to the back) hand it to onArrange (item 25), and are held back from the browser's Back and Forward even with
 * nothing selected, so the key cannot leave an editor with unsaved work. Clipboard (items 18, 19 and 21): Cmd/Ctrl+D
 * hands the selection to onClipboard, which owns the clipboard and any new selection, and is held back from the
 * browser's bookmark dialog even with nothing selected. Cmd/Ctrl+C, X and V are left to
 * the browser, whose native copy, cut and paste events carry a DataTransfer that works without a permission prompt or
 * a secure context; the event's DataTransfer goes to onClipboard, and a copy or cut event is prevented only when
 * onClipboard put text on it. Those events are heard on the whole document and taken only while the canvas has focus,
 * since a caret left in page text makes the browser fire them there. Only if no event follows the key does onClipboard
 * get the command with no DataTransfer, to use the async Clipboard API for itself. Tab and
 * Shift+Tab select the next or previous node in document order and announce its label and kind; past the last node,
 * or before the first, they move focus out of the canvas as usual.
 * Label editing (checklist items 11, 13 and 14): a double-click on a node or edge, or Enter with exactly one of them
 * selected, opens an editor: LabelEditor over a node's label, or EdgeLabelEditor at the midpoint of the route an edge
 * is drawn along now, which it follows if the edge re-routes while it is open. The committed text goes to
 * onLabelChange, and Escape closes either with nothing sent.
 * Connect (checklist item 8): with `tool` set to `connector`, the node under the pointer shows its ports; a mouse, pen
 * or touch press on a node starts a connection at the port under the pointer (or the nearest one when the press is on
 * the body), and releasing on another node's port, or on its body for the nearest port, hands the pair to onConnect. A
 * release on empty canvas or back on the source node, or Escape, creates nothing. A press on empty canvas still pans.
 * A pen or touch press on a node starts a connection only while it is the only pointer down; a second touch arriving
 * mid-gesture cancels it (nothing is created) and both pointers join a two-finger pinch instead, so a connect gesture
 * never fights a pan or zoom and a pinch still works exactly as before.
 * In the select tool, with onConnect given, the hovered node and a lone selected node show connection handles set out
 * from their ports (connectHandles.ts); a mouse, pen or touch drag from one is the same connection gesture without
 * changing tool. Touch has no hover, so a node's handles show only while it is the lone selection or a connect gesture
 * from it is already in progress. While a connection is drawn the node a release would land on is outlined and the
 * port it would snap to ringed.
 * Re-attach: in the select tool with onReconnect given, a selected edge shows a handle near each end of its drawn route,
 * set in along it from the port (connectHandles.ts); dragging one with a mouse, pen or touch onto a node (its port, or
 * its body for the nearest port) hands onReconnect that end's new port. A release anywhere else, on the node the other
 * end is on, or back on the same port, or Escape, changes nothing. A press on a port in the connector tool, or on a
 * connection handle in the select tool, always starts a new connector, even where a selected edge ends, so fanning out
 * never re-attaches. A selected connector's segment or waypoint handles, and a press directly on its route, pin its
 * route (segmentDrag.ts) the same way for a mouse, pen or touch press. A pen or touch connector, segment or re-attach
 * hit target is at least 24 CSS pixels across (ports.ts TOUCH_HIT_RADIUS); a mouse keeps its smaller, precise target.
 * Orthogonal edges are drawn along routes the routing worker computes from the current node bounds (useEdgeRoutes.ts),
 * so a move or resize re-routes them without anything being written to the model.
 * Zoom: Ctrl/Cmd+wheel and trackpad pinch (which browsers deliver as ctrl+wheel) around the cursor; two-finger
 * pinch on touch; and the keys from design/atlas/editor-shell.md: Cmd/Ctrl+= in, Cmd/Ctrl+- out, Cmd/Ctrl+0 fit,
 * Cmd/Ctrl+1 100%. This matches Lucidchart, Miro, Figma, Whimsical and FigJam.
 * Actions (checklist item 23): every keyboard command above except space and Escape is an action in the editor's
 * registry (editor/actions.ts). The canvas's keydown runs them through dispatchShortcut, and the handle's `actions`
 * hands the same records to the command palette, so a palette row does exactly what its key does. Cmd/Ctrl+C, X and V
 * are the one exception: they stay on the browser's native clipboard events above, so their registry entries
 * (DocumentViewer.tsx) carry the shortcut's label for the palette to show but a shortcut that never matches a key
 * event, and running one from the palette calls onClipboard with no DataTransfer, the same fallback this canvas uses
 * when a keydown gets no native event.
 * Derived geometry (design/atlas/sequence-editor.md): for a document whose type derives its positions from element order
 * (`typeCapabilities(type).derivedGeometry`, a sequence diagram) there is nothing to write, so none of the geometry
 * gestures above exist: no resize or rotate handle, no free move or snapping (a press-drag on a participant or a
 * message reorders it instead, onReorder, showing an insertion line and writing once on release), no connection
 * handles from ports, no segment or waypoint drag, no re-attach, no nudge. The connector tool still connects, but to a
 * participant's header or lifeline and with no port: the release y picks the slot and onInsertMessage gets the pair
 * and the index in the edge order. Tab also walks the messages after the participants, since a message is not
 * reachable from the keyboard otherwise.
 * Viewport updates coalesce into one state commit per animation frame so a fast wheel never queues renders.
 */

import { forwardRef, useCallback, useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { dispatchShortcut, type EditorAction } from '../editor/actions.ts';
import {
  arrangeShortcut,
  groupShortcut,
  clipboardCommand,
  isDeleteCommand,
  isEditLabelKey,
  isTextField,
  NUDGE_STEP,
  NUDGE_STEP_LARGE,
  nudgeDelta,
  selectionStep,
  zoomShortcut,
  type ClipboardCommand,
  type GroupCommand,
} from '../editor/shortcuts.ts';
import type { ArrangeCommand } from '../model/arrange.ts';
import type { Connection, EdgeEndName, Reconnection } from '../model/connect.ts';
import { translateGeometries } from '../model/editor.ts';
import { insertionIndex, messageLabelAnchor, messageSlot, participantSlot, slotLineX, slotLineY } from '../model/sequenceEdit.ts';
import type { CompareMarks } from '../model/compare.ts';
import type { Document, Endpoint, Geometry, Id, Point, Port, ViewRect } from '../model/types.ts';
import { typeCapabilities } from '../model/typeCapabilities.ts';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { applyDocumentOverrides, effectiveTheme } from '../theme/resolveStyle.ts';
import { CONNECT_HIT_RADIUS, connectHandleAt, endHandleAt, handleHoverNode, handleNodes, rotateHandleNode, selectedEdgeEnds } from './connectHandles.ts';
import type { ConnectorOverlayState, PortHighlight } from './ConnectorOverlay.tsx';
import { dropParents, dropPlan, dropTargets, translateWaypoints, waypointStarts, type DropPlan } from './containerDrag.ts';
import { cameraForView, viewportForCamera, viewRectOf } from './camera.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument, isContainer, type DocumentIndex } from './documentIndex.ts';
import { participantAt } from './sequenceHit.ts';
import { estimateTextWidth, labelFont } from './textEstimate.ts';
import { SEQUENCE_METRICS, type SequenceFragment } from '../model/sequenceScene.ts';
import { snapMove, snapResize, snapTargets, type SnapContext } from './dragSnap.ts';
import { EdgeLabelEditor } from './EdgeLabelEditor.tsx';
import type { Rect } from './geometry.ts';
import { compartmentAt, edgeLabelAt, membersOfText, type LabelEditEnd } from './labelEditing.ts';
import { memberPartsOf, type MemberPart } from '../model/memberParts.ts';
import { LabelEditHint } from './LabelEditHint.tsx';
import { LabelEditor } from './LabelEditor.tsx';
import { MembersEditor } from './MembersEditor.tsx';
import { marqueeRect, marqueeSelection, nodesInMarquee } from './marquee.ts';
import { edgeLabelStyle, resolveRenderedNodeStyle } from './renderedStyle.ts';
import { columnPortAt } from '../model/columnPorts.ts';
import { attachmentHit, nodeAt, pairPorts, PORT_HIT_RADIUS, portAt, portFacing, TOUCH_HIT_RADIUS } from './ports.ts';
import { HANDLE_ANCHORS, lockedHandle, resizeGeometry, resizeLockForShape, type ResizeLock } from './resize.ts';
import { angleFromCentre, normalizeRotation, rotateGeometry } from './rotate.ts';
import { dragStarts, selectOnPress, selectOnRelease, stepSelection } from './selection.ts';
import { curveWaypointAt, middleSegmentAt, moveCurvedWaypoint, moveOrthSegment } from './segmentDrag.ts';
import type { Guide } from './snapping.ts';
import type { LineBreaker, TextMeasurer } from './text.ts';
import { defaultRoutingBackend, useEdgeRoutes, type RoutingBackend } from './useEdgeRoutes.ts';
import {
  centreOn,
  fitToContent,
  IDENTITY_VIEWPORT,
  keepInPlace,
  pan,
  screenToWorld,
  type Size,
  type Viewport,
  ZOOM_STEP,
  zoomAround,
  zoomTo100,
} from './viewport.ts';

/** Why the viewport changed: a discrete command (button, shortcut, fit) or a continuous gesture (wheel, drag, pinch). */
export type ViewportChangeCause = 'command' | 'gesture';

/** The active rail tool, as far as the canvas is concerned (design/atlas/editor-shell.md). */
export type CanvasTool = 'select' | 'connector';

export interface DiagramCanvasProps {
  document: Document;
  theme: DiagramTheme;
  measure?: TextMeasurer | undefined;
  /** Breaks node labels into lines as the label editor does; undefined wraps them against `measure` (text.ts). */
  breakLines?: LineBreaker | undefined;
  /** Fixed size instead of measuring the element; for tests and export. */
  size?: Size | undefined;
  onViewportChange?: ((viewport: Viewport, cause: ViewportChangeCause) => void) | undefined;
  /** Reports the selection after every change; selection is view state and never written to the model. */
  onSelectionChange?: ((selection: ReadonlySet<Id>) => void) | undefined;
  /**
   * Writes the moved nodes' new absolute geometry back to the model, at most once per animation frame of a drag, with
   * the new waypoints of edges whose two ends both move (empty or absent when there are none).
   */
  onMoveNodes?: ((moves: ReadonlyMap<Id, Geometry>, waypoints?: ReadonlyMap<Id, Point[]>) => void) | undefined;
  /**
   * Sets (or clears, null) the parent of each dragged node whose container changed on release; called after the drag's
   * last onMoveNodes and before its onMoveEnd. Without it a drag never reparents and no drop target is shown.
   */
  onReparentNodes?: ((parents: ReadonlyMap<Id, Id | null>) => void) | undefined;
  /** Groups or ungroups the selection (Cmd/Ctrl+G, Cmd/Ctrl+Shift+G) and returns the selection to show afterwards. */
  onGroup?: ((ids: ReadonlySet<Id>, command: GroupCommand) => ReadonlySet<Id> | undefined) | undefined;
  /** Called once when a drag or resize that changed something ends, after its last onMoveNodes, and after each nudge. */
  onMoveEnd?: (() => void) | undefined;
  /**
   * Called instead of onMoveEnd when Escape cancels a move, resize or rotate that had already written frames: the
   * caller rolls those writes back so the gesture leaves nothing behind, no undo entry included. Not called for a
   * gesture that had written nothing (a press that never travelled, a reorder).
   */
  onMoveCancel?: (() => void) | undefined;
  /** Deletes the selected elements (Delete or Backspace); the canvas clears its selection straight after. */
  onDeleteElements?: ((ids: ReadonlySet<Id>) => void) | undefined;
  /**
   * The grid toggle (design/atlas/editor-shell.md, zoom cluster): when on, the grid is drawn and drags and resizes snap
   * to its spacing (the diagram theme's `grid.size`); left undefined, the theme's `grid.visible` decides.
   */
  grid?: boolean | undefined;
  /** The active tool; `select` by default. */
  tool?: CanvasTool | undefined;
  /**
   * Called once when a connector gesture ends on another node's port; the caller writes the edge. Given, the select
   * tool also offers connection handles on the hovered or selected node.
   */
  onConnect?: ((connection: Connection) => void) | undefined;
  /** Called once when a selected edge's end is dragged onto a different port; without it edge ends are not draggable. */
  onReconnect?: ((reconnection: Reconnection) => void) | undefined;
  /**
   * Derived-geometry documents only (a sequence diagram): a connector gesture ended between two participants (the same
   * one twice for a self message) at `index` of the edge order, which the release y picked from the derived scene.
   */
  onInsertMessage?: ((insert: { from: Id; to: Id; index: number }) => void) | undefined;
  /**
   * Derived-geometry documents only: a participant or a message was dragged and released at this world point; the caller
   * turns the point into a new place in the order (model/sequenceEdit.ts) and writes it as one undo step.
   */
  onReorder?: ((id: Id, at: Point) => void) | undefined;
  /** Creates the routing worker the canvas owns; the real worker by default. Tests pass an in-process one. */
  routingBackend?: (() => RoutingBackend | undefined) | undefined;
  /** Changes the render order of the selection (Cmd/Ctrl+] and Cmd/Ctrl+[, Alt for front and back). */
  onArrange?: ((ids: ReadonlySet<Id>, command: ArrangeCommand) => void) | undefined;
  /**
   * Copy, cut, paste or duplicate from the keyboard, with the selection at the time. Copy, cut and duplicate are only
   * sent with something selected. Copy, cut and paste carry the DataTransfer of the browser's clipboard event (a copy or
   * cut writes its text there, a paste reads it), or none when the browser fired no event and the caller must use the
   * system clipboard itself. The canvas leaves the selection alone: the caller replaces it through the handle.
   */
  onClipboard?: ((command: ClipboardCommand, selection: ReadonlySet<Id>, transfer?: DataTransfer) => void) | undefined;
  /**
   * Receives the id of the node or edge and the text of a committed label edit; without it labels are not editable.
   * Called on every commit, changed or not, so the caller decides whether anything is written. A discarded edit calls
   * nothing.
   */
  onLabelChange?: ((id: Id, label: string) => void) | undefined;
  /**
   * Receives the committed member list of a UML class's attributes or operations compartment (one string per member,
   * blank lines dropped); the caller decides whether anything changed and refits the box. Without it a double-click
   * on a compartment edits the class name instead.
   */
  onMembersChange?: ((id: Id, part: MemberPart, members: string[]) => void) | undefined;
  /**
   * Sequence diagrams: receives a committed guard edit of a combined fragment (design/atlas/sequence-fragments.md), the
   * fragment, the operand and the text. Called on every commit, changed or not; without it a fragment's guards are not
   * editable.
   */
  onGuardChange?: ((fragment: Id, operand: number, text: string) => void) | undefined;
  /**
   * The canvas fits to content once per value of this key. It defaults to the document, which suits a read-only view;
   * an editor passes something that stays the same across edits, or every drag frame would re-fit the view.
   */
  fitKey?: unknown;
  readOnly?: boolean;
  /** Marks a version comparison draws over the document (History panel); view state, undefined when not comparing. */
  compareMarks?: CompareMarks | undefined;
  className?: string | undefined;
}

export interface DiagramCanvasHandle {
  fit: () => void;
  zoomTo100: () => void;
  zoomIn: () => void;
  zoomOut: () => void;
  getViewport: () => Viewport;
  /**
   * Focus the canvas as a pointer press does: shortcuts and space-to-pan work, without the keyboard focus ring. With
   * `visible`, as the keyboard does, ring included, for focus handed back from a keyboard-driven dialog.
   */
  focus: (options?: { visible?: boolean }) => void;
  /** Replaces the selection, as after creating a node; reported through onSelectionChange like any other change. */
  select: (ids: ReadonlySet<Id>) => void;
  /**
   * Selects a node and pans the view so it is centred, keeping the current zoom: used when a cited node id is
   * clicked in the AI panel's generated document. A no-op for an id no node has.
   */
  reveal: (id: Id) => void;
  /** The world point at the centre of the visible canvas, where a create action places its node. */
  viewCentre: () => Point;
  /** The world rectangle the canvas shows now, for capturing a flythrough keyframe; undefined before the canvas has a size. */
  viewRect: () => ViewRect | undefined;
  /** Moves the camera to frame a keyframe's whole rectangle, as a command (no animation): checking a keyframe without presenting. */
  showView: (view: ViewRect) => void;
  /** The canvas's keyboard commands as registry actions (editor/actions.ts), for the command palette. */
  actions: readonly EditorAction[];
  /** The world point under a client (viewport) coordinate, for a drop from outside the canvas's own pointer handling. */
  pointToWorld: (client: Point) => Point;
  /**
   * Opens the label editor on an element the caller has only just added: unlike a double-click it does not need the
   * element to be drawn yet, so it can follow the write in the same event handler. It closes by itself if the element
   * never appears.
   */
  edit: (id: Id) => void;
  /** Opens the guard editor of a fragment's operand, for a frame the caller has only just added. */
  editGuard: (fragment: Id, operand: number) => void;
}

const ARRANGE_ACTIONS: readonly { command: ArrangeCommand; label: string; shortcut: string }[] = [
  { command: 'bringToFront', label: 'Bring to front', shortcut: 'Cmd/Ctrl+Alt+]' },
  { command: 'bringForward', label: 'Bring forward', shortcut: 'Cmd/Ctrl+]' },
  { command: 'sendBackward', label: 'Send backward', shortcut: 'Cmd/Ctrl+[' },
  { command: 'sendToBack', label: 'Send to back', shortcut: 'Cmd/Ctrl+Alt+[' },
];

/** The same four commands for a document whose order is its meaning: later is further right (participants) or down (messages). */
const SEQUENCE_ARRANGE_LABELS: Readonly<Record<ArrangeCommand, string>> = {
  bringToFront: 'Move to end',
  bringForward: 'Move later',
  sendBackward: 'Move earlier',
  sendToBack: 'Move to start',
};

/** The placeholder port a derived-geometry connector gesture carries: a participant has no ports, only a lifeline. */
const NO_PORT = { id: '', x: 0.5, y: 0.5 } as const;

/** The least width of the guard editor's field, so an empty guard still has somewhere to type. */
const GUARD_EDITOR_MIN_WIDTH = 80;

/** The operand of a frame whose guard row a world point is on or below (the last one above it); the first with no point. */
function guardAt(frame: SequenceFragment, at: Point | undefined): number {
  if (at === undefined) {
    return frame.sections[0]?.operand ?? 0;
  }
  const above = frame.sections.filter((section) => section.y <= at.y);
  return (above[above.length - 1] ?? frame.sections[0])?.operand ?? 0;
}

const GROUP_ACTIONS: readonly { command: GroupCommand; label: string; shortcut: string }[] = [
  { command: 'group', label: 'Group', shortcut: 'Cmd/Ctrl+G' },
  { command: 'ungroup', label: 'Ungroup', shortcut: 'Cmd/Ctrl+Shift+G' },
];

const NUDGE_ACTIONS: readonly { name: string; key: string; glyph: string; direction: Point }[] = [
  { name: 'left', key: 'ArrowLeft', glyph: '←', direction: { x: -1, y: 0 } },
  { name: 'right', key: 'ArrowRight', glyph: '→', direction: { x: 1, y: 0 } },
  { name: 'up', key: 'ArrowUp', glyph: '↑', direction: { x: 0, y: -1 } },
  { name: 'down', key: 'ArrowDown', glyph: '↓', direction: { x: 0, y: 1 } },
];

const FIT_PADDING = 40;
/** Zoom factor per wheel delta pixel with Ctrl/Cmd held or on a trackpad pinch. */
const WHEEL_ZOOM_RATE = 0.01;
/** Largest wheel delta one event may zoom by, so a mouse notch is ~35% rather than a 2.7x jump. */
const WHEEL_ZOOM_MAX_DELTA = 30;
/** Screen pixels a press must travel before it counts as a drag rather than a click. */
const DRAG_THRESHOLD = 4;

const NO_SELECTION: ReadonlySet<Id> = new Set<Id>();
const NO_GUIDES: readonly Guide[] = [];

/** What a connector press or release landed on: the node and port, and whether the port was chosen (ports.ts `PortHit`). */
interface EndHit {
  node: Id;
  port: Port;
  pinned: boolean;
}

interface Pointer {
  x: number;
  y: number;
}

// The one offset a snapped move applied to every node in `origins`; the raw one when nothing moved.
function snappedDelta(origins: ReadonlyMap<Id, Geometry>, moves: ReadonlyMap<Id, Geometry>, dx: number, dy: number): Point {
  for (const [id, start] of origins) {
    const moved = moves.get(id);
    if (moved) {
      return { x: moved.x - start.x, y: moved.y - start.y };
    }
  }
  return { x: dx, y: dy };
}

/** One frame of a drag or resize: absolute node geometry, and the waypoints of edges that move with the nodes. */
interface DragWrite {
  nodes: ReadonlyMap<Id, Geometry>;
  waypoints?: ReadonlyMap<Id, Point[]>;
}

function sameIds(a: ReadonlySet<Id>, b: ReadonlySet<Id>): boolean {
  return a.size === b.size && [...a].every((id) => b.has(id));
}

export const DiagramCanvas = forwardRef<DiagramCanvasHandle, DiagramCanvasProps>(function DiagramCanvas(
  {
    document: doc,
    theme,
    measure,
    breakLines,
    size: fixedSize,
    onViewportChange,
    onSelectionChange,
    onMoveNodes,
    onMoveEnd,
    onMoveCancel,
    onDeleteElements,
    onArrange,
    onClipboard,
    onLabelChange,
    onMembersChange,
    onGuardChange,
    grid,
    onReparentNodes,
    onGroup,
    tool = 'select',
    onConnect,
    onReconnect,
    onInsertMessage,
    onReorder,
    routingBackend = defaultRoutingBackend,
    fitKey,
    readOnly = false,
    compareMarks,
    className,
  },
  ref,
) {
  const element = useRef<HTMLDivElement>(null);
  const [measuredSize, setMeasuredSize] = useState<Size>({ width: 0, height: 0 });
  const size = fixedSize ?? measuredSize;
  const [viewport, setViewportState] = useState<Viewport>(IDENTITY_VIEWPORT);
  const [spaceHeld, setSpaceHeld] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [selection, setSelection] = useState<ReadonlySet<Id>>(NO_SELECTION);

  const [draggedNodes, setDraggedNodes] = useState<ReadonlySet<Id>>(NO_SELECTION);
  // The containers the dragged nodes would drop into if released now: view state, outlined while the drag lasts.
  const [dropHighlight, setDropHighlight] = useState<ReadonlySet<Id>>(NO_SELECTION);
  // The node or edge whose label is being edited: view state like the selection, never in the model until a commit.
  const [editing, setEditing] = useState<Id | undefined>(undefined);
  // Which part of a UML class the editor covers: undefined is the label (the class name), else a member compartment.
  const [editingPart, setEditingPart] = useState<MemberPart | undefined>(undefined);
  // A sequence fragment's guard being edited in place: the frame and which of its operands.
  const [editingGuard, setEditingGuard] = useState<{ fragment: Id; operand: number } | undefined>(undefined);
  // What Tab last selected, for screen readers (design/atlas/editor-shell.md, Keyboard and accessibility).
  const [announced, setAnnounced] = useState('');

  const index = useMemo(() => indexDocument(doc), [doc]);
  // Which member compartments (a class's attributes and operations, an entity's columns) any shape in the document has. The
  // Edit attributes / operations / columns commands exist only for those, so a flowchart's menu and palette do not list them.
  const memberPartsKey = useMemo(() => {
    const parts = new Set<MemberPart>();
    for (const placed of index.placed.values()) {
      for (const part of memberPartsOf(placed.shape)) {
        parts.add(part);
      }
    }
    return [...parts].sort().join(',');
  }, [index]);
  const contentBounds = index.contentBounds;
  const routes = useEdgeRoutes(index, routingBackend);
  const [connector, setConnector] = useState<ConnectorOverlayState | undefined>(undefined);
  const connecting = tool === 'connector' && !readOnly;
  // A document whose positions are derived from element order has no geometry to gesture on (typeCapabilities.ts).
  const derived = typeCapabilities(doc.type).derivedGeometry;
  const derivedRef = useRef(derived);
  derivedRef.current = derived;
  // The select tool's connection handles: the node the pointer is among, and the handle it is on. View state only.
  const [handleHover, setHandleHover] = useState<{ node: Id | undefined; active: PortHighlight | undefined } | undefined>(undefined);
  const selectHandles = tool === 'select' && !readOnly && !derived && onConnect !== undefined;
  const reattachable = tool === 'select' && !readOnly && !derived && onReconnect !== undefined;
  const [guides, setGuides] = useState<readonly Guide[]>(NO_GUIDES);
  const [marquee, setMarquee] = useState<Rect | undefined>(undefined);
  // The node id and quantised angle of a rotate drag in progress, shown beside the handle; view state, not the model.
  const [rotating, setRotating] = useState<{ id: Id; rotation: number } | undefined>(undefined);
  // The grid a gesture snaps to: the document's effective theme spacing, on while the toggle (or the theme) says so.
  const themeGrid = useMemo(() => applyDocumentOverrides(theme, doc.theme).grid, [theme, doc.theme]);
  const gridOn = grid ?? themeGrid.visible;
  const gridRef = useRef({ size: themeGrid.size, enabled: gridOn });
  gridRef.current = { size: themeGrid.size, enabled: gridOn };

  // Latest viewport plus a single pending frame, so wheel and pointer streams commit once per frame.
  const latest = useRef<Viewport>(IDENTITY_VIEWPORT);
  const frame = useRef<number | undefined>(undefined);
  const sizeRef = useRef(size);
  sizeRef.current = size;

  // The callback lives in a ref so commit, and with it every listener bound below, stays stable when a caller
  // passes a fresh handler on each render; rebinding mid-gesture would drop the live pointer state.
  const onChange = useRef(onViewportChange);
  onChange.current = onViewportChange;

  // The latest selection and the callbacks a long-lived listener reads, so binding remains stable across renders.
  const selectionRef = useRef<ReadonlySet<Id>>(NO_SELECTION);
  selectionRef.current = selection;
  const indexRef = useRef(index);
  indexRef.current = index;
  const onSelectionChangeRef = useRef(onSelectionChange);
  onSelectionChangeRef.current = onSelectionChange;
  const onMoveNodesRef = useRef(onMoveNodes);
  onMoveNodesRef.current = onMoveNodes;
  const onMoveEndRef = useRef(onMoveEnd);
  onMoveEndRef.current = onMoveEnd;
  const onMoveCancelRef = useRef(onMoveCancel);
  onMoveCancelRef.current = onMoveCancel;
  const onDeleteElementsRef = useRef(onDeleteElements);
  onDeleteElementsRef.current = onDeleteElements;
  const onArrangeRef = useRef(onArrange);
  onArrangeRef.current = onArrange;
  const onClipboardRef = useRef(onClipboard);
  onClipboardRef.current = onClipboard;
  const onLabelChangeRef = useRef(onLabelChange);
  onLabelChangeRef.current = onLabelChange;
  const onMembersChangeRef = useRef(onMembersChange);
  onMembersChangeRef.current = onMembersChange;
  const onGuardChangeRef = useRef(onGuardChange);
  onGuardChangeRef.current = onGuardChange;
  const onReparentNodesRef = useRef(onReparentNodes);
  onReparentNodesRef.current = onReparentNodes;
  const onGroupRef = useRef(onGroup);
  onGroupRef.current = onGroup;
  const onInsertMessageRef = useRef(onInsertMessage);
  onInsertMessageRef.current = onInsertMessage;
  const onReorderRef = useRef(onReorder);
  onReorderRef.current = onReorder;
  const editable = !readOnly && onLabelChange !== undefined;
  const editableRef = useRef(editable);
  editableRef.current = editable;
  const connectingRef = useRef(connecting);
  connectingRef.current = connecting;
  const onConnectRef = useRef(onConnect);
  onConnectRef.current = onConnect;
  const selectHandlesRef = useRef(selectHandles);
  selectHandlesRef.current = selectHandles;
  const onReconnectRef = useRef(onReconnect);
  onReconnectRef.current = onReconnect;
  const reattachableRef = useRef(reattachable);
  reattachableRef.current = reattachable;
  // A selected edge's end handles along the route it is drawn along now, router output included: computed once per
  // selection, route or zoom change and shared by the overlay and the press, so a pan recomputes nothing.
  const edgeEnds = useMemo(
    () =>
      reattachable && selection.size > 0
        ? selectedEdgeEnds(index, selection, viewport.zoom, (id) => {
            const routed = routes.get(id)?.points;
            return routed !== undefined && routed.length > 0 ? routed : index.edgePaths.get(id)?.points;
          })
        : undefined,
    [reattachable, index, routes, selection, viewport.zoom],
  );
  const edgeEndsRef = useRef(edgeEnds);
  edgeEndsRef.current = edgeEnds;
  const routesRef = useRef(routes);
  routesRef.current = routes;

  // Changing tool drops whatever the last one was showing; a gesture still in flight then ends creating nothing.
  useEffect(() => {
    setConnector(undefined);
    setHandleHover(undefined);
  }, [connecting, selectHandles]);

  // Drag and resize writes coalesce to one per animation frame; the pending write computes the frame's geometry.
  const pendingDrag = useRef<(() => DragWrite) | undefined>(undefined);
  const dragFrame = useRef<number | undefined>(undefined);
  const dragOrigins = useRef<ReadonlyMap<Id, Geometry>>(new Map());
  const waypointOrigins = useRef<ReadonlyMap<Id, Point[]>>(new Map());
  // Built once per drag: which containers the dragged nodes could drop into (containerDrag.ts).
  const drop = useRef<DropPlan | undefined>(undefined);

  // A frame that coalesces a command with gesture updates still reports the command, so the command is never lost.
  const pendingCause = useRef<ViewportChangeCause>('gesture');
  const commit = useCallback(
    (next: Viewport, cause: ViewportChangeCause) => {
      latest.current = next;
      if (cause === 'command') {
        pendingCause.current = 'command';
      }
      if (frame.current !== undefined) {
        return;
      }
      frame.current = requestAnimationFrame(() => {
        frame.current = undefined;
        const committed = pendingCause.current;
        pendingCause.current = 'gesture';
        setViewportState(latest.current);
        onChange.current?.(latest.current, committed);
      });
    },
    [],
  );
  const update = useCallback(
    (fn: (current: Viewport) => Viewport, cause: ViewportChangeCause) => {
      commit(fn(latest.current), cause);
    },
    [commit],
  );

  useEffect(
    () => () => {
      if (frame.current !== undefined) {
        cancelAnimationFrame(frame.current);
      }
    },
    [],
  );

  // Measure the box.
  const origin = useRef<Point | undefined>(undefined);
  useLayoutEffect(() => {
    if (fixedSize) {
      return;
    }
    const el = element.current;
    if (!el) {
      return;
    }
    const read = (): void => {
      const rect = el.getBoundingClientRect();
      // The shape panel docks beside the canvas, so opening it moves the canvas's left edge 240px right. The viewport
      // is relative to that edge, so without this the drawing jumps with it (QA-04): pan by the same amount the other
      // way and the drawing stays where it was on screen.
      const previous = origin.current;
      origin.current = { x: rect.left, y: rect.top };
      if (previous !== undefined && (previous.x !== rect.left || previous.y !== rect.top) && sizeRef.current.width > 0) {
        // Applied now, in the same render as the new size: going through the next-frame commit would show the jump for a frame.
        const shifted = keepInPlace(latest.current, previous, origin.current);
        latest.current = shifted;
        setViewportState(shifted);
        onChange.current?.(shifted, 'command');
      }
      setMeasuredSize((current) => (current.width === rect.width && current.height === rect.height ? current : { width: rect.width, height: rect.height }));
    };
    read();
    if (typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver(read);
    observer.observe(el);
    return () => {
      observer.disconnect();
    };
  }, [fixedSize]);

  // Fit once per document (or fitKey) as soon as the box has a size.
  const fitFor = fitKey ?? doc;
  const fittedFor = useRef<unknown>(undefined);
  const fit = useCallback(() => {
    commit(fitToContent(indexRef.current.contentBounds, sizeRef.current, FIT_PADDING), 'command');
  }, [commit]);
  useLayoutEffect(() => {
    if (fittedFor.current === fitFor || size.width === 0 || size.height === 0) {
      return;
    }
    fittedFor.current = fitFor;
    const fitted = fitToContent(contentBounds, size, FIT_PADDING);
    latest.current = fitted;
    setViewportState(fitted);
    onChange.current?.(fitted, 'command');
  }, [fitFor, contentBounds, size]);

  const centre = useCallback((): Pointer => ({ x: sizeRef.current.width / 2, y: sizeRef.current.height / 2 }), []);
  // For a drop from outside the canvas's own pointer handling (the shape panel's HTML5 drag), which reports client
  // coordinates rather than a PointerEvent local to this element.
  const pointToWorld = useCallback((client: Point): Point => {
    const rect = element.current?.getBoundingClientRect();
    const local = rect === undefined ? client : { x: client.x - rect.left, y: client.y - rect.top };
    return screenToWorld(latest.current, local);
  }, []);
  const zoomIn = useCallback(() => {
    update((vp) => zoomAround(vp, centre(), ZOOM_STEP), 'command');
  }, [update, centre]);
  const zoomOut = useCallback(() => {
    update((vp) => zoomAround(vp, centre(), 1 / ZOOM_STEP), 'command');
  }, [update, centre]);
  const reset = useCallback(() => {
    update((vp) => zoomTo100(vp, sizeRef.current), 'command');
  }, [update]);

  // Script focus after a pointer press matches :focus-visible in Chromium (the compat mousedown that would classify
  // it as pointer-driven is cancelled), so mark it and let app.css keep the ring keyboard-only until a key or blur.
  const focusFromPointer = useCallback(() => {
    const el = element.current;
    if (!el) {
      return;
    }
    el.setAttribute('data-pointer-focus', '');
    el.focus({ preventScroll: true });
  }, []);
  const focusCanvas = useCallback(
    (options?: { visible?: boolean }) => {
      if (options?.visible === true) {
        element.current?.removeAttribute('data-pointer-focus');
        element.current?.focus({ preventScroll: true });
      } else {
        focusFromPointer();
      }
    },
    [focusFromPointer],
  );

  const showView = useCallback(
    (view: ViewRect) => {
      if (sizeRef.current.width > 0 && sizeRef.current.height > 0) {
        commit(viewportForCamera(cameraForView(view, sizeRef.current), sizeRef.current), 'command');
      }
    },
    [commit],
  );

  const select = useCallback((ids: ReadonlySet<Id>) => {
    setSelection(ids);
    onSelectionChangeRef.current?.(ids);
  }, []);

  const reveal = useCallback((id: Id) => {
    if (!indexRef.current.nodesById.has(id)) {
      return;
    }
    select(new Set([id]));
    const bounds = indexRef.current.nodeBounds.get(id);
    if (bounds !== undefined && sizeRef.current.width > 0 && sizeRef.current.height > 0) {
      update((current) => centreOn(current, sizeRef.current, { x: bounds.x + bounds.w / 2, y: bounds.y + bounds.h / 2 }), 'command');
    }
  }, [select, update]);

  // A node the canvas draws, or an edge it draws a route for: an element with nowhere to show an editor is left alone.
  const labelled = useCallback((id: Id): boolean => indexRef.current.placed.has(id) || indexRef.current.edgePaths.has(id), []);
  const startEditing = useCallback(
    (id: Id, part?: MemberPart, at?: Point) => {
      // A combined fragment has no label of its own to edit: its text is its guards, so a double-click or Enter edits the
      // guard under the pointer (the first one from the keyboard).
      const frame = indexRef.current.sequence?.fragments.find((fragment) => fragment.id === id);
      if (frame !== undefined) {
        if (editableRef.current && onGuardChangeRef.current !== undefined) {
          setEditingGuard({ fragment: id, operand: guardAt(frame, at) });
        }
        return;
      }
      if (editableRef.current && labelled(id)) {
        // A member compartment is only editable on a class, and only when the caller takes member lists.
        const node = indexRef.current.placed.get(id);
        const memberEdit = part !== undefined && onMembersChangeRef.current !== undefined && node !== undefined && memberPartsOf(node.shape).includes(part);
        setEditingPart(memberEdit ? part : undefined);
        setEditing(id);
      }
    },
    [labelled],
  );
  // For an element added in this same event: the render that draws it also opens the editor (or drops it if it never comes).
  const edit = useCallback((id: Id) => {
    if (editableRef.current) {
      setEditingPart(undefined);
      setEditing(id);
    }
  }, []);
  const editGuard = useCallback((fragment: Id, operand: number) => {
    if (editableRef.current && onGuardChangeRef.current !== undefined) {
      setEditingGuard({ fragment, operand });
    }
  }, []);
  const endEditing = useCallback((end: LabelEditEnd) => {
    setEditing(undefined);
    setEditingPart(undefined);
    setEditingGuard(undefined);
    // Leaving from the keyboard hands the keys back to the canvas; a click elsewhere keeps focus where it went.
    if (end === 'keyboard') {
      element.current?.focus({ preventScroll: true });
    }
  }, []);
  const commitLabel = useCallback(
    (text: string, end: LabelEditEnd) => {
      const id = editing;
      endEditing(end);
      if (id !== undefined) {
        onLabelChangeRef.current?.(id, text);
      }
    },
    [editing, endEditing],
  );
  const commitGuard = useCallback(
    (text: string, end: LabelEditEnd) => {
      const guard = editingGuard;
      endEditing(end);
      if (guard !== undefined) {
        onGuardChangeRef.current?.(guard.fragment, guard.operand, text);
      }
    },
    [editingGuard, endEditing],
  );
  const commitMembers = useCallback(
    (text: string, end: LabelEditEnd) => {
      const id = editing;
      const part = editingPart;
      endEditing(end);
      if (id !== undefined && part !== undefined) {
        onMembersChangeRef.current?.(id, part, membersOfText(text));
      }
    },
    [editing, editingPart, endEditing],
  );

  // Whether a pointer gesture is under way; the pointer effect below points it at its own gesture state.
  const gestureActive = useRef<() => boolean>(() => false);
  // The canvas's keyboard commands (editor/actions.ts). Each reads the latest selection, document and callbacks
  // through refs, so the list only changes with readOnly; the keydown listener and the palette run the same records.
  const actions = useMemo<readonly EditorAction[]>(() => {
    // An edit never runs read-only or mid-gesture.
    const canEdit = (): boolean => !readOnly && !gestureActive.current();
    const nudgeStarts = (): ReadonlyMap<Id, Geometry> => (canEdit() && !derived && onMoveNodesRef.current ? dragStarts(indexRef.current, selectionRef.current) : new Map<Id, Geometry>());
    const zoom = (id: string, label: string, shortcut: string, command: ReturnType<typeof zoomShortcut>, run: () => void): EditorAction => ({
      id,
      label,
      group: 'View',
      shortcut: { label: shortcut, matches: (e) => zoomShortcut(e) === command },
      enabled: () => true,
      run,
    });
    const view = [
      zoom('canvas.zoomIn', 'Zoom in', 'Cmd/Ctrl+=', 'zoomIn', zoomIn),
      zoom('canvas.zoomOut', 'Zoom out', 'Cmd/Ctrl+-', 'zoomOut', zoomOut),
      zoom('canvas.fit', 'Fit to screen', 'Cmd/Ctrl+0', 'fit', fit),
      zoom('canvas.zoomTo100', 'Zoom to 100%', 'Cmd/Ctrl+1', 'zoomTo100', reset),
    ];
    if (readOnly) {
      return view;
    }
    const editLabel: EditorAction = {
      id: 'canvas.editLabel',
      label: 'Edit label',
      group: 'Edit',
      shortcut: { label: 'Enter', matches: isEditLabelKey },
      enabled: () => {
        const [only] = selectionRef.current;
        return editableRef.current && selectionRef.current.size === 1 && only !== undefined && labelled(only);
      },
      run: () => {
        const [only] = selectionRef.current;
        if (only !== undefined) {
          startEditing(only);
        }
      },
    };
    // The two member compartments of a UML class, also reachable from the keyboard and the menu (double-click is the pointer route).
    const editMembers = (part: MemberPart): EditorAction => ({
      id: part === 'attributes' ? 'canvas.editAttributes' : part === 'operations' ? 'canvas.editOperations' : 'canvas.editColumns',
      label: part === 'attributes' ? 'Edit attributes' : part === 'operations' ? 'Edit operations' : 'Edit columns',
      group: 'Edit',
      keywords: part === 'columns' ? 'er entity table database members' : 'uml class members',
      enabled: () => {
        const [only] = selectionRef.current;
        const node = only === undefined ? undefined : indexRef.current.placed.get(only);
        return editableRef.current && onMembersChangeRef.current !== undefined && selectionRef.current.size === 1 && node !== undefined && memberPartsOf(node.shape).includes(part);
      },
      run: () => {
        const [only] = selectionRef.current;
        if (only !== undefined) {
          startEditing(only, part);
        }
      },
    });
    const remove: EditorAction = {
      id: 'canvas.delete',
      label: 'Delete',
      group: 'Edit',
      keywords: 'remove backspace selection',
      shortcut: { label: 'Delete', matches: isDeleteCommand },
      enabled: () => canEdit() && onDeleteElementsRef.current !== undefined && selectionRef.current.size > 0,
      run: () => {
        onDeleteElementsRef.current?.(selectionRef.current);
        select(NO_SELECTION);
      },
    };
    const nudges = NUDGE_ACTIONS.flatMap(({ name, key, glyph, direction }) =>
      [false, true].map((large): EditorAction => {
        const step = large ? NUDGE_STEP_LARGE : NUDGE_STEP;
        return {
          id: `canvas.nudge.${name}${large ? '.large' : ''}`,
          label: `Nudge ${name} ${String(step)}px`,
          group: 'Move',
          shortcut: {
            label: large ? `Shift+${glyph}` : glyph,
            matches: (e) => e.key === key && nudgeDelta(e) !== undefined && e.shiftKey === large,
          },
          enabled: () => nudgeStarts().size > 0,
          run: () => {
            const starts = nudgeStarts();
            if (starts.size > 0) {
              const dx = direction.x * step;
              const dy = direction.y * step;
              const waypoints = translateWaypoints(waypointStarts(indexRef.current.document, starts), dx, dy);
              if (waypoints.size === 0) {
                onMoveNodesRef.current?.(translateGeometries(starts, dx, dy));
              } else {
                onMoveNodesRef.current?.(translateGeometries(starts, dx, dy), waypoints);
              }
              onMoveEndRef.current?.();
            }
          },
        };
      }),
    );
    // Held back from the browser's Back and Forward even with nothing selected, so the key cannot leave the editor.
    const order = ARRANGE_ACTIONS.map(
      ({ command, label, shortcut }): EditorAction => ({
        id: `canvas.arrange.${command}`,
        label: derived ? SEQUENCE_ARRANGE_LABELS[command] : label,
        group: 'Arrange',
        shortcut: { label: shortcut, matches: (e) => arrangeShortcut(e) === command, claim: () => onArrangeRef.current !== undefined },
        enabled: () => canEdit() && onArrangeRef.current !== undefined && selectionRef.current.size > 0,
        run: () => {
          onArrangeRef.current?.(selectionRef.current, command);
        },
      }),
    );
    // Held back from the browser's find-next even with nothing selected, as the arrange keys are. The selection
    // becomes whatever onGroup returns: the new container, or the released children.
    const grouping = GROUP_ACTIONS.map(
      ({ command, label, shortcut }): EditorAction => ({
        id: `canvas.${command}`,
        label,
        group: 'Arrange',
        shortcut: { label: shortcut, matches: (e) => groupShortcut(e) === command, claim: () => onGroupRef.current !== undefined },
        enabled: () => canEdit() && !derived && onGroupRef.current !== undefined && selectionRef.current.size > 0,
        run: () => {
          const after = onGroupRef.current?.(selectionRef.current, command);
          if (after !== undefined) {
            select(after);
          }
        },
      }),
    );
    const duplicate: EditorAction = {
      id: 'canvas.duplicate',
      label: 'Duplicate',
      group: 'Edit',
      keywords: 'copy clone selection',
      shortcut: { label: 'Cmd/Ctrl+D', matches: (e) => clipboardCommand(e) === 'duplicate', claim: () => onClipboardRef.current !== undefined },
      enabled: () => canEdit() && !derived && onClipboardRef.current !== undefined && selectionRef.current.size > 0,
      run: () => {
        onClipboardRef.current?.('duplicate', selectionRef.current);
      },
    };
    // Read-only, nothing to walk, or past either end: disabled, so Tab keeps its usual job of moving focus on.
    const walkTo = (step: 1 | -1): ReadonlySet<Id> | undefined => {
      const { document: current, placed, edgePaths } = indexRef.current;
      const nodeOrder = current.nodes.flatMap((node) => (placed.has(node.id) ? [node.id] : []));
      // A message is only reachable from the keyboard here, so a derived-geometry diagram walks them after its participants.
      const order = derived ? [...nodeOrder, ...current.edges.flatMap((edge) => (edgePaths.has(edge.id) ? [edge.id] : []))] : nodeOrder;
      return canEdit() ? stepSelection(order, selectionRef.current, step) : undefined;
    };
    const walk = (step: 1 | -1, id: string, label: string, shortcut: string): EditorAction => ({
      id,
      label,
      group: 'Move',
      keywords: 'tab walk',
      shortcut: { label: shortcut, matches: (e) => selectionStep(e) === step },
      enabled: () => walkTo(step) !== undefined,
      run: () => {
        const next = walkTo(step);
        if (next === undefined) {
          return;
        }
        select(next);
        const [only] = next;
        const node = only === undefined ? undefined : indexRef.current.nodesById.get(only);
        const message = only === undefined || !derived ? undefined : indexRef.current.edgesById.get(only);
        if (node !== undefined) {
          setAnnounced(`${node.label || node.id}, ${node.kind}`);
        } else if (message !== undefined) {
          setAnnounced(`${message.label ?? message.id}, ${message.kind}`);
        }
      },
    });
    const walks = [walk(1, 'canvas.selectNext', 'Select next node', 'Tab'), walk(-1, 'canvas.selectPrevious', 'Select previous node', 'Shift+Tab')];
    // A derived-geometry (sequence) diagram has no class or table compartments and no containers to group into, so those
    // entries are left out of the registry rather than listed greyed: the menu and palette only show what can apply.
    const presentParts = memberPartsKey === '' ? [] : memberPartsKey.split(',');
    const notForSequence = derived ? [] : (['attributes', 'operations', 'columns'] as const).filter((part) => presentParts.includes(part)).map(editMembers);
    return [editLabel, ...notForSequence, remove, duplicate, ...walks, ...nudges, ...order, ...(derived ? [] : grouping), ...view];
  }, [readOnly, derived, zoomIn, zoomOut, fit, reset, startEditing, labelled, select, memberPartsKey]);
  const actionsRef = useRef(actions);
  actionsRef.current = actions;

  useImperativeHandle(
    ref,
    () => ({
      fit,
      zoomTo100: reset,
      zoomIn,
      zoomOut,
      getViewport: () => latest.current,
      focus: focusCanvas,
      select,
      reveal,
      viewCentre: () => screenToWorld(latest.current, centre()),
      viewRect: () => viewRectOf(latest.current, sizeRef.current),
      showView,
      actions,
      pointToWorld,
      edit,
      editGuard,
    }),
    [fit, reset, zoomIn, zoomOut, focusCanvas, select, reveal, centre, showView, actions, pointToWorld, edit, editGuard],
  );

  // Pointer and wheel interaction, bound natively so wheel can be non-passive.
  useEffect(() => {
    const el = element.current;
    if (!el) {
      return;
    }
    const pointers = new Map<number, Pointer>();
    let pinch: { distance: number; mid: Pointer } | undefined;
    let spaceDown = false;
    // A mouse press on an element: a click until it travels DRAG_THRESHOLD, then a drag of `starts` by `delta` (as the
    // last frame snapped it), snapping to `others`.
    let press: { pointerId: number; pressedId: Id; onNode: boolean; shift: boolean; startScreen: Pointer; startWorld: Pointer; moved: boolean; others: readonly Rect[]; delta: Point; reorder: boolean; at?: Point } | undefined;
    // The node or edge the last click landed on, for a double-click: under pointer capture the browser may target the
    // canvas.
    let clicked: Id | undefined;
    // A Cmd/Ctrl+C, X or V still waiting for the copy, cut or paste event the browser fires after it.
    let clipboardFallback: ReturnType<typeof setTimeout> | undefined;
    // A mouse press on a resize handle: nothing is written until it travels DRAG_THRESHOLD. `lock` is the one axis a
    // bar shape (the fork/join bar) may resize along, fixed from the node's shape at the press.
    let resize: { pointerId: number; id: Id; handle: Pointer; start: Geometry; startScreen: Pointer; startWorld: Pointer; moved: boolean; others: readonly Rect[]; lock: ResizeLock | undefined } | undefined;
    // A mouse press on the rotate handle: `centre` and `startAngle` are the node's world-space centre and the
    // pointer's angle from it at the press, so a move's angle need only be diffed against `startAngle`. Nothing is
    // written until it travels DRAG_THRESHOLD, as for a resize handle.
    let rotate: { pointerId: number; id: Id; start: Geometry; centre: Point; startAngle: number; startScreen: Pointer; moved: boolean } | undefined;
    // A mouse press on empty canvas in the select tool: nothing is drawn until it travels DRAG_THRESHOLD. `base` is the
    // selection a Shift marquee adds to.
    let marqueeDrag: { pointerId: number; base: ReadonlySet<Id>; shift: boolean; startScreen: Pointer; startWorld: Pointer; moved: boolean } | undefined;
    // A connector gesture: the tool it started in, the node and port it started on, and where a release would attach.
    // `pointerType` and `screen` (its last local position) let a second touch arriving mid-gesture cancel a pen or
    // touch connect and hand both pointers to the two-finger pinch below instead of fighting it for the pointer.
    let connect: { pointerId: number; pointerType: string; screen: Pointer; tool: CanvasTool; from: EndHit; to: EndHit | undefined } | undefined;
    // A re-attach: the edge, the end being moved, the end staying put (and where it is drawn), and the drop target.
    let reattach: { pointerId: number; pointerType: string; screen: Pointer; edge: Id; end: EdgeEndName; fixed: Endpoint; origin: Point; to: EndHit | undefined } | undefined;
    // A segment drag: pinning a connector's route by dragging one of its segments/waypoints (segmentDrag.ts).
    let segmentDrag: { pointerId: number; pointerType: string; screen: Pointer; edge: Id; mode: 'orthogonal' | 'curved'; startWorld: Point; orth?: { points: readonly Point[]; segmentIndex: number }; curved?: { waypoints: readonly Point[]; index: number } } | undefined;
    // Touch or pen pointers that went down on a node in the connector tool without starting a connect (a second
    // finger, mid-pinch): they join a pinch but never pan alone.
    const heldOnNode = new Set<number>();
    gestureActive.current = () => press !== undefined || resize !== undefined || rotate !== undefined || connect !== undefined || reattach !== undefined || segmentDrag !== undefined || marqueeDrag !== undefined;
    // Whether `pointerType` is a finger or stylus rather than a mouse: touch and pen share the larger hit targets and
    // the two-finger-cancels-a-gesture rule; a mouse's targets and gestures are unchanged from before this feature.
    const isTouchLike = (pointerType: string): boolean => pointerType === 'touch' || pointerType === 'pen';

    // Drag and resize writes are coalesced to one per animation frame, then handed to onMoveNodes as absolute geometry.
    // Whether the gesture in progress has handed onMoveNodes anything yet, which is what Escape has to take back.
    let wroteFrames = false;
    const writeDrag = (): void => {
      if (pendingDrag.current !== undefined) {
        const write = pendingDrag.current;
        pendingDrag.current = undefined;
        const { nodes, waypoints } = write();
        wroteFrames = true;
        if (waypoints === undefined || waypoints.size === 0) {
          onMoveNodesRef.current?.(nodes);
        } else {
          onMoveNodesRef.current?.(nodes, waypoints);
        }
      }
    };
    const commitDragNow = (): void => {
      if (dragFrame.current !== undefined) {
        cancelAnimationFrame(dragFrame.current);
        dragFrame.current = undefined;
      }
      writeDrag();
    };
    const scheduleDrag = (write: () => DragWrite): void => {
      pendingDrag.current = write;
      dragFrame.current ??= requestAnimationFrame(() => {
        dragFrame.current = undefined;
        writeDrag();
      });
    };

    const snapContext = (others: readonly Rect[]): SnapContext => ({ others, gridSize: gridRef.current.size, gridEnabled: gridRef.current.enabled, zoom: latest.current.zoom });
    const showGuides = (next: readonly Guide[]): void => {
      setGuides(next.length === 0 ? NO_GUIDES : next);
    };
    const replaceSelection = (next: ReadonlySet<Id>): void => {
      setSelection(next);
      onSelectionChangeRef.current?.(next);
    };

    const local = (e: PointerEvent | WheelEvent): Pointer => {
      const rect = el.getBoundingClientRect();
      return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    };
    // Connector hit-testing in world units: the radius is a screen size, so it shrinks as the view zooms in. A mouse
    // keeps the smaller PORT_HIT_RADIUS; a pen or touch press gets the larger TOUCH_HIT_RADIUS (ports.ts).
    const portRadius = (): number => PORT_HIT_RADIUS / latest.current.zoom;
    const worldHitRadius = (pointerType: string): number => (isTouchLike(pointerType) ? TOUCH_HIT_RADIUS : PORT_HIT_RADIUS) / latest.current.zoom;
    const endAt = (current: DocumentIndex, world: Point, radius: number): EndHit | undefined => {
      if (derivedRef.current) {
        // A participant has no ports: its header or lifeline is the target, and the pointer's y is the message's place.
        const id = current.sequence === undefined ? undefined : participantAt(current.sequence, world, radius);
        return id === undefined ? undefined : { node: id, port: NO_PORT, pinned: true };
      }
      const node = nodeAt(current, world, radius);
      return node === undefined ? undefined : { node: node.id, ...attachmentHit(node, world, radius) };
    };
    // The ports a finished or previewed connection uses: a press or release on a node's body takes the side facing the
    // other end, and only an end on the port itself (inside its ring) pins that port (ports.ts `pairPorts`).
    const resolveConnection = (from: EndHit, to: EndHit): Connection => {
      const a = indexRef.current.placed.get(from.node);
      const b = indexRef.current.placed.get(to.node);
      if (derivedRef.current || a === undefined || b === undefined) {
        return { from: { node: from.node, port: from.port }, to: { node: to.node, port: to.port } };
      }
      const ports = pairPorts({ node: a, hit: from }, { node: b, hit: to });
      return { from: { node: from.node, port: ports.a }, to: { node: to.node, port: ports.b } };
    };
    // A re-attached end lands on the port facing the end that stays put, unless dropped on a port.
    const resolveReattach = (to: EndHit, origin: Point): Connection['to'] => {
      const node = indexRef.current.placed.get(to.node);
      return { node: to.node, port: to.pinned || derivedRef.current || node === undefined ? to.port : portFacing(node, origin) };
    };
    const showHover = (world: Point): void => {
      if (derivedRef.current) {
        setConnector(undefined);
        return;
      }
      const node = nodeAt(indexRef.current, world, portRadius());
      const port = node === undefined ? undefined : (columnPortAt(node, world) ?? portAt(node, world, portRadius()));
      setConnector((current) =>
        current?.source?.node === node?.id && current?.source?.port === port?.id && current?.pointer === undefined
          ? current
          : node === undefined
            ? undefined
            : { source: { node: node.id, port: port?.id } },
      );
    };
    const showDraft = (from: EndHit, to: EndHit | undefined, world: Point): void => {
      const scene = indexRef.current.sequence;
      if (derivedRef.current && scene !== undefined) {
        // The preview runs along the slot the release would pick, from the source lifeline to the target's (or the pointer).
        const y = slotLineY(scene, messageSlot(scene, world.y));
        const centreOf = (id: Id): number | undefined => scene.headers.find((header) => header.id === id)?.cx;
        const start = centreOf(from.node) ?? world.x;
        const end = to === undefined ? world.x : to.node === from.node ? start + 30 : (centreOf(to.node) ?? world.x);
        setConnector({ origin: { x: start, y }, pointer: { x: end, y } });
        return;
      }
      const joined = to !== undefined && to.node !== from.node ? resolveConnection(from, to) : undefined;
      const source = joined?.from ?? from;
      setConnector({ source: { node: from.node, port: source.port.id }, target: joined === undefined ? undefined : { node: joined.to.node, port: joined.to.port.id }, pointer: world });
    };
    const showReattach = (fixed: Endpoint, origin: Point, to: EndHit | undefined, world: Point): void => {
      const landed = to !== undefined && to.node !== fixed.node ? resolveReattach(to, origin) : undefined;
      const target = landed === undefined ? undefined : { node: landed.node, port: landed.port.id };
      setConnector({ origin, target, pointer: world });
    };
    // Whether a connection gesture may still finish: the tool it began in is still active and still connects.
    const stillConnecting = (started: CanvasTool): boolean => (started === 'connector' ? connectingRef.current : selectHandlesRef.current);
    // The select tool's connection handles under a world point: the candidates, and the one within reach. `pointerType`
    // widens the reach for a pen or touch press (TOUCH_HIT_RADIUS); a mouse keeps CONNECT_HIT_RADIUS.
    const handleAt = (world: Point, pointerType: string): { hovered: Id | undefined; hit: ReturnType<typeof connectHandleAt> } => {
      const zoom = latest.current.zoom;
      const hovered = handleHoverNode(indexRef.current, world, zoom)?.id;
      const reach = isTouchLike(pointerType) ? TOUCH_HIT_RADIUS : CONNECT_HIT_RADIUS;
      return { hovered, hit: connectHandleAt(handleNodes(indexRef.current, hovered, selectionRef.current), world, zoom, rotateHandleNode(selectionRef.current), reach) };
    };
    const showHandleHover = (world: Point): void => {
      const { hovered, hit } = handleAt(world, 'mouse');
      const active = hit === undefined ? undefined : { node: hit.node.id, port: hit.port.id };
      setHandleHover((current) =>
        current?.node === hovered && current?.active?.node === active?.node && current?.active?.port === active?.port ? current : { node: hovered, active },
      );
    };

    const pinchState = (): { distance: number; mid: Pointer } | undefined => {
      const [a, b] = [...pointers.values()];
      if (!a || !b) {
        return undefined;
      }
      return { distance: Math.hypot(b.x - a.x, b.y - a.y), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
    };

    const onWheel = (e: WheelEvent): void => {
      e.preventDefault();
      const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 100 : 1;
      if (e.ctrlKey || e.metaKey) {
        const at = local(e);
        // A pinch arrives as small fractional deltas; a mouse notch is ±100, so clamp it to a sane step per notch.
        const delta = Math.max(-WHEEL_ZOOM_MAX_DELTA, Math.min(WHEEL_ZOOM_MAX_DELTA, e.deltaY * unit));
        const factor = Math.exp(-delta * WHEEL_ZOOM_RATE);
        update((vp) => zoomAround(vp, at, factor), 'gesture');
        return;
      }
      const dx = e.deltaX * unit;
      const dy = e.deltaY * unit;
      update((vp) => pan(vp, -dx, -dy), 'gesture');
    };

    // Starts a connector route drag, shared by a press on the segment and on a segment handle (segmentDrag.ts), so a
    // handle only surfaces the drag that already exists. False when the route is not drawable after all.
    const beginSegmentDrag = (e: PointerEvent, edgeId: Id, mode: 'orthogonal' | 'curved', world: Point, index: number): boolean => {
      if (mode === 'orthogonal') {
        const points = routesRef.current.get(edgeId)?.points ?? indexRef.current.edgePaths.get(edgeId)?.points;
        if (points === undefined || points.length === 0) {
          return false;
        }
        segmentDrag = { pointerId: e.pointerId, pointerType: e.pointerType, screen: local(e), edge: edgeId, mode, startWorld: world, orth: { points, segmentIndex: index } };
      } else {
        const edge = indexRef.current.edgesById.get(edgeId);
        segmentDrag = { pointerId: e.pointerId, pointerType: e.pointerType, screen: local(e), edge: edgeId, mode, startWorld: world, curved: { waypoints: edge?.routing?.waypoints ?? [], index } };
      }
      el.setPointerCapture(e.pointerId);
      return true;
    };

    const onPointerDown = (e: PointerEvent): void => {
      wroteFrames = false;
      const mouse = e.pointerType === 'mouse';
      const middle = mouse && e.button === 1;
      const primary = e.button === 0;
      if (!primary && !middle) {
        return;
      }
      // A second touch or pen arriving while a single-pointer connect, re-attach or segment drag is under way from a
      // different (touch or pen) pointer cancels that gesture — nothing is created or written — and folds its pointer
      // into the generic multi-pointer tracking below, so the pair pinch-pans exactly as two fingers always have.
      if (!mouse && pointers.size === 0) {
        const active = connect ?? reattach ?? segmentDrag;
        if (active !== undefined && active.pointerId !== e.pointerId && isTouchLike(active.pointerType)) {
          if (el.hasPointerCapture(active.pointerId)) {
            el.releasePointerCapture(active.pointerId);
          }
          pointers.set(active.pointerId, active.screen);
          connect = undefined;
          reattach = undefined;
          segmentDrag = undefined;
          setConnector(undefined);
        }
      }
      // Only a lone pen or touch pointer, with none of these gestures already under way, may start one; a second
      // finger down always means the pinch above, never a competing gesture.
      const singleTouch = pointers.size === 0 && connect === undefined && reattach === undefined && segmentDrag === undefined;
      const gesturePointer = mouse || (isTouchLike(e.pointerType) && singleTouch);
      // In the connector tool a press on a node starts a connection from the port under the pointer, or the nearest.
      // It comes before the resize handles: the middle handles sit exactly on a node's default ports.
      if (connectingRef.current && gesturePointer && primary && !spaceDown) {
        const world = screenToWorld(latest.current, local(e));
        const from = endAt(indexRef.current, world, worldHitRadius(e.pointerType));
        if (from !== undefined) {
          focusFromPointer();
          e.preventDefault();
          connect = { pointerId: e.pointerId, pointerType: e.pointerType, screen: local(e), tool: 'connector', from, to: undefined };
          showDraft(from, undefined, world);
          el.setPointerCapture(e.pointerId);
          return;
        }
      }
      // In the select tool a press on a connection handle starts the same gesture from that handle's port. The rotate
      // handle keeps its own press even where another (hovered) node's handle reaches over it.
      const onRotateHandle = e.target instanceof Element && e.target.closest('[data-rotate-handle]') !== null;
      if (selectHandlesRef.current && gesturePointer && primary && !spaceDown && !onRotateHandle) {
        const world = screenToWorld(latest.current, local(e));
        const { hit } = handleAt(world, e.pointerType);
        if (hit !== undefined) {
          focusFromPointer();
          e.preventDefault();
          clicked = undefined;
          const from: EndHit = { node: hit.node.id, port: hit.port, pinned: true };
          connect = { pointerId: e.pointerId, pointerType: e.pointerType, screen: local(e), tool: 'select', from, to: undefined };
          setHandleHover(undefined);
          showDraft(from, undefined, world);
          el.setPointerCapture(e.pointerId);
          return;
        }
      }
      // A press on a selected edge's end handle re-attaches that end; the connection handles have already had it.
      if (reattachableRef.current && gesturePointer && primary && !spaceDown) {
        const world = screenToWorld(latest.current, local(e));
        const reach = isTouchLike(e.pointerType) ? TOUCH_HIT_RADIUS : CONNECT_HIT_RADIUS;
        for (const [id, handles] of edgeEndsRef.current ?? []) {
          const edge = indexRef.current.edgesById.get(id);
          const routed = routesRef.current.get(id)?.points;
          const points = routed !== undefined && routed.length > 0 ? routed : indexRef.current.edgePaths.get(id)?.points;
          const end = endHandleAt(handles, world, latest.current.zoom, reach);
          if (edge !== undefined && points !== undefined && end !== undefined) {
            focusFromPointer();
            e.preventDefault();
            clicked = undefined;
            const origin = (end === 'from' ? points[points.length - 1] : points[0]) ?? world;
            reattach = { pointerId: e.pointerId, pointerType: e.pointerType, screen: local(e), edge: id, end, fixed: end === 'from' ? edge.to : edge.from, origin, to: undefined };
            setHandleHover(undefined);
            showReattach(reattach.fixed, origin, undefined, world);
            el.setPointerCapture(e.pointerId);
            return;
          }
        }
      }
      // A press on a selected connector's segment/waypoint handle starts exactly the drag a press on the route does.
      const segmentHandleTarget = mouse && primary && !spaceDown && !readOnly && !derivedRef.current && e.target instanceof Element ? e.target.closest('[data-segment-handle]') : null;
      const segmentHandleEdge = segmentHandleTarget?.getAttribute('data-edge-id') ?? undefined;
      const segmentHandleKind = segmentHandleTarget?.getAttribute('data-segment-kind');
      const segmentHandleIndex = segmentHandleTarget?.getAttribute('data-segment-index');
      if (segmentHandleEdge !== undefined && segmentHandleIndex !== null && segmentHandleIndex !== undefined && (segmentHandleKind === 'orthogonal' || segmentHandleKind === 'curved')) {
        focusFromPointer();
        e.preventDefault();
        clicked = undefined;
        beginSegmentDrag(e, segmentHandleEdge, segmentHandleKind, screenToWorld(latest.current, local(e)), Number(segmentHandleIndex));
        return;
      }
      // A pen or touch press directly on an already-selected connector's route pins it the same way (segmentDrag.ts);
      // its wider TOUCH_HIT_RADIUS reach stands in for the small `data-segment-handle` target a mouse presses exactly.
      if (!mouse && gesturePointer && primary && !spaceDown && !readOnly && !derivedRef.current && selectionRef.current.size === 1) {
        const target = e.target instanceof Element ? e.target.closest('[data-id].edge') : null;
        const edgeId = target?.getAttribute('data-id') ?? undefined;
        if (edgeId !== undefined && selectionRef.current.has(edgeId)) {
          const edge = indexRef.current.edgesById.get(edgeId);
          const mode = edge?.routing?.mode;
          const points = routesRef.current.get(edgeId)?.points ?? indexRef.current.edgePaths.get(edgeId)?.points;
          if (edge !== undefined && points !== undefined && points.length > 0) {
            const world = screenToWorld(latest.current, local(e));
            const reach = TOUCH_HIT_RADIUS / latest.current.zoom;
            if (mode === 'orthogonal') {
              const segmentIndex = middleSegmentAt(points, world, reach);
              if (segmentIndex !== undefined) {
                focusFromPointer();
                e.preventDefault();
                clicked = undefined;
                if (beginSegmentDrag(e, edgeId, 'orthogonal', world, segmentIndex)) {
                  return;
                }
              }
            } else if (mode === 'curved') {
              const hit = curveWaypointAt(edge.routing?.waypoints ?? [], world, reach);
              if (hit !== undefined) {
                focusFromPointer();
                e.preventDefault();
                clicked = undefined;
                beginSegmentDrag(e, edgeId, 'curved', world, hit);
                return;
              }
            }
          }
        }
      }

      // A touch or pen press on a node that did not start one of the gestures above (a second finger, mid-pinch, or
      // the connector tool with nothing to connect) still must not pan on its own; it still counts towards a
      // two-finger pinch.
      if (connectingRef.current && !mouse && e.target instanceof Element && e.target.closest('[data-id].node') !== null) {
        heldOnNode.add(e.pointerId);
      }

      // A press on a selected node's handle resizes that node (checklist item 3).
      const handleTarget = mouse && primary && !spaceDown && !readOnly && !derivedRef.current && e.target instanceof Element ? e.target.closest('[data-handle]') : null;
      const handleOf = handleTarget?.closest('[data-selection-id]')?.getAttribute('data-selection-id') ?? undefined;
      const handle = handleTarget ? HANDLE_ANCHORS[Number(handleTarget.getAttribute('data-handle'))] : undefined;
      const resized = handleOf === undefined ? undefined : indexRef.current.placed.get(handleOf);
      if (handle !== undefined && handleOf !== undefined && resized !== undefined) {
        focusFromPointer();
        e.preventDefault();
        pendingDrag.current = undefined;
        clicked = undefined;
        const screen = local(e);
        const others = snapTargets(indexRef.current, new Set([handleOf]));
        resize = { pointerId: e.pointerId, id: handleOf, handle, start: resized.geometry, startScreen: screen, startWorld: screenToWorld(latest.current, screen), moved: false, others, lock: resizeLockForShape(resized.shape) };
        el.setPointerCapture(e.pointerId);
        return;
      }

      // A press on the (single) selected node's rotate handle rotates it about its own centre (checklist item 4).
      const rotateTarget = mouse && primary && !spaceDown && !readOnly && !derivedRef.current && e.target instanceof Element ? e.target.closest('[data-rotate-handle]') : null;
      const rotateOf = rotateTarget?.closest('[data-selection-id]')?.getAttribute('data-selection-id') ?? undefined;
      const rotated = rotateOf === undefined ? undefined : indexRef.current.placed.get(rotateOf);
      if (rotateTarget !== null && rotateOf !== undefined && rotated !== undefined) {
        focusFromPointer();
        e.preventDefault();
        pendingDrag.current = undefined;
        clicked = undefined;
        const screen = local(e);
        const world = screenToWorld(latest.current, screen);
        const start = rotated.geometry;
        const centre = { x: start.x + start.w / 2, y: start.y + start.h / 2 };
        rotate = { pointerId: e.pointerId, id: rotateOf, start, centre, startAngle: angleFromCentre(centre, world), startScreen: screen, moved: false };
        setRotating({ id: rotateOf, rotation: normalizeRotation(start.rotation ?? 0) });
        el.setPointerCapture(e.pointerId);
        return;
      }

      const target = e.target instanceof Element ? e.target.closest('[data-id]') : null;
      const elementId = target?.getAttribute('data-id') ?? undefined;
      const onNode = target?.classList.contains('node') === true;
      const onEdge = target?.classList.contains('edge') === true;

      // With a mouse, a plain press on a node or edge is selection (checklist item 1); a press on a node also arms
      // a drag. Everywhere else — empty canvas, space held, middle button, touch — pans as it always did.
      if (mouse && primary && !spaceDown && elementId !== undefined && (onNode || onEdge)) {
        focusFromPointer();
        e.preventDefault();
        if (readOnly) {
          return;
        }
        const next = selectOnPress(selectionRef.current, elementId, e.shiftKey);
        setSelection(next);
        onSelectionChangeRef.current?.(next);
        // A press on an already-selected connector (the only thing selected) lets its route be pinned: an orthogonal
        // segment drags perpendicular, a curve adds or moves a waypoint (segmentDrag.ts). The end handles and
        // connection handles have already claimed their own presses.
        if (onEdge && !derivedRef.current && next.has(elementId) && selectionRef.current.has(elementId) && selectionRef.current.size === 1) {
          const edge = indexRef.current.edgesById.get(elementId);
          const mode = edge?.routing?.mode;
          const points = routesRef.current.get(elementId)?.points ?? indexRef.current.edgePaths.get(elementId)?.points;
          if (edge !== undefined && points !== undefined && points.length > 0) {
            const world = screenToWorld(latest.current, local(e));
            const reach = PORT_HIT_RADIUS / latest.current.zoom;
            if (mode === 'orthogonal') {
              const segmentIndex = middleSegmentAt(points, world, reach);
              if (segmentIndex !== undefined && beginSegmentDrag(e, elementId, 'orthogonal', world, segmentIndex)) {
                return;
              }
            } else if (mode === 'curved') {
              const hit = curveWaypointAt(edge.routing?.waypoints ?? [], world, reach);
              beginSegmentDrag(e, elementId, 'curved', world, hit ?? -1);
              return;
            }
          }
        }
        // Only a press on a node that stays selected drags; a press on an edge, or a Shift-press that deselected
        // the node, selects and releases.
        dragOrigins.current = onNode && next.has(elementId) && !derivedRef.current ? dragStarts(indexRef.current, next) : new Map<Id, Geometry>();
        waypointOrigins.current = waypointStarts(indexRef.current.document, dragOrigins.current);
        drop.current = onReparentNodesRef.current && dragOrigins.current.size > 0 ? dropPlan(indexRef.current.document, dragOrigins.current) : undefined;
        pendingDrag.current = undefined;
        const screen = local(e);
        press = { pointerId: e.pointerId, pressedId: elementId, onNode, shift: e.shiftKey, startScreen: screen, startWorld: screenToWorld(latest.current, screen), moved: false, others: [], delta: { x: 0, y: 0 }, reorder: derivedRef.current && next.has(elementId) };
        el.setPointerCapture(e.pointerId);
        return;
      }

      clicked = undefined;
      // A mouse press on empty canvas in the select tool starts a marquee; in the connector tool or a read-only view it
      // pans. Either way it clears the selection first, except that Shift keeps it for the marquee to add to.
      const marquees = mouse && primary && !spaceDown && !readOnly && !connectingRef.current;
      if (mouse && primary && !spaceDown && !(marquees && e.shiftKey) && selectionRef.current.size > 0) {
        replaceSelection(NO_SELECTION);
      }
      if (marquees) {
        focusFromPointer();
        e.preventDefault();
        const screen = local(e);
        marqueeDrag = { pointerId: e.pointerId, base: selectionRef.current, shift: e.shiftKey, startScreen: screen, startWorld: screenToWorld(latest.current, screen), moved: false };
        el.setPointerCapture(e.pointerId);
        return;
      }

      // preventDefault also cancels the browser's mousedown focus, so take focus explicitly or the
      // keyboard shortcuts and space-to-pan stay dead until the user tabs to the canvas.
      focusFromPointer();
      e.preventDefault();
      pointers.set(e.pointerId, local(e));
      el.setPointerCapture(e.pointerId);
      pinch = pointers.size >= 2 ? pinchState() : undefined;
      setDragging(true);
    };

    const onPointerMove = (e: PointerEvent): void => {
      if (connect?.pointerId === e.pointerId) {
        connect.screen = local(e);
        if (!stillConnecting(connect.tool)) {
          return;
        }
        const world = screenToWorld(latest.current, connect.screen);
        connect.to = endAt(indexRef.current, world, worldHitRadius(e.pointerType));
        showDraft(connect.from, connect.to, world);
        return;
      }
      if (reattach?.pointerId === e.pointerId) {
        reattach.screen = local(e);
        if (!reattachableRef.current) {
          return;
        }
        const world = screenToWorld(latest.current, reattach.screen);
        reattach.to = endAt(indexRef.current, world, worldHitRadius(e.pointerType));
        showReattach(reattach.fixed, reattach.origin, reattach.to, world);
        return;
      }
      if (segmentDrag?.pointerId === e.pointerId) {
        segmentDrag.screen = local(e);
        const world = screenToWorld(latest.current, local(e));
        const drag = segmentDrag;
        if (drag.mode === 'orthogonal' && drag.orth !== undefined) {
          const delta = { x: world.x - drag.startWorld.x, y: world.y - drag.startWorld.y };
          const waypoints = moveOrthSegment(drag.orth.points, drag.orth.segmentIndex, delta);
          scheduleDrag(() => ({ nodes: new Map<Id, Geometry>(), waypoints: new Map([[drag.edge, waypoints]]) }));
        } else if (drag.mode === 'curved' && drag.curved !== undefined) {
          const waypoints = moveCurvedWaypoint(drag.curved.waypoints, drag.curved.index, world);
          scheduleDrag(() => ({ nodes: new Map<Id, Geometry>(), waypoints: new Map([[drag.edge, waypoints]]) }));
        }
        return;
      }
      const idle = e.pointerType === 'mouse' && pointers.size === 0 && press === undefined && resize === undefined && marqueeDrag === undefined && connect === undefined && reattach === undefined && segmentDrag === undefined;
      if (connectingRef.current && idle) {
        showHover(screenToWorld(latest.current, local(e)));
      }
      if (selectHandlesRef.current && idle) {
        showHandleHover(screenToWorld(latest.current, local(e)));
      }
      if (marqueeDrag?.pointerId === e.pointerId) {
        const screen = local(e);
        if (!marqueeDrag.moved && Math.hypot(screen.x - marqueeDrag.startScreen.x, screen.y - marqueeDrag.startScreen.y) < DRAG_THRESHOLD) {
          return;
        }
        marqueeDrag.moved = true;
        setMarquee(marqueeRect(marqueeDrag.startWorld, screenToWorld(latest.current, screen)));
        return;
      }
      if (resize?.pointerId === e.pointerId) {
        const screen = local(e);
        if (!resize.moved && Math.hypot(screen.x - resize.startScreen.x, screen.y - resize.startScreen.y) < DRAG_THRESHOLD) {
          return;
        }
        resize.moved = true;
        const world = screenToWorld(latest.current, screen);
        const { id, handle, start, startWorld, others, lock } = resize;
        const delta = { x: world.x - startWorld.x, y: world.y - startWorld.y };
        const keepAspect = e.shiftKey;
        scheduleDrag(() => {
          // A locked shape (the fork/join bar) only resizes along its length: pin the perpendicular axis to the middle
          // so neither it nor the anchor moves, and snap only the axis that is actually changing.
          const effective = lockedHandle(handle, lock);
          const snapped = snapResize(start, effective, resizeGeometry(start, effective, delta, keepAspect), keepAspect, snapContext(others));
          showGuides(snapped.guides);
          return { nodes: new Map([[id, snapped.geometry]]) };
        });
        return;
      }
      if (rotate?.pointerId === e.pointerId) {
        const screen = local(e);
        if (!rotate.moved && Math.hypot(screen.x - rotate.startScreen.x, screen.y - rotate.startScreen.y) < DRAG_THRESHOLD) {
          return;
        }
        rotate.moved = true;
        const { id, start, centre, startAngle } = rotate;
        const world = screenToWorld(latest.current, screen);
        const pointerAngle = angleFromCentre(centre, world);
        const shiftKey = e.shiftKey;
        scheduleDrag(() => {
          const next = rotateGeometry(start, startAngle, pointerAngle, shiftKey);
          setRotating({ id, rotation: next.rotation ?? 0 });
          return { nodes: new Map([[id, next]]) };
        });
        return;
      }
      if (press?.pointerId === e.pointerId) {
        const screen = local(e);
        if (!press.moved) {
          if (Math.hypot(screen.x - press.startScreen.x, screen.y - press.startScreen.y) < DRAG_THRESHOLD) {
            return;
          }
          press.moved = true;
          if (onMoveNodesRef.current && !derivedRef.current) {
            setDraggedNodes(new Set(dragOrigins.current.keys()));
          }
          // The other nodes stand still for the whole drag, so their snap targets are gathered once.
          press.others = snapTargets(indexRef.current, new Set(dragOrigins.current.keys()));
        }
        const scene = indexRef.current.sequence;
        if (press.reorder && scene !== undefined) {
          // Nothing moves: the insertion line shows where the release would put it, along x for a participant, y for a message.
          const world = screenToWorld(latest.current, screen);
          const bounds = scene.bounds ?? { x: 0, y: 0, w: 0, h: 0 };
          press.at = world;
          showGuides(
            press.onNode
              ? (() => {
                  const x = slotLineX(scene, participantSlot(scene, world.x, press.pressedId), press.pressedId);
                  return [{ kind: 'edge' as const, from: { x, y: bounds.y }, to: { x, y: bounds.y + bounds.h } }];
                })()
              : (() => {
                  const y = slotLineY(scene, messageSlot(scene, world.y, press.pressedId), press.pressedId);
                  return [{ kind: 'edge' as const, from: { x: bounds.x, y }, to: { x: bounds.x + bounds.w, y } }];
                })(),
          );
          return;
        }
        if (dragOrigins.current.size > 0) {
          const world = screenToWorld(latest.current, screen);
          const dx = world.x - press.startWorld.x;
          const dy = world.y - press.startWorld.y;
          const origins = dragOrigins.current;
          const others = press.others;
          const waypoints = waypointOrigins.current;
          const plan = drop.current;
          const dragged = press;
          scheduleDrag(() => {
            const snapped = snapMove(origins, dx, dy, snapContext(others));
            showGuides(snapped.guides);
            // Snapping moves every dragged node by one offset; the contents' waypoints and the drop follow it.
            const delta = snappedDelta(origins, snapped.moves, dx, dy);
            dragged.delta = delta;
            if (plan !== undefined) {
              const targets = new Set([...dropTargets(plan, delta).values()].filter((target) => target !== null));
              setDropHighlight((current) => (sameIds(current, targets) ? current : targets));
            }
            return { nodes: snapped.moves, waypoints: translateWaypoints(waypoints, delta.x, delta.y) };
          });
        }
        return;
      }
      const previous = pointers.get(e.pointerId);
      if (!previous) {
        return;
      }
      const current = local(e);
      pointers.set(e.pointerId, current);
      if (pointers.size >= 2) {
        const next = pinchState();
        if (pinch && next && pinch.distance > 0) {
          const from = pinch;
          update((vp) => pan(zoomAround(vp, from.mid, next.distance / from.distance), next.mid.x - from.mid.x, next.mid.y - from.mid.y), 'gesture');
        }
        pinch = next;
        return;
      }
      if (heldOnNode.has(e.pointerId)) {
        return;
      }
      update((vp) => pan(vp, current.x - previous.x, current.y - previous.y), 'gesture');
    };

    const onPointerEnd = (e: PointerEvent): void => {
      if (connect?.pointerId === e.pointerId) {
        const ended = connect;
        connect = undefined;
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        const world = screenToWorld(latest.current, local(e));
        const to = e.type === 'pointerup' && stillConnecting(ended.tool) ? endAt(indexRef.current, world, worldHitRadius(e.pointerType)) : undefined;
        const scene = indexRef.current.sequence;
        if (derivedRef.current) {
          if (to !== undefined && scene !== undefined) {
            const index = insertionIndex(indexRef.current.document, scene, messageSlot(scene, world.y));
            onInsertMessageRef.current?.({ from: ended.from.node, to: to.node, index });
          }
        } else if (to !== undefined && to.node !== ended.from.node) {
          onConnectRef.current?.(resolveConnection(ended.from, to));
        }
        if (connectingRef.current) {
          showHover(world);
        } else {
          setConnector(undefined);
          if (selectHandlesRef.current) {
            showHandleHover(world);
          }
        }
        return;
      }
      if (reattach?.pointerId === e.pointerId) {
        const ended = reattach;
        reattach = undefined;
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        setConnector(undefined);
        const world = screenToWorld(latest.current, local(e));
        const to = e.type === 'pointerup' && reattachableRef.current ? endAt(indexRef.current, world, worldHitRadius(e.pointerType)) : undefined;
        const current = indexRef.current.edgesById.get(ended.edge)?.[ended.end];
        const landed = to === undefined ? undefined : resolveReattach(to, ended.origin);
        const unchanged = landed !== undefined && current?.node === landed.node && current.port === landed.port.id;
        if (landed !== undefined && current !== undefined && landed.node !== ended.fixed.node && !unchanged) {
          onReconnectRef.current?.({ edge: ended.edge, end: ended.end, to: landed });
        }
        if (selectHandlesRef.current) {
          showHandleHover(world);
        } else if (connectingRef.current) {
          showHover(world);
        }
        return;
      }
      if (segmentDrag?.pointerId === e.pointerId) {
        segmentDrag = undefined;
        commitDragNow();
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        onMoveEndRef.current?.();
        return;
      }
      if (marqueeDrag?.pointerId === e.pointerId) {
        const ended = marqueeDrag;
        marqueeDrag = undefined;
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        setMarquee(undefined);
        if (ended.moved && e.type === 'pointerup') {
          const rect = marqueeRect(ended.startWorld, screenToWorld(latest.current, local(e)));
          replaceSelection(marqueeSelection(ended.base, nodesInMarquee(indexRef.current, rect, e.altKey), ended.shift));
        }
        return;
      }
      if (resize?.pointerId === e.pointerId) {
        const ended = resize;
        resize = undefined;
        commitDragNow();
        showGuides(NO_GUIDES);
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        if (ended.moved) {
          onMoveEndRef.current?.();
        }
        return;
      }
      if (rotate?.pointerId === e.pointerId) {
        const ended = rotate;
        rotate = undefined;
        commitDragNow();
        setRotating(undefined);
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        if (ended.moved) {
          onMoveEndRef.current?.();
        }
        return;
      }
      if (press?.pointerId === e.pointerId) {
        const ended = press;
        press = undefined;
        clicked = !ended.moved && !ended.shift ? ended.pressedId : undefined;
        commitDragNow();
        showGuides(NO_GUIDES);
        setDraggedNodes(NO_SELECTION);
        setDropHighlight(NO_SELECTION);
        if (el.hasPointerCapture(e.pointerId)) {
          el.releasePointerCapture(e.pointerId);
        }
        if (ended.moved && ended.reorder && ended.at !== undefined && e.type === 'pointerup') {
          onReorderRef.current?.(ended.pressedId, ended.at);
        }
        if (ended.moved && dragOrigins.current.size > 0) {
          const reparented = drop.current === undefined ? undefined : dropParents(indexRef.current.document, drop.current, ended.delta);
          if (reparented !== undefined && reparented.size > 0) {
            onReparentNodesRef.current?.(reparented);
          }
          onMoveEndRef.current?.();
        }
        const next = selectOnRelease(selectionRef.current, ended.pressedId, ended.shift, ended.moved);
        if (next !== selectionRef.current) {
          setSelection(next);
          onSelectionChangeRef.current?.(next);
        }
        return;
      }
      heldOnNode.delete(e.pointerId);
      if (!pointers.delete(e.pointerId)) {
        return;
      }
      if (el.hasPointerCapture(e.pointerId)) {
        el.releasePointerCapture(e.pointerId);
      }
      pinch = pointers.size >= 2 ? pinchState() : undefined;
      if (pointers.size === 0) {
        setDragging(false);
      }
    };

    const onDoubleClick = (e: MouseEvent): void => {
      if (e.shiftKey) {
        return;
      }
      const target = e.target instanceof Element ? e.target.closest('.node[data-id], .edge[data-id]') : null;
      const id = target?.getAttribute('data-id') ?? clicked;
      if (id !== undefined) {
        e.preventDefault();
        const world = pointToWorld({ x: e.clientX, y: e.clientY });
        startEditing(id, compartmentAt(indexRef.current.placed.get(id), world), world);
      }
    };

    const onKeyDown = (e: KeyboardEvent): void => {
      el.removeAttribute('data-pointer-focus');
      if (e.key === 'Escape' && (press !== undefined || resize !== undefined || rotate !== undefined)) {
        // Cancel the move, resize or rotate: nothing of it stays, and the pointer-up that follows has nothing to finish.
        e.preventDefault();
        const pointerId = (press ?? resize ?? rotate)?.pointerId;
        press = undefined;
        resize = undefined;
        rotate = undefined;
        if (dragFrame.current !== undefined) {
          cancelAnimationFrame(dragFrame.current);
          dragFrame.current = undefined;
        }
        pendingDrag.current = undefined;
        if (pointerId !== undefined && el.hasPointerCapture(pointerId)) {
          el.releasePointerCapture(pointerId);
        }
        showGuides(NO_GUIDES);
        setDraggedNodes(NO_SELECTION);
        setDropHighlight(NO_SELECTION);
        setRotating(undefined);
        clicked = undefined;
        if (wroteFrames) {
          wroteFrames = false;
          onMoveCancelRef.current?.();
        }
        return;
      }
      if (e.key === 'Escape' && marqueeDrag !== undefined) {
        // Abandon the marquee; the pointer-up that follows then selects nothing.
        e.preventDefault();
        if (el.hasPointerCapture(marqueeDrag.pointerId)) {
          el.releasePointerCapture(marqueeDrag.pointerId);
        }
        marqueeDrag = undefined;
        setMarquee(undefined);
        return;
      }
      if (e.key === 'Escape' && reattach !== undefined) {
        // Abandon the re-attach; the edge keeps both ends where they were.
        e.preventDefault();
        if (el.hasPointerCapture(reattach.pointerId)) {
          el.releasePointerCapture(reattach.pointerId);
        }
        reattach = undefined;
        setConnector(undefined);
        return;
      }
      if (e.key === 'Escape' && segmentDrag !== undefined) {
        // Abandon the segment drag; what is already written stays (an in-flight pinch would have captured the ways
        // anyway), but no further frame is written and the gesture ends without an extra step.
        e.preventDefault();
        if (el.hasPointerCapture(segmentDrag.pointerId)) {
          el.releasePointerCapture(segmentDrag.pointerId);
        }
        segmentDrag = undefined;
        commitDragNow();
        onMoveEndRef.current?.();
        return;
      }
      if (e.key === 'Escape' && connect !== undefined) {
        // Abandon the connection in progress; the pointer-up that follows then has nothing to finish.
        e.preventDefault();
        if (el.hasPointerCapture(connect.pointerId)) {
          el.releasePointerCapture(connect.pointerId);
        }
        connect = undefined;
        setConnector(undefined);
        return;
      }
      if (e.key === ' ' && !e.metaKey && !e.ctrlKey && !e.altKey) {
        if (!spaceDown) {
          spaceDown = true;
          setSpaceHeld(true);
        }
        e.preventDefault();
        return;
      }
      // Commands: never from a text field (a label editor drawn inside the canvas).
      if (!isTextField(e.target)) {
        const clipboard = clipboardCommand(e);
        if (clipboard !== undefined && clipboard !== 'duplicate') {
          // Not prevented, so the browser fires its copy, cut or paste event (onClipboardEvent); the event is dispatched
          // before a task queued now runs, so this fallback only fires for a browser that sends none on a non-editable
          // element.
          if (!readOnly && !gestureActive.current() && onClipboardRef.current && (clipboard === 'paste' || selectionRef.current.size > 0)) {
            clearTimeout(clipboardFallback);
            clipboardFallback = setTimeout(() => {
              clipboardFallback = undefined;
              onClipboardRef.current?.(clipboard, selectionRef.current);
            }, 0);
          }
          return;
        }
        dispatchShortcut(actionsRef.current, e);
      }
    };
    const onClipboardEvent = (e: ClipboardEvent): void => {
      clearTimeout(clipboardFallback);
      clipboardFallback = undefined;
      const command = e.type === 'paste' ? 'paste' : e.type === 'cut' ? 'cut' : 'copy';
      if (el.ownerDocument.activeElement !== el || readOnly || isTextField(e.target) || gestureActive.current() || !onClipboardRef.current) {
        return;
      }
      if (command !== 'paste' && selectionRef.current.size === 0) {
        return;
      }
      const transfer = e.clipboardData ?? undefined;
      onClipboardRef.current(command, selectionRef.current, transfer);
      // A copy that carried nothing keeps the browser's default, so the system clipboard is left as it was.
      if (command === 'paste' || (transfer?.getData('text/plain') ?? '') !== '') {
        e.preventDefault();
      }
    };
    const onKeyUp = (e: KeyboardEvent): void => {
      if (e.key === ' ') {
        spaceDown = false;
        setSpaceHeld(false);
      }
    };
    const onPointerLeave = (e: PointerEvent): void => {
      if (connect === undefined && reattach === undefined && e.pointerType === 'mouse') {
        setConnector(undefined);
        setHandleHover(undefined);
      }
    };
    const onBlur = (): void => {
      el.removeAttribute('data-pointer-focus');
      spaceDown = false;
      setSpaceHeld(false);
    };

    el.addEventListener('wheel', onWheel, { passive: false });
    el.addEventListener('pointerdown', onPointerDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerEnd);
    el.addEventListener('pointercancel', onPointerEnd);
    el.addEventListener('dblclick', onDoubleClick);
    el.addEventListener('pointerleave', onPointerLeave);
    el.addEventListener('keydown', onKeyDown);
    el.addEventListener('keyup', onKeyUp);
    // On the document: with a caret or range left in page text, the browser fires the event there, not at the focus.
    const page = el.ownerDocument;
    page.addEventListener('copy', onClipboardEvent);
    page.addEventListener('cut', onClipboardEvent);
    page.addEventListener('paste', onClipboardEvent);
    el.addEventListener('blur', onBlur);
    return () => {
      el.removeEventListener('wheel', onWheel);
      el.removeEventListener('pointerdown', onPointerDown);
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerEnd);
      el.removeEventListener('pointercancel', onPointerEnd);
      el.removeEventListener('dblclick', onDoubleClick);
      el.removeEventListener('pointerleave', onPointerLeave);
      el.removeEventListener('keydown', onKeyDown);
      el.removeEventListener('keyup', onKeyUp);
      page.removeEventListener('copy', onClipboardEvent);
      page.removeEventListener('cut', onClipboardEvent);
      page.removeEventListener('paste', onClipboardEvent);
      clearTimeout(clipboardFallback);
      el.removeEventListener('blur', onBlur);
      if (dragFrame.current !== undefined) {
        cancelAnimationFrame(dragFrame.current);
      }
    };
  }, [update, zoomIn, zoomOut, reset, fit, focusFromPointer, startEditing, pointToWorld, readOnly]);

  // The edited element can vanish underneath the editor (another writer removed it); the edit then ends unsent.
  const editedNode = editing !== undefined && editable ? index.placed.get(editing) : undefined;
  const editedEdge = editing !== undefined && editable && index.edgePaths.has(editing) ? index.edgesById.get(editing) : undefined;
  useEffect(() => {
    if (editing !== undefined && editedNode === undefined && editedEdge === undefined) {
      setEditing(undefined);
      setEditingPart(undefined);
    }
  }, [editing, editedNode, editedEdge]);
  // The frame whose guard is being edited, and where its text is: the editor opens over the text and follows a re-flow.
  const editedFrame = editingGuard === undefined || !editable ? undefined : index.sequence?.fragments.find((fragment) => fragment.id === editingGuard.fragment);
  const editedGuardAt = useMemo(() => {
    const section = editedFrame?.sections.find((candidate) => candidate.operand === editingGuard?.operand) ?? editedFrame?.sections[0];
    return section === undefined ? undefined : { x: section.guardAt.x + Math.max(GUARD_EDITOR_MIN_WIDTH, estimateTextWidth(section.guard, labelFont('sans-serif', SEQUENCE_METRICS.fragmentFontSize))) / 2, y: section.guardAt.y };
  }, [editedFrame, editingGuard]);
  const guardEditorStyle = useMemo(() => (editedFrame === undefined ? undefined : edgeLabelStyle(effectiveTheme(theme, doc))), [editedFrame, theme, doc]);
  useEffect(() => {
    if (editingGuard !== undefined && editedFrame === undefined) {
      setEditingGuard(undefined);
    }
  }, [editingGuard, editedFrame]);
  // The midpoint of the route the edge is drawn along now, router output included, so the field moves when it does.
  const editedEdgeAt = useMemo(() => {
    if (editedEdge === undefined || !index.edgePaths.has(editedEdge.id)) {
      return undefined;
    }
    // A sequence message's label sits above its line, where the scene draws it, not on the line.
    const message = index.sequence?.messages.find((candidate) => candidate.id === editedEdge.id);
    if (message !== undefined) {
      return messageLabelAnchor(message);
    }
    const routed = routes.get(editedEdge.id)?.points;
    const points = routed !== undefined && routed.length > 0 ? routed : index.edgePaths.get(editedEdge.id)?.points;
    return points === undefined ? undefined : edgeLabelAt(points, editedEdge.routing?.mode === 'curved');
  }, [editedEdge, index, routes]);
  const editedEdgeStyle = useMemo(() => (editedEdge === undefined ? undefined : edgeLabelStyle(effectiveTheme(theme, doc))), [editedEdge, theme, doc]);
  const editedStyle = useMemo(
    () => (editedNode === undefined ? undefined : resolveRenderedNodeStyle(theme, doc, index, editedNode)),
    [theme, doc, index, editedNode],
  );
  const editedIsContainer = editedNode !== undefined && isContainer(index, editedNode);

  // Connection handles show while the select tool is idle: never mid-drag, mid-pan, mid-marquee or over a label editor.
  const idleForHandles = selectHandles && connector === undefined && !dragging && draggedNodes.size === 0 && marquee === undefined && guides.length === 0 && editing === undefined && editingGuard === undefined;
  const handleHighlights = useMemo(
    () =>
      idleForHandles
        ? handleNodes(index, handleHover?.node, selection).map((node) => ({
            node: node.id,
            port: handleHover?.active?.node === node.id ? handleHover.active.port : undefined,
            rotateHandle: node.id === rotateHandleNode(selection),
          }))
        : [],
    [idleForHandles, index, handleHover, selection],
  );
  const overlay = readOnly ? undefined : handleHighlights.length > 0 ? { handles: handleHighlights } : (connecting || connector?.pointer !== undefined ? connector : undefined);

  const classes = ['diagram-canvas', dragging ? 'is-dragging' : spaceHeld ? 'is-pannable' : connecting ? 'is-connecting' : '', className ?? ''].filter(Boolean).join(' ');

  return (
    <>
      <div ref={element} className={classes} tabIndex={0} role="img" aria-label={`Diagram: ${doc.title}`} data-diagram-theme={theme.name}>
        {size.width > 0 && size.height > 0 && (
          <DiagramSvg
            document={doc}
            theme={theme}
            viewport={viewport}
            size={size}
            index={index}
            measure={measure}
            breakLines={breakLines}
            selection={readOnly ? undefined : selection}
            rotating={readOnly ? undefined : rotating}
            draggedNodes={draggedNodes}
            dropTargets={dropHighlight}
            compareMarks={compareMarks}
            editingLabel={editedNode?.id ?? editedEdge?.id}
            editingPart={editingPart}
            editingGuard={editingGuard}
            routes={routes}
            connector={overlay}
            edgeEndpoints={edgeEnds}
            guides={guides.length > 0 ? guides : undefined}
            marquee={marquee}
            showGrid={gridOn}
            // Read per render: every pan and zoom renders, so a window dragged to a screen of another density catches up.
            pixelRatio={window.devicePixelRatio}
          />
        )}
        {editedNode !== undefined && editedStyle !== undefined && editingPart !== undefined && (
          <MembersEditor key={`${editedNode.id}:${editingPart}`} node={editedNode} part={editingPart} style={editedStyle} viewport={viewport} onCommit={commitMembers} onCancel={endEditing} />
        )}
        {((editedNode !== undefined && editedStyle !== undefined && editingPart === undefined) || (editedEdge !== undefined && editedEdgeAt !== undefined && editedEdgeStyle !== undefined) || (editedFrame !== undefined && editedGuardAt !== undefined && guardEditorStyle !== undefined)) && (
          <LabelEditHint singleLine={editedNode === undefined} />
        )}
        {editedNode !== undefined && editedStyle !== undefined && editingPart === undefined && (
          <LabelEditor key={editedNode.id} node={editedNode} style={editedStyle} container={editedIsContainer} viewport={viewport} onCommit={commitLabel} onCancel={endEditing} />
        )}
        {editedFrame !== undefined && editedGuardAt !== undefined && guardEditorStyle !== undefined && (
          <EdgeLabelEditor
            key={`${editedFrame.id}:${String(editingGuard?.operand)}`}
            edgeId={editedFrame.id}
            label={editedFrame.sections.find((section) => section.operand === editingGuard?.operand)?.guard ?? ''}
            at={editedGuardAt}
            style={guardEditorStyle}
            viewport={viewport}
            ariaLabel="Fragment guard"
            onCommit={commitGuard}
            onCancel={endEditing}
          />
        )}
        {editedEdge !== undefined && editedEdgeAt !== undefined && editedEdgeStyle !== undefined && (
          <EdgeLabelEditor
            key={editedEdge.id}
            edgeId={editedEdge.id}
            label={editedEdge.label ?? ''}
            at={editedEdgeAt}
            style={editedEdgeStyle}
            viewport={viewport}
            onCommit={commitLabel}
            onCancel={endEditing}
          />
        )}
      </div>
      {/* Outside the canvas: its role="img" makes everything inside it presentational, a live region included. */}
      <span className="visually-hidden" role="status">
        {announced}
      </span>
    </>
  );
});
