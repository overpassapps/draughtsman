/**
 * The editor's document state: a Y.Doc held in the tab, its JSON projection as React state, an origin-scoped undo
 * manager, and a save that keeps using the guarded PUT. Every local edit mutates the Y.Doc; the JSON the canvas
 * renders and the server stores are both derived from it, so the picture and the wire format never disagree.
 *
 * A refused save (412: the document moved on; 428: no base version) changes nothing locally: the Y.Doc, the undo history
 * and the base version all stay as they were. The status becomes `conflict`, autosave stops, the user's version is kept
 * as a local copy (editor/localCopy.ts) and the viewer asks what to do: `reloadTheirs`, `keepMine` (overwrite, quoting
 * the server's current version) or `saveMineAsNew` (design/atlas/editor-safety.md, "Autosave"). Live sync does not merge
 * while that question is open, so "mine" and "theirs" stay what the user was shown.
 *
 * Autosave (`autosaveDelayMs`): a local change schedules a save that many milliseconds after the last change (at most
 * AUTOSAVE_MAX_WAIT_MS after the first unsaved one, so continuous editing still saves), and a tab going hidden or being
 * closed flushes at once. Saves are serial and each sends the document as it is when it starts, so an older snapshot
 * never lands over a newer one; an edit made while one is in flight leaves the status unsaved and is the next save's.
 * A failed save that can succeed later (server down, offline, 5xx, signed out) retries with a growing delay
 * (editor/saveFailure.ts) and the status stays not-saved until it does.
 *
 * Live sync (checklist item 30): with `liveSyncIntervalMs` set, the hook polls the stored version with a conditional GET
 * (documentClient.ts `fetchDocumentIfChanged`) and merges another writer's change into the Y.Doc under REMOTE_ORIGIN
 * (model/remoteMerge.ts), so it shows without a reload, Cmd/Ctrl+Z never undoes it, and unsaved local edits stay. The
 * merged-in version becomes the base the next save quotes. Where both sides changed the same thing the local edit is
 * kept on the canvas and the clash is reported in `remote.conflicts` until the user keeps theirs or takes the other.
 * Polling pauses while a save is in flight, and a poll that returns after a save started is dropped, so the tab never
 * merges its own save back into itself. It does not pause for a hidden tab: Chrome on macOS reports a merely occluded
 * window as hidden, which would stall a canvas the user can see, and the browser already throttles background timers.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as Y from 'yjs';
import type { AlignCommand, DistributeCommand } from '../canvas/align.ts';
import { alignSelection, distributeSelection } from '../canvas/alignSelection.ts';
import { indexDocument } from '../canvas/documentIndex.ts';
import { arrange as arrangeDocument, type ArrangeCommand } from '../model/arrange.ts';
import { connectionsForSelection } from './connectSelection.ts';
import { connect as connectInDoc, reconnect as reconnectInDoc, type Connection, type Reconnection } from '../model/connect.ts';
import { copy, paste, type ClipboardPayload } from '../model/clipboard.ts';
import { applyDocumentDiff } from '../model/documentDiff.ts';
import { mergeTextEdit, takeMine, type TextMergeResult } from '../model/textMerge.ts';
import { documentsEqual } from '../model/documentEquality.ts';
import { SaveConflict, createDocumentFrom, fetchDocumentIfChanged, putDocument, renameDocument } from '../model/documentClient.ts';
import { restoreVersion as restoreVersionRequest } from '../model/historyClient.ts';
import { parseDocument } from '../model/parse.ts';
import { requestLayout, type LayoutAlgorithm } from '../model/layoutClient.ts';
import {
  addElements,
  addNodes,
  applyGroup,
  applyUngroup,
  clearEdgeWaypoints,
  createUndoManager,
  derivesGeometry,
  documentToYDoc,
  endUndoStep,
  LOCAL_ORIGIN,
  readFlythrough,
  removeElements,
  renameColumnPorts,
  setEdgeKind as setEdgeKindInDoc,
  setEdgeProps,
  setEdgeLabel,
  setEdgeEnds as setEdgeEndsInDoc,
  setEdgeRoutingMode as setEdgeRoutingModeInDoc,
  setEdgeWaypoints,
  setElementOrder,
  setFlythrough as setFlythroughInDoc,
  setNodeGeometries,
  setNodeLabel,
  setNodeParents,
  setNodeStyles,
  setEdgeStyles,
  setNodeProps as setNodePropsInDoc,
  setTitle as setTitleInDoc,
  REMOTE_ORIGIN,
  yDocToDocument,
} from '../model/editor.ts';
import { createMessage, insertEdgeAt, reorderedMessages, reorderedParticipants } from '../model/sequenceEdit.ts';
import { addNote as addNoteEdit, emptiedFragments, emptiedOperands, addOperand, applySequenceEdit, membershipAt, setGuard, setOperator, toggleActivation as toggleActivationEdit, withMembership, wrapInFragment as wrapInFragmentEdit } from '../model/sequenceFragments.ts';
import { sequenceScene } from '../model/sequenceScene.ts';
import { fitContainers, group as groupPlan, ungroup as ungroupPlan } from '../model/grouping.ts';
import { mergeRemote, takeRemote, type MergeConflict } from '../model/remoteMerge.ts';
import { columnRenames } from '../model/columnPorts.ts';
import { fitEntity } from '../model/erShapes.ts';
import { memberPartsOf, type MemberPart } from '../model/memberParts.ts';
import { fitClass, isClassShape, memberLines, umlShape } from '../model/umlShapes.ts';
import { addLanePlan, isLane, layoutLanes, moveLanePlan, relayoutAfterMoves, type SwimlaneOrientation } from '../model/swimlanes.ts';
import type { ArrowHead, Document, EdgeStyle, Flythrough, Geometry, Id, Node, NodeStyle, Point, Props, RoutingMode } from '../model/types.ts';
import { dispatchShortcut, type EditorAction } from './actions.ts';
import { onSessionEnded } from '../model/sessionEnded.ts';
import { answeredCopyExpired, clearLocalCopy, markLocalCopyAnswered, newCopyId, readLocalCopies, storeLocalCopy, type LocalCopy } from './localCopy.ts';
import { classifySaveFailure, retryDelay } from './saveFailure.ts';
import { historyCommand, isTextField } from './shortcuts.ts';

/** `conflict`: the server refused the save as stale and the user has not yet decided what to do about it. */
export type SaveStatus = 'saved' | 'unsaved' | 'saving' | 'error' | 'conflict';

/** One editable field of a node's sparse style; what the styling panel changes. */
export type NodeStyleField = keyof NodeStyle;

/** One editable field of an edge's sparse style (arrowheads excluded; owned by another lane). */
export type EdgeStyleField = Exclude<keyof EdgeStyle, 'arrowStart' | 'arrowEnd'>;

/** How often an open document checks for another writer's change: well inside the two seconds item 30 allows. */
export const LIVE_SYNC_INTERVAL_MS = 1000;

/** How long after the last change autosave waits before saving. */
export const AUTOSAVE_DELAY_MS = 2000;

/** The longest a document stays unsaved under autosave however steadily it is edited. */
export const AUTOSAVE_MAX_WAIT_MS = 10_000;

/** What the last merge of another writer's change did, for the viewer to announce. */
export interface RemoteChange {
  /** Who wrote it, when the change carries AI provenance naming the agent; otherwise undefined. */
  by: string | undefined;
  /** Clashes with unsaved local edits still awaiting a decision; empty when the change merged cleanly. */
  conflicts: readonly MergeConflict[];
}

export interface DocumentEditorOptions {
  /** Poll the stored version this often and merge another writer's change; off when undefined (previews, tests). */
  liveSyncIntervalMs?: number;
  /** Save this long after the last change, and on leaving; off when undefined (previews, tests, the Try page). */
  autosaveDelayMs?: number;
}

/** What the stale-save question is doing: a recovery action is running, or the last one failed and why. */
export interface StaleState {
  busy: boolean;
  error: string | undefined;
}

export interface DocumentEditor {
  /** The document derived from the Y.Doc; a new object on every change. */
  document: Document;
  /**
   * Writes absolute node geometry for one frame of a move, resize or nudge, with the new waypoints of any edge moving
   * with the nodes; every write until moveEnd is one undo step.
   */
  moveNodes: (moves: ReadonlyMap<Id, Geometry>, waypoints?: ReadonlyMap<Id, Point[]>) => void;
  /** Sets or clears parents as part of the gesture under way (a drop into or out of a container); moveEnd closes it. */
  reparentNodes: (parents: ReadonlyMap<Id, Id | null>) => void;
  /** Fits each selected container around its children with `padding` on every side, as one undo step. */
  fitToContents: (ids: ReadonlySet<Id>, padding: number) => void;
  /** Adds a lane as the far lane of `poolId` and repacks the pool's lanes, as one undo step; returns the new lane's id. */
  addLane: (poolId: Id) => Id | undefined;
  /** Deletes a lane, its contents and its edges, and repacks the pool's remaining lanes, as one undo step. */
  removeLane: (laneId: Id) => void;
  /** Moves a lane one place earlier (-1) or later (+1) among its pool's lanes and repacks them, as one undo step. */
  moveLane: (laneId: Id, direction: -1 | 1) => void;
  /** Sets a pool's lane arrangement (rows or columns) and repacks its lanes, as one undo step. */
  setPoolOrientation: (poolId: Id, orientation: SwimlaneOrientation) => void;
  /** Groups the selection into a new container as one undo step; returns the container's id, or undefined for no change. */
  group: (ids: ReadonlySet<Id>, padding: number) => Id | undefined;
  /**
   * Ungroups the selected containers without edges of their own as one undo step, writing nothing when there are none;
   * returns the released children and the containers refused for their edges, or undefined when no container is selected.
   */
  ungroup: (ids: ReadonlySet<Id>) => { released: ReadonlySet<Id>; refused: Id[] } | undefined;
  /**
   * Sequence diagrams: inserts a `sync` message from one participant to another (the same one for a self message) at
   * `index` of the edge order, renumbering only the edges after it, as one undo step; returns its id.
   */
  insertMessage: (from: Id, to: Id, index: number) => Id;
  /**
   * Sequence diagrams: moves a message to the slot world `y` picks, or a participant to the one world `x` picks
   * (model/sequenceEdit.ts), as one undo step; a drop where it already is writes nothing and records no step.
   */
  reorderElement: (id: Id, at: { x: number; y: number }) => void;
  /**
   * Sequence diagrams (design/atlas/sequence-fragments.md): each of these is one undo step, and says `undefined` (writing
   * nothing, recording no step) when it does not apply. Wrap the selected messages in a new fragment; returns the fragment
   * and the operand whose guard to edit.
   */
  wrapInFragment: (ids: ReadonlySet<Id>, operator: string) => { fragment: Id; operand: number } | undefined;
  /** Splits the fragment a message is in at it, into a new operand; returns the fragment and the new operand. */
  addFragmentOperand: (messageId: Id) => { fragment: Id; operand: number } | undefined;
  /** Adds an empty note row after a message (at the end when none), returning its id. */
  addNote: (afterId: Id | undefined, kind?: 'note' | 'interaction-use') => Id | undefined;
  /** Turns the activation bar a message opens on or off; returns whether it is now on. */
  toggleActivation: (messageId: Id) => boolean | undefined;
  /** Sets one operand's guard text of a fragment. */
  setFragmentGuard: (fragment: Id, operand: number, text: string) => void;
  /** Sets a fragment's operator (its tab text). */
  setFragmentOperator: (fragment: Id, operator: string) => void;
  /** Closes the gesture's undo step. */
  moveEnd: () => void;
  /**
   * Abandons the gesture under way after Escape: rolls back what its frames wrote and leaves neither an undo nor a redo
   * entry. Only for a gesture that has written a frame; anything else has nothing of its own to roll back.
   */
  moveCancel: () => void;
  /** Adds a node as one undo step. The caller supplies an id the document does not use (model/create.ts). */
  addNode: (node: Node) => void;
  /** Adds several nodes (a pool and its lanes, parent first) as one undo step. */
  addNodeGroup: (nodes: readonly Node[]) => void;
  /** Sequence diagrams: adds a participant and puts it among the lifelines where world `x` falls, as one undo step (a palette drag). */
  addParticipant: (node: Node, x: number) => void;
  /**
   * Deletes the selection, the contents of any selected container and every edge attached to a removed node, as one
   * undo step.
   */
  deleteElements: (ids: ReadonlySet<Id>) => void;
  /**
   * Pastes a copied selection moved by `offset` (clipboard.ts: fresh ids, internal edges kept, relative layout kept) as
   * one undo step, and returns the pasted node and edge ids. An empty payload writes nothing.
   */
  pasteElements: (payload: ClipboardPayload, offset: Point) => ReadonlySet<Id>;
  /** Duplicates the selection moved by `offset` as one undo step: a copy and a paste that bypass the clipboard. */
  duplicateElements: (ids: ReadonlySet<Id>, offset: Point) => ReadonlySet<Id>;
  /**
   * Commits an edited node or edge label as exactly one undo step; an unchanged label writes nothing and records no
   * step. An empty edge label removes the edge's label.
   */
  setLabel: (id: Id, label: string) => void;
  /**
   * Replaces one member compartment (`attributes` or `operations` of a UML class or interface, `columns` of an ER
   * entity) with `members`, one string per member, and refits the box to its content (model/umlShapes.ts `fitClass`,
   * model/erShapes.ts `fitEntity`), as exactly one undo step. An empty list removes the prop; an unchanged list, or a
   * node with no such compartment, writes nothing.
   */
  setClassMembers: (id: Id, part: MemberPart, members: readonly string[]) => void;
  /** Switches an edge's kind as exactly one undo step, leaving its style alone; an unchanged kind writes nothing. */
  setEdgeKind: (id: Id, kind: string) => void;
  /**
   * Sets an edge's arrowhead ends as exactly one undo step. `undefined` for either end removes that override so the
   * edge falls back to its kind default; a chosen value is stored explicitly. An unchanged pair writes nothing.
   */
  setEdgeEnds: (id: Id, arrowStart: ArrowHead | undefined, arrowEnd: ArrowHead | undefined) => void;
  /**
   * Sets or clears one node style property on every named node as one undo step; an undefined value clears the
   * override so the field inherits the diagram theme again. An unchanged value writes nothing.
   */
  setNodeStyle: (ids: ReadonlySet<Id>, field: NodeStyleField, value: NodeStyle[NodeStyleField] | undefined) => void;
  /**
   * The edge counterpart, restricted to stroke, strokeWidth and dash (arrowheads are owned by another lane).
   */
  setEdgeStyle: (ids: ReadonlySet<Id>, field: EdgeStyleField, value: EdgeStyle[EdgeStyleField] | undefined) => void;
  /**
   * Replaces one node's free-form `props` (or clears them with null) as exactly one undo step; unchanged props write
   * nothing. Used to point an image node at a freshly uploaded asset.
   */
  setNodeProps: (id: Id, props: Props | null) => void;
  /** Switches an edge's routing mode as exactly one undo step, keeping any pinned waypoints; an unchanged mode writes nothing. */
  setEdgeRoutingMode: (id: Id, mode: RoutingMode) => void;
  /** Clears an edge's pinned waypoints so it returns to derived routing, as exactly one undo step; no waypoints writes nothing. */
  resetRoute: (id: Id) => void;
  /**
   * Edits the camera path Present plays (model/flythrough.ts) as exactly one undo step: `edit` is given the path as it
   * is at this moment (undefined when there is none) and answers the new one, or undefined to remove it. Answers
   * whether anything changed; an edit that changed nothing records no step.
   */
  editFlythrough: (edit: (current: Flythrough | undefined) => Flythrough | undefined) => boolean;
  /** Adds an orthogonal edge between two ports as one undo step and returns its id. */
  connect: (connection: Connection) => Id;
  /** Joins the selected shapes in selection order (editor/connectSelection.ts) as one undo step; resolves the new edge ids. */
  connectSelected: (selection: ReadonlySet<Id>) => Id[];
  /** Moves one end of an edge onto another port as one undo step; re-attaching an end where it already is writes nothing. */
  reconnect: (reconnection: Reconnection) => void;
  /** Aligns the selected nodes (canvas/alignSelection.ts) as one undo step; nothing to move records no step. */
  align: (ids: ReadonlySet<Id>, command: AlignCommand) => void;
  /** Distributes the selected nodes as one undo step; nothing to move records no step. */
  distribute: (ids: ReadonlySet<Id>, command: DistributeCommand) => void;
  /** Changes the render order of the selection (model/arrange.ts) as one undo step; no change records no step. */
  arrange: (ids: ReadonlySet<Id>, command: ArrangeCommand) => void;
  /**
   * Lays the whole document out on the server (model/layoutClient.ts) and applies the result as one undo step,
   * returning the layout engine's warnings. Rejects (with the server's message) when the layout service is
   * unavailable; the document is untouched in that case.
   */
  tidy: (algorithm: LayoutAlgorithm) => Promise<string[]>;
  applyTextDocument: (next: Document, base: Document) => void;
  /**
   * Applies text that was edited from `base` onto the diagram as it is now, when the diagram has moved on since
   * (model/textMerge.ts): one undo step, the text's changes in, the diagram's own changes kept, and what both changed
   * returned as conflicts (the diagram's side stands). Throws, writing nothing, for text that is not a valid document.
   */
  mergeTextDocument: (next: Document, base: Document) => TextMergeResult;
  /** Takes the text's side of conflicts `mergeTextDocument` returned, as one more undo step. */
  takeTextConflicts: (next: Document, conflicts: readonly MergeConflict[]) => void;
  undo: () => void;
  redo: () => void;
  /** Undo and redo as registry actions (actions.ts): what Cmd/Ctrl+Z and Cmd/Ctrl+Shift+Z run, and what the palette lists. */
  actions: readonly EditorAction[];
  /**
   * Saves through the ETag-guarded PUT. Resolves true only when the whole current document is stored: false for a
   * refused or failed save (`saveStatus`/`saveError` say why), for one already in flight, and for an edit that landed
   * while it was in flight.
   */
  save: () => Promise<boolean>;
  /**
   * Makes sure the whole current document is stored: waits for a save in flight, then saves what is still unsaved.
   * Resolves true only when it is. What the leave guard's "Save and leave" runs, since it may be asked while autosave
   * is mid-request, when `save` would refuse.
   */
  flush: () => Promise<boolean>;
  /**
   * Renames the document through its own guarded PATCH; throws `SaveConflict` on a stale base version, same as
   * `save`. Neither an undo step nor a change to `saveStatus`.
   */
  renameTitle: (title: string) => Promise<void>;
  saveStatus: SaveStatus;
  saveError: string | undefined;
  /** True while a failed save will be tried again by itself. */
  retrying: boolean;
  /** While `saveStatus` is `conflict`: whether a recovery action is running and why the last one failed; otherwise undefined. */
  stale: StaleState | undefined;
  /** Replaces the editor's content with the server's current version. The user's version stays as the local copy. */
  reloadTheirs: () => Promise<void>;
  /**
   * Puts the document back to a stored version (History panel): stores what is unsaved first (so nothing on the canvas is
   * lost and the restore quotes the version the server really holds), asks the server to restore, which checkpoints the
   * document as it is, and adopts the restored one as the saved document. Rejects with a message to show - a
   * `SaveConflict` when the document moved on meanwhile - and changes nothing in that case. Not a local undo step: like any
   * server-side change it is undone from the history list (the checkpoint it took), not with Cmd/Ctrl+Z.
   */
  restoreVersion: (versionId: string) => Promise<void>;
  /** Overwrites the server's current version with the editor's content, quoting that version. The caller confirms first. */
  keepMine: () => Promise<void>;
  /** Stores the editor's content as a new document and loads the server's version here; resolves the new document's id. */
  saveMineAsNew: () => Promise<string | undefined>;
  /**
   * Versions of the user's document kept aside because the server did not take them, oldest first, until each is saved as a
   * new document or discarded. A copy from an earlier visit shows here on load.
   */
  localCopies: readonly LocalCopy[];
  /** Stores one local copy as a new document and drops it; resolves the new document's id. */
  saveCopyAsNew: (copyId: string) => Promise<string>;
  discardLocalCopy: (copyId: string) => void;
  /** The last change merged in from another writer, until the next local edit; undefined when there is none. */
  remote: RemoteChange | undefined;
  /** Leaves the local side of every conflict in place and clears the report. */
  keepLocal: () => void;
  /** Takes the other writer's side of every conflict, as one undo step. */
  takeTheirs: () => void;
}

export const CONFLICT_MESSAGE = 'This document was changed somewhere else since you opened it. Nothing was saved; your edits are still here.';

/** Builds the editor state for one loaded document and wires Cmd/Ctrl+Z (undo) and Cmd/Ctrl+Shift+Z (redo). */
export function useDocumentEditor(id: string | undefined, initial: Document, etag: string | undefined, options: DocumentEditorOptions = {}): DocumentEditor {
  const { liveSyncIntervalMs, autosaveDelayMs } = options;
  // A new initial document (the Try page importing another one) starts a new Y.Doc and a new undo history.
  const yDoc = useMemo(() => documentToYDoc(initial), [initial]);
  // Never destroyed from an effect cleanup: StrictMode runs that cleanup on a live component and undo would silently
  // stop recording. It only listens to its own Y.Doc, so it is collected along with it.
  const undoManager = useMemo(() => createUndoManager(yDoc), [yDoc]);

  const [revision, setRevision] = useState(0);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- revision is the signal that the Y.Doc changed
  const document = useMemo(() => yDocToDocument(yDoc), [yDoc, revision]);
  const [saveStatus, setSaveStatus] = useState<SaveStatus>('saved');
  const [saveError, setSaveError] = useState<string | undefined>(undefined);
  // The status as decisions read it: state lags a render behind, and a save, a poll or a flush must not act on a stale one.
  const statusRef = useRef<SaveStatus>('saved');
  const setStatus = useCallback((next: SaveStatus | ((status: SaveStatus) => SaveStatus)) => {
    const value = typeof next === 'function' ? next(statusRef.current) : next;
    statusRef.current = value;
    setSaveStatus(value);
  }, []);
  const inConflict = useCallback((): boolean => statusRef.current === 'conflict', []);
  // Bumped by every local change, so autosave's debounce restarts on each one.
  const [changeTick, setChangeTick] = useState(0);

  // The base version the next save is composed from: the loaded ETag, then whatever each successful save or merge
  // returns. `baseDocument` is that version's content, the common ancestor a merge compares both sides against.
  const baseVersion = useRef(etag);
  const baseDocument = useRef(initial);
  useEffect(() => {
    baseVersion.current = etag;
    baseDocument.current = initial;
  }, [yDoc, etag, initial]);
  const [remote, setRemote] = useState<(RemoteChange & { theirs: Document }) | undefined>(undefined);

  // Counts local Y.Doc changes, so a save can tell whether the document moved on while it was in flight. A merged
  // remote change is not one: it is already saved, so it leaves the save status as it was.
  const changes = useRef(0);
  useEffect(() => {
    const onUpdate = (_update: Uint8Array, origin: unknown): void => {
      setRevision((r) => r + 1);
      if (origin === REMOTE_ORIGIN) {
        return;
      }
      changes.current += 1;
      setChangeTick((tick) => tick + 1);
      // A save in flight settles its own status; a stale-save question stays open until it is answered.
      setStatus((status) => (status === 'saving' || status === 'conflict' ? status : 'unsaved'));
      // A local edit ends the announcement of a clean merge; open conflicts stay until the user decides.
      setRemote((previous) => (previous === undefined || previous.conflicts.length > 0 ? previous : undefined));
    };
    yDoc.on('update', onUpdate);
    return () => {
      yDoc.off('update', onUpdate);
    };
  }, [yDoc, setStatus]);

  // "Unsaved" is a comparison, not a latch: an edit marks it at once (above), and once the document is the saved base
  // again - a drag that ended where it began, or undo back to the last save - it is saved again. What is compared is
  // the whole model (`documentsEqual`: nodes, edges, title, theme, meta) against the base version's content passed through the
  // same Yjs round trip the live document takes, so a representational difference between stored JSON and a
  // materialised Y.Doc can never leave it unequal forever.
  const normalisedBase = useRef<{ source: Document; normal: Document } | undefined>(undefined);
  useEffect(() => {
    const base = baseDocument.current;
    if (normalisedBase.current?.source !== base) {
      normalisedBase.current = { source: base, normal: yDocToDocument(documentToYDoc(base)) };
    }
    const normal = normalisedBase.current.normal;
    setStatus((status) => (status === 'unsaved' && documentsEqual(document, normal) ? 'saved' : status));
  }, [document, setStatus]);

  const moveNodes = useCallback(
    (moves: ReadonlyMap<Id, Geometry>, waypoints?: ReadonlyMap<Id, Point[]>) => {
      // A derived layout has no stored position to write; refused here too, since even an empty transaction is an undo step.
      if (derivesGeometry(yDoc)) {
        return;
      }
      // A pool being resized repacks its lanes into the new box (model/swimlanes.ts); the lanes travel with a moved
      // pool already, so only the ones not in this frame's moves are written.
      const laneUpdates = relayoutAfterMoves(yDocToDocument(yDoc), moves);
      yDoc.transact(() => {
        setNodeGeometries(yDoc, moves);
        if (laneUpdates.size > 0) {
          setNodeGeometries(yDoc, laneUpdates);
        }
        if (waypoints !== undefined) {
          setEdgeWaypoints(yDoc, waypoints);
        }
      }, LOCAL_ORIGIN);
    },
    [yDoc],
  );
  // Part of the drag it ends, so no step is closed here: the gesture's moveEnd closes the move and the drop together.
  const reparentNodes = useCallback(
    (parents: ReadonlyMap<Id, Id | null>) => {
      setNodeParents(yDoc, parents);
    },
    [yDoc],
  );
  const moveEnd = useCallback(() => {
    endUndoStep(undoManager);
  }, [undoManager]);
  // Escape mid-gesture: the frames written since the last step closed are that gesture's alone, so undoing that one
  // open step restores the model exactly. The undo pushed it onto the redo stack, and it must not stay there for
  // Cmd/Ctrl+Shift+Z to bring the cancelled gesture back; the redo stack was empty anyway, since the gesture's first
  // write cleared it.
  const moveCancel = useCallback(() => {
    undoManager.undo();
    undoManager.clear(false, true);
  }, [undoManager]);
  // A discrete command is its own undo step whatever came before it: close any open step, edit, close again.
  const addNode = useCallback(
    (node: Node) => {
      endUndoStep(undoManager);
      addNodes(yDoc, [node]);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const addNodeGroup = useCallback(
    (nodes: readonly Node[]) => {
      endUndoStep(undoManager);
      addNodes(yDoc, nodes);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const addParticipant = useCallback(
    (node: Node, x: number) => {
      endUndoStep(undoManager);
      addNodes(yDoc, [node]);
      // Added at the right-hand end, then moved to the lifeline slot `x` falls in, in the same step.
      const current = yDocToDocument(yDoc);
      const nodes = reorderedParticipants(current, sequenceScene(current), node.id, x);
      if (nodes !== undefined) {
        setElementOrder(yDoc, { nodes, edges: current.edges });
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const insertMessage = useCallback(
    (from: Id, to: Id, index: number): Id => {
      const current = yDocToDocument(yDoc);
      // A message dropped between two members of a fragment is a member too (design/atlas/sequence-fragments.md).
      const message = withMembership(createMessage(current, from, to), membershipAt(current, index));
      endUndoStep(undoManager);
      insertEdgeAt(yDoc, message, index);
      endUndoStep(undoManager);
      return message.id;
    },
    [yDoc, undoManager],
  );
  const reorderElement = useCallback(
    (elementId: Id, at: { x: number; y: number }) => {
      // The scene is derived from the live Y.Doc, so the slot is read against what another writer just changed too.
      const current = yDocToDocument(yDoc);
      const scene = sequenceScene(current);
      const nodes = reorderedParticipants(current, scene, elementId, at.x);
      const edges = reorderedMessages(current, scene, elementId, at.y);
      if (nodes === undefined && edges === undefined) {
        return;
      }
      endUndoStep(undoManager);
      setElementOrder(yDoc, { nodes: nodes ?? current.nodes, edges: edges ?? current.edges });
      if (edges !== undefined) {
        inheritMembership(yDoc, new Set([elementId]));
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const wrapInFragment = useCallback(
    (ids: ReadonlySet<Id>, operator: string) => {
      const wrapped = wrapInFragmentEdit(yDocToDocument(yDoc), ids, operator);
      if (wrapped !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, wrapped.edit);
        endUndoStep(undoManager);
      }
      return wrapped === undefined ? undefined : { fragment: wrapped.fragment, operand: wrapped.operand };
    },
    [yDoc, undoManager],
  );
  const addFragmentOperand = useCallback(
    (messageId: Id) => {
      const added = addOperand(yDocToDocument(yDoc), messageId);
      if (added !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, added.edit);
        endUndoStep(undoManager);
      }
      return added === undefined ? undefined : { fragment: added.fragment, operand: added.operand };
    },
    [yDoc, undoManager],
  );
  const addNote = useCallback(
    (afterId: Id | undefined, kind?: 'note' | 'interaction-use') => {
      const added = addNoteEdit(yDocToDocument(yDoc), afterId, undefined, kind);
      if (added !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, added.edit);
        endUndoStep(undoManager);
      }
      return added?.note;
    },
    [yDoc, undoManager],
  );
  const toggleActivation = useCallback(
    (messageId: Id) => {
      const toggled = toggleActivationEdit(yDocToDocument(yDoc), messageId);
      if (toggled !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, toggled.edit);
        endUndoStep(undoManager);
      }
      return toggled?.active;
    },
    [yDoc, undoManager],
  );
  const setFragmentGuard = useCallback(
    (fragment: Id, operand: number, text: string) => {
      const edit = setGuard(yDocToDocument(yDoc), fragment, operand, text);
      if (edit !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, edit);
        endUndoStep(undoManager);
      }
    },
    [yDoc, undoManager],
  );
  const setFragmentOperator = useCallback(
    (fragment: Id, operator: string) => {
      const edit = setOperator(yDocToDocument(yDoc), fragment, operator);
      if (edit !== undefined) {
        endUndoStep(undoManager);
        applySequenceEdit(yDoc, edit);
        endUndoStep(undoManager);
      }
    },
    [yDoc, undoManager],
  );
  const deleteElements = useCallback(
    (ids: ReadonlySet<Id>) => {
      // A deleted lane leaves its pool's remaining lanes to repack; note the pools before the removal.
      const pools = new Set<Id>();
      for (const node of yDocToDocument(yDoc).nodes) {
        if (ids.has(node.id) && isLane(node) && node.parent !== undefined) {
          pools.add(node.parent);
        }
      }
      const before = yDocToDocument(yDoc);
      endUndoStep(undoManager);
      removeElements(yDoc, ids);
      if (before.type === 'sequence') {
        // A frame whose last message just left (a deleted participant's messages included) goes in the same step, or it
        // would stay behind drawn empty; one undo restores the frame and its messages together.
        const emptied = emptiedFragments(before, yDocToDocument(yDoc));
        if (emptied.size > 0) {
          removeElements(yDoc, emptied);
        }
        // An operand of a surviving frame that lost its last row goes too, the later ones renumbered (else it would sit in
        // the data with nothing drawn under its guard).
        const operands = emptiedOperands(before, yDocToDocument(yDoc));
        if (operands !== undefined) {
          applySequenceEdit(yDoc, operands);
        }
      }
      if (pools.size > 0) {
        const after = yDocToDocument(yDoc);
        const geometries = new Map<Id, Geometry>();
        for (const poolId of pools) {
          for (const [laneId, geometry] of layoutLanes(after, poolId)) {
            geometries.set(laneId, geometry);
          }
        }
        setNodeGeometries(yDoc, geometries);
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const pasteElements = useCallback(
    (payload: ClipboardPayload, offset: Point): ReadonlySet<Id> => {
      // The live Y.Doc, not the last render, is what the new ids must not collide with.
      const pasted = paste(yDocToDocument(yDoc), payload, offset);
      endUndoStep(undoManager);
      addElements(yDoc, pasted.nodes, pasted.edges);
      endUndoStep(undoManager);
      return new Set<Id>([...pasted.nodes.map((node) => node.id), ...pasted.edges.map((edge) => edge.id)]);
    },
    [yDoc, undoManager],
  );
  const duplicateElements = useCallback(
    (ids: ReadonlySet<Id>, offset: Point) => pasteElements(copy(yDocToDocument(yDoc), ids), offset),
    [yDoc, pasteElements],
  );
  const setLabel = useCallback(
    (elementId: Id, label: string) => {
      // Closed on both sides: a label commit never merges into a gesture still capturing, nor into the next edit.
      endUndoStep(undoManager);
      // Node and edge ids never collide (addNodes, connect), so at most one of these finds the element.
      if (!setNodeLabel(yDoc, elementId, label)) {
        setEdgeLabel(yDoc, elementId, label);
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setClassMembers = useCallback(
    (nodeId: Id, part: MemberPart, members: readonly string[]) => {
      const node = yDocToDocument(yDoc).nodes.find((candidate) => candidate.id === nodeId);
      if (node === undefined || !memberPartsOf(node.shape).includes(part)) {
        return;
      }
      const uml = umlShape(node.shape);
      const current = memberLines(node.props, part);
      if (current.length === members.length && current.every((line, i) => line === members[i])) {
        return;
      }
      const props: Props = Object.fromEntries(Object.entries(node.props ?? {}).filter(([key]) => key !== part));
      if (members.length > 0) {
        props[part] = [...members];
      }
      // The box is sized from its content, so the same undo step that changes the list refits it.
      endUndoStep(undoManager);
      setNodePropsInDoc(yDoc, new Map([[nodeId, Object.keys(props).length === 0 ? null : props]]));
      if (part === 'columns') {
        // A renamed column keeps the connectors drawn to its row (a column port is addressed by name, columnPorts.ts).
        renameColumnPorts(yDoc, nodeId, columnRenames(current, members));
      }
      if (node.geometry !== undefined) {
        const fitted = isClassShape(uml) ? fitClass(uml, node.label, props, node.geometry) : fitEntity(node.label, props, node.geometry);
        setNodeGeometries(yDoc, new Map([[nodeId, fitted]]));
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setEdgeKind = useCallback(
    (edgeId: Id, kind: string) => {
      endUndoStep(undoManager);
      setEdgeKindInDoc(yDoc, edgeId, kind);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
const setEdgeEnds = useCallback(
    (edgeId: Id, arrowStart: ArrowHead | undefined, arrowEnd: ArrowHead | undefined) => {
      endUndoStep(undoManager);
      setEdgeEndsInDoc(yDoc, edgeId, arrowStart, arrowEnd);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setEdgeRoutingMode = useCallback(
    (edgeId: Id, mode: RoutingMode) => {
      endUndoStep(undoManager);
      setEdgeRoutingModeInDoc(yDoc, edgeId, mode);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const editFlythrough = useCallback(
    (edit: (current: Flythrough | undefined) => Flythrough | undefined): boolean => {
      endUndoStep(undoManager);
      const changed = setFlythroughInDoc(yDoc, edit(readFlythrough(yDoc)));
      endUndoStep(undoManager);
      return changed;
    },
    [yDoc, undoManager],
  );
  const setNodeStyle = useCallback(
    (ids: ReadonlySet<Id>, field: NodeStyleField, value: NodeStyle[NodeStyleField] | undefined) => {
      endUndoStep(undoManager);
      const current = yDocToDocument(yDoc);
      const styles = new Map<Id, NodeStyle | null>();
      for (const id of ids) {
        const node = current.nodes.find((candidate) => candidate.id === id);
        if (node === undefined) {
          continue;
        }
        const existing = node.style ?? {};
        if (value === undefined) {
          const rest = Object.fromEntries(Object.entries(existing).filter(([key]) => key !== field)) as NodeStyle;
          styles.set(id, styleHasValue(rest) ? rest : null);
        } else {
          if (existing[field] === value) {
            continue;
          }
          styles.set(id, { ...existing, [field]: value });
        }
      }
      setNodeStyles(yDoc, styles);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setEdgeStyle = useCallback(
    (ids: ReadonlySet<Id>, field: EdgeStyleField, value: EdgeStyle[EdgeStyleField] | undefined) => {
      endUndoStep(undoManager);
      const current = yDocToDocument(yDoc);
      const styles = new Map<Id, EdgeStyle | null>();
      for (const id of ids) {
        const edge = current.edges.find((candidate) => candidate.id === id);
        if (edge === undefined) {
          continue;
        }
        const existing = edge.style ?? {};
        if (value === undefined) {
          const cleaned = Object.fromEntries(Object.entries(existing).filter(([key]) => key !== field)) as EdgeStyle;
          styles.set(id, styleHasValue(cleaned) ? cleaned : null);
        } else {
          if (existing[field] === value) {
            continue;
          }
          styles.set(id, { ...existing, [field]: value });
        }
      }
      setEdgeStyles(yDoc, styles);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const resetRoute = useCallback(
    (edgeId: Id) => {
      endUndoStep(undoManager);
      clearEdgeWaypoints(yDoc, edgeId);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setNodeProps = useCallback(
    (id: Id, props: Props | null) => {
      endUndoStep(undoManager);
      setNodePropsInDoc(yDoc, new Map([[id, props]]));
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const connect = useCallback(
    (connection: Connection) => {
      endUndoStep(undoManager);
      const edgeId = connectInDoc(yDoc, connection);
      endUndoStep(undoManager);
      return edgeId;
    },
    [yDoc, undoManager],
  );
  const connectSelected = useCallback(
    (selection: ReadonlySet<Id>) => {
      endUndoStep(undoManager);
      const ids = connectionsForSelection(yDocToDocument(yDoc), selection).map((connection) => connectInDoc(yDoc, connection));
      endUndoStep(undoManager);
      return ids;
    },
    [yDoc, undoManager],
  );
  const reconnect = useCallback(
    (reconnection: Reconnection) => {
      endUndoStep(undoManager);
      reconnectInDoc(yDoc, reconnection);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  // Align, distribute and arrange read the Y.Doc itself, not the last render, like a delete does.
  const align = useCallback(
    (ids: ReadonlySet<Id>, command: AlignCommand) => {
      endUndoStep(undoManager);
      setNodeGeometries(yDoc, alignSelection(indexDocument(yDocToDocument(yDoc)), ids, command));
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const distribute = useCallback(
    (ids: ReadonlySet<Id>, command: DistributeCommand) => {
      endUndoStep(undoManager);
      setNodeGeometries(yDoc, distributeSelection(indexDocument(yDocToDocument(yDoc)), ids, command));
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const arrange = useCallback(
    (ids: ReadonlySet<Id>, command: ArrangeCommand) => {
      endUndoStep(undoManager);
      const current = yDocToDocument(yDoc);
      const arranged = arrangeDocument(current, ids, command);
      if (arranged !== current) {
        setElementOrder(yDoc, arranged);
        // A message that moved takes the membership of where it landed, as a drag does.
        if (current.type === 'sequence') {
          inheritMembership(yDoc, new Set([...ids].filter((id) => current.edges.findIndex((edge) => edge.id === id) !== arranged.edges.findIndex((edge) => edge.id === id))));
        }
      }
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  // Fit, group and ungroup read the Y.Doc itself, like align, so they act on what another writer just added too.
  const fitToContents = useCallback(
    (ids: ReadonlySet<Id>, padding: number) => {
      endUndoStep(undoManager);
      setNodeGeometries(yDoc, fitContainers(yDocToDocument(yDoc), ids, padding));
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  // Swimlane commands (model/swimlanes.ts): each reads the live Y.Doc, writes the plan in one transaction and closes
  // its own undo step, so an Add/Move/Orientation command is one Cmd/Ctrl+Z.
  const addLane = useCallback(
    (poolId: Id): Id | undefined => {
      const plan = addLanePlan(yDocToDocument(yDoc), poolId);
      if (plan === undefined) {
        return undefined;
      }
      endUndoStep(undoManager);
      addNodes(yDoc, [plan.lane]);
      setNodeGeometries(yDoc, plan.geometries);
      endUndoStep(undoManager);
      return plan.lane.id;
    },
    [yDoc, undoManager],
  );
  const removeLane = useCallback(
    (laneId: Id) => {
      const lane = yDocToDocument(yDoc).nodes.find((node) => node.id === laneId);
      if (lane === undefined || !isLane(lane) || lane.parent === undefined) {
        return;
      }
      // Deleting a lane takes its contents and their edges with it and repacks the pool (deleteElements).
      deleteElements(new Set([laneId]));
    },
    [yDoc, deleteElements],
  );
  const moveLane = useCallback(
    (laneId: Id, direction: -1 | 1) => {
      const plan = moveLanePlan(yDocToDocument(yDoc), laneId, direction);
      if (plan === undefined) {
        return;
      }
      endUndoStep(undoManager);
      setElementOrder(yDoc, { nodes: plan.nodes, edges: [] });
      setNodeGeometries(yDoc, plan.geometries);
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const setPoolOrientation = useCallback(
    (poolId: Id, orientation: SwimlaneOrientation) => {
      const pool = yDocToDocument(yDoc).nodes.find((node) => node.id === poolId);
      if (pool === undefined || !(pool.kind === 'pool')) {
        return;
      }
      endUndoStep(undoManager);
      setNodePropsInDoc(yDoc, new Map([[poolId, { ...(pool.props ?? {}), orientation }]]));
      setNodeGeometries(yDoc, layoutLanes(yDocToDocument(yDoc), poolId));
      endUndoStep(undoManager);
    },
    [yDoc, undoManager],
  );
  const group = useCallback(
    (ids: ReadonlySet<Id>, padding: number) => {
      const plan = groupPlan(yDocToDocument(yDoc), ids, padding);
      if (plan === undefined) {
        return undefined;
      }
      endUndoStep(undoManager);
      applyGroup(yDoc, plan);
      endUndoStep(undoManager);
      return plan.container.id;
    },
    [yDoc, undoManager],
  );
  const ungroup = useCallback(
    (ids: ReadonlySet<Id>) => {
      const plan = ungroupPlan(yDocToDocument(yDoc), ids);
      if (plan === undefined) {
        return undefined;
      }
      if (plan.containers.size > 0) {
        endUndoStep(undoManager);
        applyUngroup(yDoc, plan);
        endUndoStep(undoManager);
      }
      return { released: new Set(plan.parents.keys()), refused: plan.refused };
    },
    [yDoc, undoManager],
  );
  const applyTextDocument = useCallback((next: Document, base: Document) => {
    endUndoStep(undoManager);
    applyDocumentDiff(yDoc, next, base);
    endUndoStep(undoManager);
  }, [yDoc, undoManager]);
  const mergeTextDocument = useCallback((next: Document, base: Document): TextMergeResult => {
    endUndoStep(undoManager);
    const result = mergeTextEdit(yDoc, base, next);
    endUndoStep(undoManager);
    return result;
  }, [yDoc, undoManager]);
  const takeTextConflicts = useCallback((next: Document, conflicts: readonly MergeConflict[]) => {
    endUndoStep(undoManager);
    takeMine(yDoc, next, conflicts);
    endUndoStep(undoManager);
  }, [yDoc, undoManager]);
  // Tidy lays out the current document on the server and applies the result through the same diff as the DSL panel,
  // so it is one undo step and leaves labels, styles and props the layout never touched exactly as they were.
  const tidy = useCallback(
    async (algorithm: LayoutAlgorithm): Promise<string[]> => {
      const current = yDocToDocument(yDoc);
      const { document: laidOut, warnings } = await requestLayout(current, algorithm);
      applyTextDocument(laidOut, current);
      return warnings;
    },
    [yDoc, applyTextDocument],
  );
  const undo = useCallback(() => {
    endUndoStep(undoManager);
    undoManager.undo();
  }, [undoManager]);
  const redo = useCallback(() => {
    endUndoStep(undoManager);
    undoManager.redo();
  }, [undoManager]);

  // The history keys are always kept from the browser in an editor, as they were before undo could be disabled.
  const actions = useMemo<readonly EditorAction[]>(
    () => [
      {
        id: 'document.undo',
        label: 'Undo',
        group: 'Edit',
        shortcut: { label: 'Cmd/Ctrl+Z', matches: (e) => historyCommand(e) === 'undo', claim: () => true },
        enabled: () => undoManager.canUndo(),
        run: undo,
      },
      {
        id: 'document.redo',
        label: 'Redo',
        group: 'Edit',
        shortcut: { label: 'Cmd/Ctrl+Shift+Z', matches: (e) => historyCommand(e) === 'redo', claim: () => true },
        enabled: () => undoManager.canRedo(),
        run: redo,
      },
    ],
    [undoManager, undo, redo],
  );

  useEffect(() => {
    if (id === undefined) {
      return;
    }
    const onKeyDown = (e: KeyboardEvent): void => {
      if (!isTextField(e.target)) {
        dispatchShortcut(actions, e);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [id, actions]);

  const saving = useRef(false);
  // The save in flight, for a flush to wait on; resolves whether it stored the whole document.
  const inFlight = useRef<Promise<boolean> | undefined>(undefined);
  // Bumped when a save starts, so a poll that was already in flight when it did is recognised as stale and dropped.
  const saveGeneration = useRef(0);
  // Set for the duration of an in-flight renameTitle PATCH, so a whole-document save that starts meanwhile (e.g. the
  // Save button clicked while the title field still has an uncommitted edit, whose blur fires renameTitle first) waits
  // for it rather than racing it: without this, save() reads the pre-rename title and base version, and its PUT loses
  // to the rename's PATCH with a stale version, showing a false conflict for what was actually a clean save.
  const renameInFlight = useRef<Promise<void> | undefined>(undefined);

  // Autosave's retry state: how many saves in a row have failed, and whether the last failure is worth trying again.
  const failures = useRef(0);
  const retryable = useRef(false);
  const [retrying, setRetrying] = useState(false);
  // One per failed save, so the retry is rescheduled even when a fast failure leaves the status as it was (error to error).
  const [failureTick, setFailureTick] = useState(0);
  // When the document first became unsaved, so continuous editing still saves (AUTOSAVE_MAX_WAIT_MS).
  const dirtySince = useRef<number | undefined>(undefined);
  const [stale, setStale] = useState<StaleState | undefined>(undefined);
  const [localCopies, setLocalCopies] = useState<readonly LocalCopy[]>(() => (id === undefined ? [] : copiesStillDifferent(id, initial)));
  // The copy this run keeps the user's current document in while a save is failing or refused; undefined when none is
  // open. It is replaced in place as the document changes and dropped when a save stores the whole document. A copy
  // from an earlier visit, or one set aside when theirs was loaded over it, is never this one and is never dropped by a save.
  const ownCopyId = useRef<string | undefined>(undefined);

  /** Merges a newer stored version into the Y.Doc and makes it the base the next save quotes. */
  const applyRemote = useCallback(
    (theirs: Document, version: string) => {
      const { changed, conflicts } = mergeRemote(yDoc, baseDocument.current, theirs);
      const ancestor = baseDocument.current;
      baseDocument.current = theirs;
      baseVersion.current = version;
      if (!changed && conflicts.length === 0) {
        return;
      }
      const by = newAgent(ancestor, theirs);
      setRemote((previous) => ({ by, conflicts: mergeConflicts(previous?.conflicts ?? [], conflicts), theirs }));
    },
    [yDoc],
  );

  const syncNow = useCallback(
    async (signal?: AbortSignal) => {
      // A stale-save question pins "mine" and "theirs" to what the user was shown: nothing merges until it is answered.
      if (id === undefined || saving.current || inConflict()) {
        return;
      }
      const generation = saveGeneration.current;
      const asked = baseVersion.current;
      try {
        const next = await fetchDocumentIfChanged(id, asked, fetch, signal);
        // A save that started meanwhile bumped the generation: what came back may be that save, so it is not merged.
        if (next === undefined || generation !== saveGeneration.current || asked !== baseVersion.current || inConflict()) {
          return;
        }
        applyRemote(next.document, next.etag);
      } catch {
        // A failed poll changes nothing; the next one tries again.
      }
    },
    [id, applyRemote, inConflict],
  );

  useEffect(() => {
    if (id === undefined || liveSyncIntervalMs === undefined) {
      return;
    }
    // One poll at a time: the next is scheduled only when the last has finished, so a slow server never piles them up.
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let polling = false;
    const tick = async (): Promise<void> => {
      polling = true;
      await syncNow(controller.signal);
      polling = false;
      if (!controller.signal.aborted) {
        timer = setTimeout(() => void tick(), liveSyncIntervalMs);
      }
    };
    // Coming back to a background tab, whose timers the browser may have throttled, checks at once.
    const onVisible = (): void => {
      if (!globalThis.document.hidden && !polling) {
        clearTimeout(timer);
        void tick();
      }
    };
    timer = setTimeout(() => void tick(), liveSyncIntervalMs);
    globalThis.document.addEventListener('visibilitychange', onVisible);
    return () => {
      controller.abort();
      clearTimeout(timer);
      globalThis.document.removeEventListener('visibilitychange', onVisible);
    };
  }, [id, liveSyncIntervalMs, syncNow]);

  /** Keeps the user's current document aside in this browser, replacing the copy this run already keeps. */
  const keepLocalCopy = useCallback(() => {
    if (id === undefined) {
      return;
    }
    ownCopyId.current ??= newCopyId();
    storeLocalCopy({ copyId: ownCopyId.current, documentId: id, document: yDocToDocument(yDoc), keptAt: new Date().toISOString() });
    setLocalCopies(readLocalCopies(id));
  }, [id, yDoc]);

  /** Drops the copy this run keeps, once the server has the document it was protecting. */
  const dropOwnCopy = useCallback(() => {
    if (id !== undefined && ownCopyId.current !== undefined) {
      clearLocalCopy(id, ownCopyId.current);
      ownCopyId.current = undefined;
      setLocalCopies(readLocalCopies(id));
    }
  }, [id]);

  // The session ending behind the person's back (a password reset, a deactivation; S9) swaps the page for the sign-in
  // screen, which would take unsaved edits with it. Keep them aside first, in the same local copy a failed save uses, and
  // say so (the sign-in notice reads the answer): signing back in and reopening the document offers them again.
  useEffect(() => {
    if (id === undefined) {
      return;
    }
    return onSessionEnded(() => {
      if (statusRef.current === 'saved') {
        return false;
      }
      keepLocalCopy();
      return true;
    });
  }, [id, keepLocalCopy]);

  const discardLocalCopy = useCallback(
    (copyId: string) => {
      if (id === undefined) {
        return;
      }
      clearLocalCopy(id, copyId);
      if (ownCopyId.current === copyId) {
        ownCopyId.current = undefined;
      }
      setLocalCopies(readLocalCopies(id));
    },
    [id],
  );

  const saveCopyAsNew = useCallback(
    async (copyId: string): Promise<string> => {
      const copy = localCopies.find((candidate) => candidate.copyId === copyId);
      if (copy === undefined) {
        throw new Error('That local copy is no longer there.');
      }
      const kept = parseDocument(copy.document);
      const created = await createDocumentFrom({ ...kept, title: copyTitle(kept.title) });
      discardLocalCopy(copyId);
      return created;
    },
    [localCopies, discardLocalCopy],
  );

  /**
   * After a save was refused as stale: reads the server's current version and, when merging it would raise no clash with
   * what the person changed (`mergeRemote` on a throw-away copy first, so a clash leaves the editor exactly as it was),
   * merges it in and makes it the base the retry quotes. False when it cannot be read or would clash, so the caller asks.
   */
  const rebaseOntoServer = useCallback(async (): Promise<boolean> => {
    if (id === undefined) {
      return false;
    }
    try {
      const next = await fetchDocumentIfChanged(id, undefined);
      if (next === undefined) {
        return false;
      }
      const trial = new Y.Doc();
      Y.applyUpdate(trial, Y.encodeStateAsUpdate(yDoc));
      const { conflicts } = mergeRemote(trial, baseDocument.current, next.document);
      trial.destroy();
      if (conflicts.length > 0) {
        return false;
      }
      applyRemote(next.document, next.etag);
      return true;
    } catch {
      return false;
    }
  }, [id, yDoc, applyRemote]);

  const performSave = useCallback(
    async (keepalive = false): Promise<boolean> => {
      if (id === undefined || saving.current || inConflict()) {
        return false;
      }
      saving.current = true;
      const run = (async (): Promise<boolean> => {
        if (renameInFlight.current !== undefined) {
          await renameInFlight.current.catch(() => {
            // The rename's own caller surfaces its failure; a save that raced it proceeds with whatever base version and
            // title resulted (unchanged, on a refused rename).
          });
        }
        saveGeneration.current += 1;
        setStatus('saving');
        setSaveError(undefined);
        setRetrying(false);
        const changesAtSave = changes.current;
        // The snapshot is taken here, after the rename settled and with nothing awaited before the request is made, so
        // what is sent is what the document was when this save began; an edit after that is the next save's.
        let sent = yDocToDocument(yDoc);
        let stored = false;
        let rebased = false;
        try {
          for (;;) {
            try {
              baseVersion.current = await putDocument(id, sent, baseVersion.current, fetch, keepalive ? { keepalive: true } : {});
              // What was sent is now the stored version's content: the ancestor the next merge compares against.
              baseDocument.current = sent;
              failures.current = 0;
              retryable.current = false;
              // An edit made while the request was in flight is not in what was saved, unless it changed nothing that shows.
              stored = changes.current === changesAtSave || documentsEqual(yDocToDocument(yDoc), sent);
              setStatus(stored ? 'saved' : 'unsaved');
              if (stored) {
                // The server has the whole document now, so nothing is kept aside any longer.
                dropOwnCopy();
              }
              break;
            } catch (error) {
              if (error instanceof SaveConflict) {
                // The server has a newer version than the one this save was made from. If what changed there does not
                // touch anything the person changed, take it in the way the live-sync poll would have and save once
                // more: whether the poll or the save got there first must not decide if they see an "Out of date"
                // screen for a harmless difference (QA S10). A real clash, or a second refusal, is asked about below.
                if (!rebased && !keepalive && (await rebaseOntoServer())) {
                  rebased = true;
                  sent = yDocToDocument(yDoc);
                  continue;
                }
                // Refused because another writer got there first. Nothing is overwritten and nothing is discarded: the
                // user's version is kept aside, autosave stops, and the viewer asks (reloadTheirs / keepMine / saveMineAsNew).
                retryable.current = false;
                setStatus('conflict');
                setSaveError(CONFLICT_MESSAGE);
                setStale({ busy: false, error: undefined });
                keepLocalCopy();
              } else {
                const failure = classifySaveFailure(error);
                failures.current += 1;
                setFailureTick((tick) => tick + 1);
                retryable.current = failure.retry;
                setStatus('error');
                setSaveError(failure.message);
                setRetrying(failure.retry);
                keepLocalCopy();
              }
              break;
            }
          }
        } finally {
          saving.current = false;
        }
        return stored;
      })();
      inFlight.current = run;
      const clear = (): void => {
        if (inFlight.current === run) {
          inFlight.current = undefined;
        }
      };
      void run.then(clear, clear);
      return run;
    },
    [id, yDoc, setStatus, keepLocalCopy, dropOwnCopy, inConflict, rebaseOntoServer],
  );

  const save = useCallback((): Promise<boolean> => performSave(), [performSave]);

  const flush = useCallback(async (): Promise<boolean> => {
    if (inFlight.current !== undefined) {
      await inFlight.current;
    }
    if (statusRef.current === 'saved') {
      return true;
    }
    return performSave();
  }, [performSave]);

  // Autosave: a save after the last change (at most AUTOSAVE_MAX_WAIT_MS after the first unsaved one), or a retry of a
  // failed one after a growing delay. Every local change restarts the debounce through `changeTick`. Nothing is
  // scheduled while a save is in flight (it settles the status the effect reacts to) or a stale-save question is open.
  useEffect(() => {
    if (autosaveDelayMs === undefined || id === undefined) {
      return;
    }
    if (saveStatus === 'saved') {
      dirtySince.current = undefined;
      failures.current = 0;
      return;
    }
    if (saveStatus === 'saving' || saveStatus === 'conflict' || (saveStatus === 'error' && !retryable.current)) {
      return;
    }
    dirtySince.current ??= Date.now();
    const wait =
      saveStatus === 'error'
        ? retryDelay(failures.current, autosaveDelayMs)
        : Math.min(autosaveDelayMs, Math.max(0, dirtySince.current + AUTOSAVE_MAX_WAIT_MS - Date.now()));
    const timer = setTimeout(() => {
      void performSave();
    }, wait);
    return () => {
      clearTimeout(timer);
    };
  }, [autosaveDelayMs, id, saveStatus, changeTick, failureTick, performSave]);

  // Leaving: a tab going hidden or closing saves at once rather than waiting out the delay, and coming back online
  // retries a save the network refused without waiting for its backoff.
  useEffect(() => {
    if (autosaveDelayMs === undefined || id === undefined) {
      return;
    }
    const flushOnLeave = (): void => {
      const status = statusRef.current;
      if (status === 'unsaved' || (status === 'error' && retryable.current)) {
        void performSave(true);
      }
    };
    const onVisibility = (): void => {
      if (globalThis.document.hidden) {
        flushOnLeave();
      }
    };
    const onOnline = (): void => {
      if (statusRef.current === 'error' && retryable.current) {
        void performSave();
      }
    };
    window.addEventListener('pagehide', flushOnLeave);
    window.addEventListener('online', onOnline);
    globalThis.document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.removeEventListener('pagehide', flushOnLeave);
      window.removeEventListener('online', onOnline);
      globalThis.document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [autosaveDelayMs, id, performSave]);

  // While a save is failing or refused the kept-aside copy follows the document, so a crash after the last edit still has it.
  useEffect(() => {
    if (saveStatus === 'saved') {
      // Stored, or undone back to what is stored: the copy this run was keeping protects nothing now.
      dropOwnCopy();
    } else if (saveStatus === 'error' || saveStatus === 'conflict' || ownCopyId.current !== undefined) {
      keepLocalCopy();
    }
  }, [document, saveStatus, keepLocalCopy, dropOwnCopy]);

  /** Runs one of the stale-save answers, showing that it is busy and, if it fails, why; the question stays open then. */
  const answerStale = useCallback(async (answer: () => Promise<void>): Promise<void> => {
    setStale({ busy: true, error: undefined });
    try {
      await answer();
    } catch (error) {
      setStale({ busy: false, error: staleFailureMessage(error) });
    }
  }, []);

  /** Replaces the editor's content with the server's current version, first keeping the user's own aside if `keepMine`. */
  const loadServerVersion = useCallback(async (keepMine: boolean): Promise<void> => {
    if (id === undefined) {
      return;
    }
    const next = await fetchDocumentIfChanged(id, undefined);
    if (next === undefined) {
      throw new Error('The server sent no document.');
    }
    const mine = yDocToDocument(yDoc);
    if (keepMine && !documentsEqual(mine, normalised(next.document))) {
      keepLocalCopy();
      // Choosing theirs answers the question: the copy stays as the net under that choice, offered for the rest of this
      // visit but not nagging on every later open (QA S10).
      if (ownCopyId.current !== undefined) {
        markLocalCopyAnswered(id, ownCopyId.current);
        setLocalCopies(readLocalCopies(id));
      }
    }
    // Set aside: from here the copy is the user's to deal with, and a later save must not drop it.
    ownCopyId.current = undefined;
    // With the editor's own content as the base, nothing is a local edit, so the server's side wins everywhere.
    mergeRemote(yDoc, mine, next.document);
    // Undoing back into "mine" would put it on the canvas as an unsaved edit that autosave then writes over theirs.
    undoManager.clear();
    baseDocument.current = next.document;
    baseVersion.current = next.etag;
    failures.current = 0;
    retryable.current = false;
    dirtySince.current = undefined;
    setRemote(undefined);
    setSaveError(undefined);
    setRetrying(false);
    setStale(undefined);
    setStatus('saved');
  }, [id, yDoc, undoManager, keepLocalCopy, setStatus]);

  const reloadTheirs = useCallback((): Promise<void> => answerStale(() => loadServerVersion(true)), [answerStale, loadServerVersion]);

  const restoreVersion = useCallback(
    async (versionId: string): Promise<void> => {
      if (id === undefined) {
        return;
      }
      if (!(await flush())) {
        throw new Error('Your latest changes could not be saved, so restoring now could lose them. Resolve the save first.');
      }
      const base = baseVersion.current;
      if (base === undefined) {
        throw new Error('This document has no saved version to restore over yet.');
      }
      await restoreVersionRequest(id, versionId, base);
      await loadServerVersion(false);
    },
    [id, flush, loadServerVersion],
  );

  const keepMine = useCallback(
    (): Promise<void> =>
      answerStale(async () => {
        if (id === undefined) {
          return;
        }
        // The server's version as it is now, not as it was when the save was refused: overwriting an older one would
        // put the user's version over a change they were never shown.
        const current = await fetchDocumentIfChanged(id, undefined);
        if (current === undefined) {
          throw new Error('The server sent no document.');
        }
        const changesAtSave = changes.current;
        const sent = yDocToDocument(yDoc);
        baseVersion.current = await putDocument(id, sent, current.etag);
        baseDocument.current = sent;
        failures.current = 0;
        retryable.current = false;
        const stored = changes.current === changesAtSave || documentsEqual(yDocToDocument(yDoc), sent);
        setSaveError(undefined);
        setStale(undefined);
        setRemote(undefined);
        setStatus(stored ? 'saved' : 'unsaved');
        if (stored) {
          dropOwnCopy();
        }
      }),
    [id, yDoc, answerStale, setStatus, dropOwnCopy],
  );

  const saveMineAsNew = useCallback(async (): Promise<string | undefined> => {
    let created: string | undefined;
    await answerStale(async () => {
      const mine = yDocToDocument(yDoc);
      created = await createDocumentFrom({ ...mine, title: copyTitle(mine.title) });
      // It now lives in a real document, so the kept-aside copy has done its job.
      dropOwnCopy();
      await loadServerVersion(false);
    });
    return created;
  }, [yDoc, answerStale, dropOwnCopy, loadServerVersion]);

  /**
   * Renames the document through its own guarded PATCH (docs/api.md), independent of the canvas's undo/save cycle:
   * it neither adds an undo step nor disturbs unsaved canvas edits, and does not itself touch save status. Composed
   * from the same base version the next full save would quote, so a rename and a save never disagree about it; a
   * stale base throws SaveConflict like a refused save.
   */
  const renameTitle = useCallback(
    (title: string): Promise<void> => {
      if (id === undefined || baseVersion.current === undefined) {
        return Promise.resolve();
      }
      const idAtCall = id;
      const baseAtCall = baseVersion.current;
      const promise = (async () => {
        const nextVersion = await renameDocument(idAtCall, title, baseAtCall);
        baseVersion.current = nextVersion;
        baseDocument.current = { ...baseDocument.current, title };
        setTitleInDoc(yDoc, title, REMOTE_ORIGIN);
      })();
      renameInFlight.current = promise;
      const clear = (): void => {
        if (renameInFlight.current === promise) {
          renameInFlight.current = undefined;
        }
      };
      promise.then(clear, clear);
      return promise;
    },
    [id, yDoc],
  );

  const keepLocal = useCallback(() => {
    setRemote(undefined);
  }, []);
  const takeTheirs = useCallback(() => {
    if (remote === undefined || remote.conflicts.length === 0) {
      return;
    }
    endUndoStep(undoManager);
    takeRemote(yDoc, remote.theirs, remote.conflicts);
    endUndoStep(undoManager);
    setRemote(undefined);
  }, [remote, yDoc, undoManager]);

  // The report without the other writer's document, which only takeTheirs needs.
  const shownRemote = useMemo<RemoteChange | undefined>(() => (remote === undefined ? undefined : { by: remote.by, conflicts: remote.conflicts }), [remote]);

  return {
    document,
    moveNodes,
    moveEnd,
    moveCancel,
    insertMessage,
    reorderElement,
    wrapInFragment,
    addFragmentOperand,
    addNote,
    toggleActivation,
    setFragmentGuard,
    setFragmentOperator,
    reparentNodes,
    fitToContents,
    addLane,
    removeLane,
    moveLane,
    setPoolOrientation,
    group,
    ungroup,
    addNode,
    addNodeGroup,
    addParticipant,
    deleteElements,
    pasteElements,
    duplicateElements,
    setLabel,
    setClassMembers,
    setEdgeKind,
    setEdgeEnds,
    setNodeStyle,
    setEdgeStyle,
    setNodeProps,
    setEdgeRoutingMode,
    resetRoute,
    editFlythrough,
    connect,
    connectSelected,
    reconnect,
    align,
    distribute,
    arrange,
    tidy,
    applyTextDocument,
    mergeTextDocument,
    takeTextConflicts,
    undo,
    redo,
    actions,
    save,
    flush,
    renameTitle,
    saveStatus,
    saveError,
    retrying,
    stale,
    reloadTheirs,
    restoreVersion,
    keepMine,
    saveMineAsNew,
    localCopies,
    saveCopyAsNew,
    discardLocalCopy,
    remote: shownRemote,
    keepLocal,
    takeTheirs,
  };
}

/**
 * After a sequence message has moved, gives each of `moved` the fragment its new neighbours share (none when they share
 * none), so dragging a message out of a frame takes it out and dropping it between two members puts it in. Writes only
 * the messages whose membership changes; the caller owns the undo step.
 */
function inheritMembership(yDoc: Y.Doc, moved: ReadonlySet<Id>): void {
  const current = yDocToDocument(yDoc);
  const writes = new Map<Id, Props | null>();
  for (const id of moved) {
    const at = current.edges.findIndex((edge) => edge.id === id);
    const edge = current.edges[at];
    if (edge !== undefined) {
      writes.set(id, withMembership(edge, membershipAt(current, at, id)).props ?? null);
    }
  }
  setEdgeProps(yDoc, writes);
}

/** A document as the editor holds it, which is what `documentsEqual` must be given both sides of. */
function normalised(document: Document): Document {
  return yDocToDocument(documentToYDoc(document));
}

/** The title a document saved aside as a new one is given. */
function copyTitle(title: string): string {
  return `${title} (my copy)`;
}

/**
 * The copies kept for a document on an earlier visit that still differ from what was loaded. One equal to it (the save
 * that failed did land after all, or the user moved on to the same content) protects nothing and is cleared.
 */
function copiesStillDifferent(id: string, loaded: Document): LocalCopy[] {
  const current = normalised(loaded);
  return readLocalCopies(id).filter((copy) => {
    if (copy.answered === true) {
      // The person already chose the server's version over this one (Reload theirs): not offered again, and gone after a week.
      if (answeredCopyExpired(copy)) {
        clearLocalCopy(id, copy.copyId);
      }
      return false;
    }
    try {
      if (documentsEqual(normalised(parseDocument(copy.document)), current)) {
        clearLocalCopy(id, copy.copyId);
        return false;
      }
      return true;
    } catch {
      // A copy that no longer parses cannot be shown or saved; it is left in storage rather than deleted unseen.
      return false;
    }
  });
}

/** Why a stale-save answer did not go through, for the question that stays open. */
function staleFailureMessage(error: unknown): string {
  if (error instanceof SaveConflict) {
    return 'The document changed again while you were deciding. Nothing was overwritten; choose again.';
  }
  if (error instanceof TypeError) {
    return 'Cannot reach the server. Nothing was changed; try again when it is back.';
  }
  return error instanceof Error ? error.message : 'That did not work.';
}

/** The agent named by an AI provenance entry the newer version has and the older one does not, if there is one. */
function newAgent(before: Document, after: Document): string | undefined {
  const known = before.meta.aiProvenance?.length ?? 0;
  return after.meta.aiProvenance?.slice(known).at(-1)?.agent;
}

/** Folds a new merge's conflicts into those still open: one entry per element and reason, fields unioned. */
function mergeConflicts(open: readonly MergeConflict[], added: readonly MergeConflict[]): MergeConflict[] {
  const byKey = new Map(open.map((conflict) => [`${conflict.kind}:${conflict.id}:${conflict.reason}`, conflict]));
  for (const conflict of added) {
    const key = `${conflict.kind}:${conflict.id}:${conflict.reason}`;
    const existing = byKey.get(key);
    byKey.set(key, existing === undefined ? conflict : { ...conflict, fields: [...new Set([...existing.fields, ...conflict.fields])] });
  }
  return [...byKey.values()];
}

/** True when a sparse style still carries at least one deliberate override (a value that is not the default false). */
function styleHasValue(style: object): boolean {
  return Object.values(style).some((value) => value !== undefined && value !== false);
}
