/**
 * The editor canvas: DocumentPage loads a document and its version, this owns the Y.Doc behind it and the Save that
 * writes it back, and DiagramCanvas handles selection, drag and label editing on top of the render. The one-line bar
 * carries the title, save state and the diagram theme switcher, and the zoom cluster floats bottom right as
 * design/atlas/editor-shell.md lays the frame out. A refused save (412/428) is surfaced here and the local document
 * is left untouched; deciding how to merge is out of scope.
 *
 * While editing, a tool rail sits left of the canvas (editor-shell.md), in the atlas's order: Select (V); Add shape,
 * which places the document type's default kind at the view centre, stepped by one grid step from a node already
 * there, then selected; Shapes (S, or Cmd/Ctrl+Shift+S), which opens the shape panel
 * (editor/ShapePanel.tsx, design/atlas/shape-connector-palette.md) docked beside the rail, closed by Escape or by
 * switching tool; and Connector (C), which draws an edge from port to port. Select and Connector are modes, shown
 * pressed when active; the panel is a toggle. "Add shape" places immediately through placeShape (view centre,
 * stepped). A panel glyph instead arms its NodeTemplate (armedShape) and the next pointerdown on the canvas surface
 * places it centred on that point via the same createNode/pointToWorld path drag-and-drop uses, then disarms;
 * repeated glyph-then-click lands each shape at its own point. A shape dragged from the panel onto the canvas is
 * dropped at the pointer instead, tracked as a plain, uncaptured pointer gesture from the glyph's pointerdown
 * (dragShapeStart) rather than native HTML5 drag-and-drop, whose dragstart the M2 final-check found unreliable.
 * The select tool connects too, from the handles on a hovered or selected shape, and a selected edge's ends can be
 * dragged to re-attach it (DiagramCanvas); a new connector is selected as soon as it is drawn, ready for a label.
 * Right of the canvas sits the properties panel (design/atlas/properties-panel.md): its Arrange section
 * (editor/ArrangePanel.tsx) aligns, distributes and orders the canvas's selection, each one undo step, and with one edge
 * selected the Connector section (editor/ConnectorPanel.tsx) follows it, whose kind picker switches the edge's kind as
 * one undo step. The Container section (editor/ContainerPanel.tsx) groups, ungroups and fits to contents, the same
 * commands as Cmd/Ctrl+G and Cmd/Ctrl+Shift+G on the canvas, each one undo step. A fitted or grouped container leaves
 * two grid steps of the diagram theme on every side (model/grouping.ts). Ungroup leaves a container with connections
 * of its own in place rather than delete them, still ungrouping the rest of the selection; the bar then shows a brief
 * polite notice naming the refused containers, cleared after a few seconds or by the next edit.
 *
 * The zoom cluster also carries the grid toggle (Cmd/Ctrl+'), as editor-shell.md places it: it starts as the diagram
 * theme's `grid.visible` says and, while on, draws the grid and makes drags and resizes snap to its spacing.
 *
 * Cmd/Ctrl+K opens the command palette (editor/CommandPalette.tsx, checklist item 23) from anywhere in the page, a
 * read-only preview included, where it lists only what a preview can do. It lists the registry (editor/actions.ts):
 * the document editor's history, the canvas's keyboard commands and this view's own tools, add shape, copy, cut,
 * paste, align, distribute, fit to contents, connector kind, grid and save. Running a row closes the palette and
 * hands focus back to the canvas first, then runs the same action record the shortcut runs; Escape just closes it,
 * focus back on the canvas. The open label editor keeps every key to itself (canvas/LabelEditor.tsx), Cmd/Ctrl+K
 * included, so a label is committed or discarded before the palette can open. Copy, cut and paste are registered
 * here (not the canvas) because they own onClipboard; their palette shortcut never matches a keydown, since the key
 * itself stays on the browser's native clipboard events (DiagramCanvas.tsx module comment), and a palette run instead
 * calls onClipboard with no DataTransfer, the same async-clipboard fallback a keydown uses when no native event
 * follows it.
 *
 * The menu button at the left of the bar (editor/EditorMenu.tsx, editor-shell.md) lists the same registry under File,
 * Edit, View, Arrange and Help, so every canvas action is reachable from a visible menu (checklist item 28); running an
 * item hands focus back to the canvas first, as the palette does. Help opens the palette. F6 and Shift+F6 move focus
 * between the shell's regions in the atlas's order (top bar, tool rail, shape panel while open, canvas, properties
 * panel), landing on a region's first control, or on the region itself when none can take focus, and Escape from any region
 * but the canvas hands focus back to it. Arrow keys move between the rail's buttons, along a row of the properties
 * panel, and through the shape panel's grid, while Tab still moves between controls. Undo and redo sit at the foot of
 * the rail, the history actions' own records, kept focusable while they cannot run so a press never strands focus.
 *
 * Export PNG and Export SVG sit in the File menu and the palette (viewer.exportPng/viewer.exportSvg): each re-renders
 * the document through the same DiagramSvg the canvas shows, cropped to its content on the export surface and named
 * after the title (editor/exportDiagram.tsx), so a download carries the active theme and never the editor's grid or
 * selection chrome. They work in a read-only preview too. Export PDF is the same drawing on a page-sized layout handed to the
 * browser's print dialog (editor/printExport.ts), so it needs nothing on the server; when the server's optional
 * headless-Chromium renderer (ExportConfig.Chromium) has proved itself, the dialog offers its file as the primary route.
 * Export Mermaid
 * (viewer.exportMermaid, design/atlas/mermaid-export.md) copies the diagram as Mermaid text for a README, and shows the
 * text in a dialog when the browser refuses the clipboard.
 *
 * Copy, cut, paste and duplicate (checklist items 18, 19 and 21) go through the tab's shared clipboard store, which
 * writes the system clipboard through the browser's copy and cut events so a copy pastes into another open document,
 * and reads it back from the browser's paste event. A duplicate lands one grid step (the effective theme's, document
 * overrides included) down and right of what it duplicates. A paste lands one grid step from where it was copied, and
 * each repeat of the same payload one step further on (the third paste three steps), until a copy or cut here or a
 * different payload starts again at one. Either becomes the selection; a paste of anything that is not a Draughtsman
 * selection does nothing. A copy that carries no nodes (only edges selected) leaves the clipboard untouched, but a cut
 * still writes it, so the paste after an edges-only cut adds nothing rather than older, unrelated content.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent, type DragEvent as ReactDragEvent, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { useCurrentUser } from '../app/currentUser.ts';
import { useCompactNav } from '../app/useCompactNav.ts';
import { guardedLeave, navigate } from '../app/router.ts';
import { DIAGRAM_TEMPLATES } from '../app/diagramTemplates.ts';
import { DIAGRAM_TYPES, createAndOpen } from '../app/newDocument.ts';
import { DiagramCanvas, type CanvasTool, type DiagramCanvasHandle, type ViewportChangeCause } from '../canvas/DiagramCanvas.tsx';
import { indexDocument } from '../canvas/documentIndex.ts';
import { mayHaveContainers, shotsFromContainers } from '../canvas/flythroughGenerate.ts';
import { createCanvasMeasurer, sharedDomLineBreaker } from '../canvas/text.ts';
import type { Viewport } from '../canvas/viewport.ts';
import { MIN_ALIGN_NODES, MIN_DISTRIBUTE_NODES, type AlignCommand, type DistributeCommand } from '../canvas/align.ts';
import { dispatchShortcut, orderActions, type EditorAction } from '../editor/actions.ts';
import { documentToSvg, downloadBlob, exportFileName, svgToPngBlob } from '../editor/exportDiagram.tsx';
import { AiPanel } from '../editor/AiPanel.tsx';
import { flythroughToHtml } from '../editor/exportFlythrough.tsx';
import { FlythroughPanel } from '../editor/FlythroughPanel.tsx';
import { PresentationStage } from '../editor/PresentationStage.tsx';
import { DslPanel } from '../editor/DslPanel.tsx';
import { loadTextPanePrefs, saveTextPanePrefs } from '../editor/textPanePrefs.ts';
import { HistoryPanel } from '../editor/HistoryPanel.tsx';
import { ReviewPanel } from '../editor/ReviewPanel.tsx';
import type { CompareMarks } from '../model/compare.ts';
import { ImportDrawioDialog } from '../editor/ImportDrawioDialog.tsx';
import { ImportMermaidDialog } from '../editor/ImportMermaidDialog.tsx';
import { ImportVisioDialog } from '../editor/ImportVisioDialog.tsx';
import { LeaveDialog } from '../editor/LeaveDialog.tsx';
import { LocalCopyBar, SavedCopyNotice, StaleSaveBar } from '../editor/SaveRecoveryBars.tsx';
import { useLeaveGuard } from '../editor/useLeaveGuard.ts';
import { ExportPdfDialog } from '../editor/ExportPdfDialog.tsx';
import { pdfProblemOf, type PdfExportOptions, type PdfProblem } from '../editor/pdfExport.ts';
import { openPrintTarget, printPageHtml } from '../editor/printExport.ts';
import { withEmbeddedFonts } from '../editor/exportFonts.ts';
import { ImportSqlDialog } from '../editor/ImportSqlDialog.tsx';
import { MermaidTextDialog } from '../editor/MermaidTextDialog.tsx';
import { exportDsl } from '../model/dslExport.ts';
import { copyMermaid, exportMermaidDetailed } from '../model/mermaidExport.ts';
import { SQL_DIALECTS, exportSql, hasEntities, type SqlDialectChoice } from '../model/sqlImport.ts';
import { ConnectorPanel } from '../editor/ConnectorPanel.tsx';
import { EditorMenu } from '../editor/EditorMenu.tsx';
import { arrowFocusHandler, focusableIn, regionStep } from '../editor/focusNavigation.ts';
import { menuSections } from '../editor/menus.ts';
import { isCommandPalette, isConnectSelected, isGridToggle, isPresent, isShapePanelToggle, isShortcutSheet, isTextField, regionShortcut, toolShortcut, type ClipboardCommand, type GroupCommand } from '../editor/shortcuts.ts';
import { ArrangePanel } from '../editor/ArrangePanel.tsx';
import { StylePanel } from '../editor/StylePanel.tsx';
import { CommandPalette } from '../editor/CommandPalette.tsx';
import { EmptyCanvasHint } from '../editor/EmptyCanvasHint.tsx';
import { ShortcutSheet } from '../editor/ShortcutSheet.tsx';
import { ContainerPanel } from '../editor/ContainerPanel.tsx';
import { ShapePanel, type PanelCommand } from '../editor/ShapePanel.tsx';
import { sessionClipboard } from '../editor/systemClipboard.ts';
import { appliedNotice, conflictNotice } from '../editor/remoteNotice.ts';
import { CONFLICT_MESSAGE, useDocumentEditor, type EdgeStyleField, type SaveStatus } from '../editor/useDocumentEditor.ts';
import type { StylableField } from '../editor/styleState.ts';
import { copy } from '../model/clipboard.ts';
import { SaveConflict } from '../model/documentClient.ts';
import { ACCEPTED_IMAGE_TYPES, createImageNode, fitImageSize, imageDimensions, preloadAssetDataUrls, uploadAsset } from '../model/assets.ts';
import { createNode, DEFAULT_NODE, type NodeTemplate } from '../model/create.ts';
import { edgeKindOptions } from '../model/edgeKinds.ts';
import { addKeyframe, hasKeyframes } from '../model/flythrough.ts';
import { canFitContents, containerPadding, GROUP_TEMPLATE } from '../model/grouping.ts';
import { isPool, LANE_KIND, POOL_KIND, poolOrientation, poolWithLanes, type SwimlaneOrientation } from '../model/swimlanes.ts';
import type { LayoutAlgorithm } from '../model/layoutClient.ts';
import { defaultDiagramThemeName, diagramThemeNames, diagramThemes, type DiagramThemeName } from '../generated/diagram-themes.ts';
import type { Connection } from '../model/connect.ts';
import { createParticipant } from '../model/sequenceEdit.ts';
import * as sequenceFragments from '../model/sequenceFragments.ts';
import { typeCapabilities } from '../model/typeCapabilities.ts';
import type { Document, EdgeStyle, Flythrough, Id, Node as DocNode, Point } from '../model/types.ts';
import { applyDocumentOverrides, effectiveTheme } from '../theme/resolveStyle.ts';
import { applyDiagramOverride } from '../theme/orgTheme.ts';
import type { PublicThemes } from '../model/themesClient.ts';
import { platformShortcut } from '../editor/platform.ts';

export interface DocumentViewerProps {
  document: Document;
  /** Present on the document route, where the view edits and saves; absent on read-only previews. */
  id?: string;
  etag?: string | undefined;
  /**
   * Poll the stored document this often and merge another writer's change into the open one (checklist item 30); the
   * document route passes LIVE_SYNC_INTERVAL_MS. Off when absent.
   */
  liveSyncIntervalMs?: number | undefined;
  /**
   * Save this long after the last change and on leaving, with a retry when a save fails and a question when it is refused
   * as stale (design/atlas/editor-safety.md, "Autosave"); the document route passes AUTOSAVE_DELAY_MS. Off when absent.
   */
  autosaveDelayMs?: number | undefined;
  /**
   * Opens the title already in edit mode with its text selected, so a diagram just created from New diagram can be
   * named straight away (design/atlas/document-list.md). Read once, on mount; DocumentPage passes it from
   * `consumePendingTitleEdit`.
   */
  initialTitleEditActive?: boolean;
  /** The right-hand panel to open on: the home screen's "From text" card arrives on the AI panel. */
  initialPanel?: 'ai' | undefined;
  /**
   * The organisation's themes and its default (GET /api/themes). The Theme menu lists them after the built-in diagram
   * themes, and the default is what the viewer opens in until the user picks another. Absent on read-only previews and
   * while the read is in flight, where the shipped default applies.
   */
  orgThemes?: PublicThemes | undefined;
}

const NOTICE_MS = 4000;

/** The Theme menu value for an organisation theme; built-in themes use their own name. */
const ORG_THEME_PREFIX = 'org:';

/** The notice for containers ungroup left in place because they have connections, naming each by its label. */
function hasConnectionsNotice(labels: string[]): string {
  return `Couldn’t ungroup ${labels.join(', ')}: ${labels.length === 1 ? 'it has' : 'they have'} connections. Remove them first.`;
}

function isThemeName(value: string): value is DiagramThemeName {
  return (diagramThemeNames as readonly string[]).includes(value);
}

function orgThemeKey(id: string): string {
  return `${ORG_THEME_PREFIX}${id}`;
}

function percent(zoom: number): string {
  return `${String(Math.round(zoom * 100))}%`;
}

function statusLabel(status: SaveStatus, retrying: boolean): string {
  switch (status) {
    case 'saving':
      return 'Saving…';
    case 'saved':
      return 'Saved';
    case 'unsaved':
      return 'Unsaved changes';
    case 'error':
      return retrying ? 'Not saved, retrying…' : 'Not saved';
    case 'conflict':
      return 'Out of date';
  }
}

/** Lucide icons (ISC licence), inlined as the Add shape icon is: mouse-pointer-2 and spline. */
const TOOL_ICONS: Record<CanvasTool, ReactNode> = {
  select: <path d="M4.037 4.688a.495.495 0 0 1 .651-.651l16 6.5a.5.5 0 0 1-.063.947l-6.124 1.58a2 2 0 0 0-1.438 1.435l-1.579 6.126a.5.5 0 0 1-.947.063z" />,
  connector: (
    <>
      <circle cx="19" cy="5" r="2" />
      <circle cx="5" cy="19" r="2" />
      <path d="M5 17A12 12 0 0 1 17 5" />
    </>
  ),
};

const TOOL_NAMES: Record<CanvasTool, { name: string; key: string }> = {
  select: { name: 'Select', key: 'V' },
  connector: { name: 'Connector', key: 'C' },
};

const ALIGN_ACTIONS: readonly { command: AlignCommand; label: string }[] = [
  { command: 'left', label: 'Align left' },
  { command: 'centre', label: 'Align centre' },
  { command: 'right', label: 'Align right' },
  { command: 'top', label: 'Align top' },
  { command: 'middle', label: 'Align middle' },
  { command: 'bottom', label: 'Align bottom' },
];

const DISTRIBUTE_ACTIONS: readonly { command: DistributeCommand; label: string }[] = [
  { command: 'horizontal', label: 'Distribute horizontally' },
  { command: 'vertical', label: 'Distribute vertically' },
];

/** Tidy's layout algorithms, lower-case as POST /api/layout documents them (docs/api.md). */
const LAYOUT_ALGORITHMS: readonly { algorithm: LayoutAlgorithm; label: string }[] = [
  { algorithm: 'layered', label: 'Layered' },
  { algorithm: 'stress', label: 'Stress' },
  { algorithm: 'tree', label: 'Tree' },
];

const railKeys = arrowFocusHandler({ kind: 'vertical' });
const panelRowKeys = arrowFocusHandler({ kind: 'horizontal' }, 'button', '[role="group"]');

/** Lucide undo-2 and redo-2 (ISC licence). */
const HISTORY_ICONS: Record<'undo' | 'redo', ReactNode> = {
  undo: (
    <>
      <path d="M9 14 4 9l5-5" />
      <path d="M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11" />
    </>
  ),
  redo: (
    <>
      <path d="m15 14 5-5-5-5" />
      <path d="M20 9H9.5A5.5 5.5 0 0 0 4 14.5A5.5 5.5 0 0 0 9.5 20H13" />
    </>
  ),
};

export function DocumentViewer({ document: initial, id, etag, liveSyncIntervalMs, autosaveDelayMs, initialTitleEditActive, initialPanel, orgThemes }: DocumentViewerProps) {
  const { document, moveNodes, moveEnd, moveCancel, insertMessage, addParticipant, addNodeGroup, reorderElement, wrapInFragment, addFragmentOperand, addNote, toggleActivation, setFragmentGuard, setFragmentOperator, reparentNodes, fitToContents, addLane, removeLane, moveLane, setPoolOrientation, group, ungroup, addNode, deleteElements, pasteElements, duplicateElements, setLabel, setClassMembers, setEdgeKind, setEdgeEnds, setNodeStyle, setEdgeStyle, setNodeProps, setEdgeRoutingMode, resetRoute, editFlythrough, connect, connectSelected, reconnect, align, distribute, arrange, tidy, applyTextDocument, mergeTextDocument, takeTextConflicts, actions: editorActions, save, flush, renameTitle, saveStatus, saveError, retrying, stale, reloadTheirs, restoreVersion, keepMine, saveMineAsNew, localCopies, saveCopyAsNew, discardLocalCopy, remote, keepLocal, takeTheirs } =
    useDocumentEditor(id, initial, etag, { ...(liveSyncIntervalMs === undefined ? {} : { liveSyncIntervalMs }), ...(autosaveDelayMs === undefined ? {} : { autosaveDelayMs }) });
  // Unsaved edits are never lost by leaving: the tab prompts itself, in-app navigation asks (design/atlas/editor-safety.md).
  // With autosave on, leaving with edits the save has not caught up with saves them first and asks only if that fails.
  const leaveGuard = useLeaveGuard(id !== undefined && saveStatus !== 'saved', flush, autosaveDelayMs !== undefined && (saveStatus === 'unsaved' || saveStatus === 'saving'));
  // Local copies stored as documents of their own: which one just was, and whether the store is under way or failed.
  const [savedCopyId, setSavedCopyId] = useState<string | undefined>(undefined);
  const [copyBusy, setCopyBusy] = useState(false);
  const [copyError, setCopyError] = useState<string | undefined>(undefined);
  const storeCopyAsNew = useCallback(
    (copyId: string): void => {
      setCopyBusy(true);
      setCopyError(undefined);
      saveCopyAsNew(copyId).then(
        (created) => {
          setCopyBusy(false);
          setSavedCopyId(created);
        },
        (cause: unknown) => {
          setCopyBusy(false);
          setCopyError(cause instanceof Error ? cause.message : 'Saving the copy failed.');
        },
      );
    },
    [saveCopyAsNew],
  );
  const newestCopy = localCopies.at(-1);
  const storeMineAsNew = useCallback((): void => {
    void saveMineAsNew().then((created) => {
      if (created !== undefined) {
        setSavedCopyId(created);
      }
    });
  }, [saveMineAsNew]);
  const editable = id !== undefined;
  // A sequence diagram's positions are derived from its message and participant order (model/sequenceScene.ts): the
  // canvas edits it, but only through order (design/atlas/sequence-editor.md), and gates every geometry gesture itself.
  const derived = typeCapabilities(document.type).derivedGeometry;
  const canvasEditable = editable;
  // The title (design/atlas/editor-shell.md: "document title (click to rename)"). Renaming is composed independently
  // of the canvas's own save/undo cycle (useDocumentEditor.ts renameTitle), so it can never lose or undo canvas work.
  const [editingTitle, setEditingTitle] = useState(() => editable && initialTitleEditActive === true);
  const [titleDraft, setTitleDraft] = useState(document.title);
  const [titleSaving, setTitleSaving] = useState(false);
  const [titleError, setTitleError] = useState<string | undefined>(undefined);
  const titleInput = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (editingTitle) {
      titleInput.current?.focus();
      titleInput.current?.select();
    }
  }, [editingTitle]);
  // Enter and blur both call commitTitleEdit (Enter-then-blur fires both in the same gesture); this ref
  // makes the commit idempotent per edit so only one PATCH goes out and a still-pending rename never
  // reads as a conflict against itself.
  const titleCommitInFlight = useRef(false);
  const beginTitleEdit = useCallback(() => {
    titleCommitInFlight.current = false;
    setTitleDraft(document.title);
    setTitleError(undefined);
    setEditingTitle(true);
  }, [document.title]);
  const cancelTitleEdit = useCallback(() => {
    titleCommitInFlight.current = false;
    setEditingTitle(false);
    setTitleError(undefined);
  }, []);
  const commitTitleEdit = useCallback(() => {
    if (titleCommitInFlight.current) return;
    const trimmed = titleDraft.trim();
    if (trimmed.length === 0 || trimmed === document.title) {
      setEditingTitle(false);
      setTitleError(undefined);
      return;
    }
    titleCommitInFlight.current = true;
    setTitleSaving(true);
    setTitleError(undefined);
    void renameTitle(trimmed)
      .then(() => {
        titleCommitInFlight.current = false;
        setTitleSaving(false);
        setEditingTitle(false);
      })
      .catch((cause: unknown) => {
        titleCommitInFlight.current = false;
        setTitleSaving(false);
        setEditingTitle(false);
        setTitleError(cause instanceof SaveConflict ? CONFLICT_MESSAGE : cause instanceof Error ? cause.message : 'Renaming failed.');
      });
  }, [titleDraft, document.title, renameTitle]);
  const onTitleKeyDown = useCallback(
    (e: ReactKeyboardEvent<HTMLInputElement>) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        commitTitleEdit();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        cancelTitleEdit();
      }
    },
    [commitTitleEdit, cancelTitleEdit],
  );
  // Which view the right-hand panel shows: Properties, the DSL text, the AI panel, the Tour (flythrough) panel, the version History or the Review (critique).
  // The Text pane's open state is remembered for this viewer (editor/textPanePrefs.ts); a panel asked for by name wins.
  const [restoredText] = useState(() => initialPanel === undefined && id !== undefined && loadTextPanePrefs().open);
  const [panelView, setPanelView] = useState<'properties' | 'text' | 'ai' | 'tour' | 'history' | 'review'>(initialPanel ?? (restoredText ? 'text' : 'properties'));
  // On a narrow window (QA S3: 270 px of panel left a 768 px editor 450 px of canvas, and a phone none) the panel is a sheet that
  // opens over the canvas on request, closed to begin with; choosing a view from the palette or the menu opens it.
  const compactLayout = useCompactNav();
  const [panelOpen, setPanelOpen] = useState(false);
  const panelHidden = compactLayout && !panelOpen;
  const lastPanelView = useRef(panelView);
  useEffect(() => {
    if (lastPanelView.current !== panelView) {
      lastPanelView.current = panelView;
      setPanelOpen(true);
      if (id !== undefined) saveTextPanePrefs({ open: panelView === 'text' });
    }
  }, [panelView, id]);
  const textOpen = panelView === 'text';
  const toggleText = useCallback(() => { setPanelView((view) => (view === 'text' ? 'properties' : 'text')); }, []);
  const aiOpen = panelView === 'ai';
  const tourOpen = panelView === 'tour';
  const historyOpen = panelView === 'history';
  const reviewOpen = panelView === 'review';
  const [textVisited, setTextVisited] = useState(false);
  if (textOpen && !textVisited) setTextVisited(true);
  const [aiVisited, setAiVisited] = useState(false);
  if (aiOpen && !aiVisited) setAiVisited(true);
  const [tourVisited, setTourVisited] = useState(false);
  if (tourOpen && !tourVisited) setTourVisited(true);
  const [historyVisited, setHistoryVisited] = useState(false);
  if (historyOpen && !historyVisited) setHistoryVisited(true);
  const [reviewVisited, setReviewVisited] = useState(false);
  if (reviewOpen && !reviewVisited) setReviewVisited(true);
  // What a version comparison in the History panel marks on the canvas: view state, never part of the document.
  const [compareMarks, setCompareMarks] = useState<CompareMarks | undefined>(undefined);
  // The Present overlay (editor/PresentationStage.tsx) plays a snapshot of the document taken when it opened, so another
  // writer's live-synced change never moves a show under way.
  const [presenting, setPresenting] = useState<{ document: Document; flythrough: Flythrough; startIndex: number } | undefined>(undefined);
  const [tool, setTool] = useState<CanvasTool>('select');
  // The payload text this viewer last pasted and how many times in a row, so a repeat steps on from the one before.
  const pasted = useRef<{ text: string; count: number } | undefined>(undefined);
  // The canvas owns the selection; this copy only feeds the properties panel.
  const [selection, setSelection] = useState<ReadonlySet<Id>>(() => new Set());
  // Counted against the document, so an id the selection still holds after an undo removed its element counts for nothing.
  const { nodeCount, elementCount } = useMemo(() => {
    const nodeIds = new Set(document.nodes.map((node) => node.id));
    const edgeIds = new Set(document.edges.map((edge) => edge.id));
    const nodes = [...selection].filter((selected) => nodeIds.has(selected)).length;
    return { nodeCount: nodes, elementCount: nodes + [...selection].filter((selected) => edgeIds.has(selected)).length };
  }, [document, selection]);
  // Looked up in the document, so an id the selection still holds after an undo removed its edge shows nothing.
  const selectedEdge = useMemo(() => {
    const [only] = selection;
    return selection.size === 1 && only !== undefined ? document.edges.find((edge) => edge.id === only) : undefined;
  }, [document, selection]);
  // The selected nodes and edges the styling panel acts on, looked up so a stale id shows nothing.
  const selectedNodes = useMemo(() => document.nodes.filter((node) => selection.has(node.id)), [document, selection]);
  const selectedEdges = useMemo(() => document.edges.filter((edge) => selection.has(edge.id)), [document, selection]);
  // Undefined until the user picks a theme, so the organisation's default (which may arrive after the editor opens) applies until then.
  const [themeChoice, setThemeChoice] = useState<string | undefined>(undefined);
  const orgDefaultKey = orgThemes?.defaultId != null && orgThemes.themes.some((t) => t.id === orgThemes.defaultId) ? orgThemeKey(orgThemes.defaultId) : undefined;
  const themeKey = themeChoice ?? orgDefaultKey ?? defaultDiagramThemeName;
  const [zoom, setZoom] = useState(1);
  // Export PDF is always offered (browser print needs nothing on the server). The Chromium component the health endpoint
  // reports (build-spec-v0.1.md section 7) only decides whether the dialog also offers the server's file, as its primary route.
  const [pdfAvailable, setPdfAvailable] = useState<boolean | undefined>(undefined);
  // The server's own install instructions while PDF is not available (health's `export.pdfHint`), and the dialog they appear in.
  const [pdfHint, setPdfHint] = useState<string | undefined>(undefined);
  const [pdfProblem, setPdfProblem] = useState<PdfProblem | undefined>(undefined);
  const [pdfDialogOpen, setPdfDialogOpen] = useState(false);
  // The AI panel (design/atlas/ai-panel.md) is hidden entirely, not just disabled, while no provider is configured.
  const [aiAvailable, setAiAvailable] = useState(false);
  // The Import SQL paste box (editor/ImportSqlDialog.tsx): it makes a new document, so it never touches this one.
  const [sqlImportOpen, setSqlImportOpen] = useState(false);
  const [mermaidImportOpen, setMermaidImportOpen] = useState(false);
  // Import draw.io (editor/ImportDrawioDialog.tsx): like the two above it makes a new document and never touches this one.
  const [drawioImportOpen, setDrawioImportOpen] = useState(false);
  // Import Visio (editor/ImportVisioDialog.tsx): the same, for a .vsdx.
  const [visioImportOpen, setVisioImportOpen] = useState(false);
  // Export Mermaid's fallback (editor/MermaidTextDialog.tsx): the text, shown when the browser refused the clipboard.
  const [mermaidText, setMermaidText] = useState<{ text: string; notes: string[]; intro?: string } | undefined>(undefined);
  // The readout follows every frame, but only fit, 100% and the zoom steps reach the live region: a wheel or pinch
  // would otherwise narrate dozens of percentages per gesture.
  const [announced, setAnnounced] = useState('');
  // A notice belongs to the document it was raised on, so the next edit clears it. It is raised unbound and bound to
  // the document of the render that shows it, which already includes any write made alongside it. Declared here,
  // ahead of the image callbacks below, because they raise one on a failed upload or export.
  const [notice, setNotice] = useState<{ text: string; on?: Document }>();
  const canvas = useRef<DiagramCanvasHandle>(null);
  // Selects elements a History or Review entry names and centres the first drawn one: a node itself, or the start of a connector.
  const latestDocument = useRef(document);
  useEffect(() => {
    latestDocument.current = document;
  });
  const selectElements = useCallback((ids: readonly Id[]): void => {
    const handle = canvas.current;
    if (handle === null || ids.length === 0) {
      return;
    }
    handle.select(new Set(ids));
    const current = latestDocument.current;
    const target = ids.find((id) => current.nodes.some((node) => node.id === id)) ?? current.edges.find((edge) => ids.includes(edge.id))?.from.node;
    if (target !== undefined) {
      handle.reveal(target);
    }
  }, []);
  const revealElement = useCallback((id: Id): void => { selectElements([id]); }, [selectElements]);
  // The hidden file input the Image glyph, a dropped file, a pasted image and "Replace image" all drive, and the
  // pending request remembering where the picture should land and which node it replaces (no point means view centre).
  const imageInput = useRef<HTMLInputElement>(null);
  const imageRequest = useRef<{ point: Point | undefined; target: Id | undefined } | undefined>(undefined);
  // State rather than a ref, so the region actions built during render can close over it.
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const measure = useMemo(() => createCanvasMeasurer(), []);
  // Node labels break where the label editor breaks them (checklist item 15).
  const breakLines = useMemo(() => sharedDomLineBreaker(), []);
  // Whether the server's optional Chromium PDF component is present, so the dialog can offer its file beside browser print.
  useEffect(() => {
    if (!editable) {
      return;
    }
    const controller = new AbortController();
    fetch('/api/health', { signal: controller.signal, headers: { Accept: 'application/json' } })
      .then(async (response) => {
        if (!response.ok) {
          return;
        }
        const body = (await response.json()) as { export?: { pdf?: boolean; pdfHint?: string; pdfProblem?: string }; ai?: { enabled?: boolean } };
        setPdfAvailable(body.export === undefined ? undefined : body.export.pdf === true);
        setPdfHint(body.export?.pdfHint);
        setPdfProblem(pdfProblemOf(body.export?.pdfProblem));
        setAiAvailable(body.ai?.enabled === true);
      })
      .catch(() => {
        // The signal is a nicety for the menu; a failure leaves PDF export unoffered.
      });
    return () => {
      controller.abort();
    };
  }, [editable]);
  // The Help menu's Documentation entry (docs/api.md "## About and status"): present only when the instance configures a docs URL.
  const [docsUrl, setDocsUrl] = useState<string | undefined>(undefined);
  // The sheet lists a snapshot of the registry taken when it opens, as the palette does.
  const [shortcutsSheet, setShortcutsSheet] = useState<readonly EditorAction[] | undefined>(undefined);
  const shortcutsOpen = shortcutsSheet !== undefined;
  const openShortcuts = useRef<() => void>(() => undefined);
  const currentUser = useCurrentUser();
  useEffect(() => {
    if (!editable) {
      return;
    }
    const controller = new AbortController();
    try {
      fetch('/api/about', { signal: controller.signal, headers: { Accept: 'application/json' } })
        .then(async (response) => {
          if (!response.ok) {
            return;
          }
          const body = (await response.json()) as { docsUrl?: unknown };
          setDocsUrl(typeof body.docsUrl === 'string' ? body.docsUrl : undefined);
        })
        .catch(() => {
          // A nicety for the menu: without it Help simply has no Documentation entry.
        });
    } catch {
      // A fetch that throws synchronously (no network layer at all) is the same: no Documentation entry.
    }
    return () => {
      controller.abort();
    };
  }, [editable]);
  // The PDF arrives from the dialog, which has already asked the server with the chosen page options (editor/pdfExport.ts).
  const downloadPdf = useCallback(
    (blob: Blob) => {
      downloadBlob(exportFileName(document.title, 'pdf'), blob);
      setPdfDialogOpen(false);
    },
    [document.title],
  );
  // Export SQL writes the diagram's tables as CREATE TABLE DDL for one dialect (docs/api.md POST /api/export/sql).
  const downloadSql = useCallback(
    (dialect: Exclude<SqlDialectChoice, 'auto'>) => {
      exportSql(document, dialect)
        .then((sql) => {
          downloadBlob(exportFileName(document.title, 'sql'), new Blob([sql], { type: 'text/plain;charset=utf-8' }));
        })
        .catch((error: unknown) => {
          setNotice({ text: error instanceof Error ? `Couldn’t export the SQL: ${error.message}` : 'Couldn’t export the SQL.' });
        });
    },
    [document],
  );
  // Export Mermaid copies the diagram as Mermaid text for a README (docs/api.md POST /api/export/mermaid); a clipboard the
  // browser refuses shows the text in a dialog instead, so the export is never lost.
  const copyAsMermaid = useCallback(() => {
    copyMermaid(document)
      .then(({ text, copied, warnings, notes }) => {
        if (!copied) {
          setMermaidText({ text, notes });
        } else if (warnings > 0) {
          // Mermaid has no spelling for some of what is drawn (a mixed diagram degrades to a flowchart): say so, since
          // the copy is not the diagram, and list what was simplified (the text carries a `%%` comment beside each).
          setNotice({ text: `Copied as Mermaid, but it is lossy: ${String(warnings)} ${warnings === 1 ? 'thing has' : 'things have'} no Mermaid equivalent and ${warnings === 1 ? 'was' : 'were'} simplified.` });
          setMermaidText({ text, notes, intro: `Copied to the clipboard. Mermaid has no spelling for ${String(warnings)} ${warnings === 1 ? 'thing' : 'things'} in this diagram, so ${warnings === 1 ? 'it was' : 'they were'} simplified:` });
        } else {
          setNotice({ text: 'Copied the diagram as Mermaid.' });
        }
      })
      .catch((error: unknown) => {
        setNotice({ text: error instanceof Error ? `Couldn’t export Mermaid: ${error.message}` : 'Couldn’t export Mermaid.' });
      });
  }, [document]);
  // Export Mermaid saves a `.mmd` file (the clipboard copy is its own command above); what Mermaid could only approximate is
  // listed afterwards, so a lossy export is never silent (QA P8).
  const downloadMermaid = useCallback(() => {
    exportMermaidDetailed(document)
      .then(({ text, warnings, notes }) => {
        const file = exportFileName(document.title, 'mmd');
        downloadBlob(file, new Blob([text], { type: 'text/plain;charset=utf-8' }));
        if (warnings > 0) {
          setMermaidText({ text, notes, intro: `Saved as ${file}. Mermaid has no spelling for ${String(warnings)} ${warnings === 1 ? 'thing' : 'things'} in this diagram, so ${warnings === 1 ? 'it was' : 'they were'} simplified:` });
        } else {
          setNotice({ text: `Saved ${file}.` });
        }
      })
      .catch((error: unknown) => {
        setNotice({ text: error instanceof Error ? `Couldn’t export Mermaid: ${error.message}` : 'Couldn’t export Mermaid.' });
      });
  }, [document]);
  // Export DSL saves the document's own text form (the Text tab's content) as a `.dsl` file.
  const downloadDsl = useCallback(() => {
    exportDsl(document)
      .then((text) => {
        const file = exportFileName(document.title, 'dsl');
        downloadBlob(file, new Blob([text], { type: 'text/plain;charset=utf-8' }));
        setNotice({ text: `Saved ${file}.` });
      })
      .catch((error: unknown) => {
        setNotice({ text: error instanceof Error ? `Couldn’t export the DSL: ${error.message}` : 'Couldn’t export the DSL.' });
      });
  }, [document]);
  const onViewportChange = useCallback((viewport: Viewport, cause: ViewportChangeCause) => {
    setZoom(viewport.zoom);
    if (cause === 'command') {
      setAnnounced(`Zoom ${percent(viewport.zoom)}`);
    }
  }, []);
  // A mouse press on a zoom button hands focus to the canvas instead of the button, so the shortcuts keep working
  // straight after a click. Keyboard activation does not fire mousedown, so tab focus on the buttons is unaffected.
  const focusCanvas = useCallback((e: MouseEvent<HTMLElement>) => {
    e.preventDefault();
    canvas.current?.focus();
  }, []);
  const orgTheme = themeKey.startsWith(ORG_THEME_PREFIX) ? orgThemes?.themes.find((t) => orgThemeKey(t.id) === themeKey) : undefined;
  const theme = useMemo(
    () => (orgTheme === undefined ? diagramThemes[isThemeName(themeKey) ? themeKey : defaultDiagramThemeName] : applyDiagramOverride(orgTheme.diagram, orgTheme.name)),
    [orgTheme, themeKey],
  );
  // "Generate from containers": a scan of the nodes keeps its button current, and the shots (which need an index) are
  // worked out when it is pressed, so dragging a shape with the Tour panel open builds no second index a frame.
  const canGenerateTour = useMemo(() => tourVisited && mayHaveContainers(document), [tourVisited, document]);
  const generateTour = useCallback(() => shotsFromContainers(indexDocument(document)), [document]);
  // Opens Present on the document as it is now, starting at a keyframe; a document with no tour has nothing to play.
  const present = useCallback(
    (startIndex = 0) => {
      if (hasKeyframes(document.flythrough)) {
        setPresenting({ document, flythrough: document.flythrough, startIndex });
      }
    },
    [document],
  );
  // Captures what the canvas shows as the tour's next keyframe, from a menu or the palette rather than the Tour panel.
  const addKeyframeFromView = useCallback(() => {
    const view = canvas.current?.viewRect();
    if (view === undefined) {
      return;
    }
    editFlythrough((current) => addKeyframe(current, view)?.flythrough ?? current);
    setPanelView('tour');
    setNotice({ text: 'Added a fly-through keyframe from the current view.' });
  }, [editFlythrough]);
  const closePresentation = useCallback(() => {
    setPresenting(undefined);
  }, []);
  // The overlay hands focus back to whatever opened it (the canvas, or the Tour panel's Present button); when that left
  // nothing focused, the canvas takes it, as it does after the palette or a dialog closes.
  const wasPresenting = useRef(false);
  useEffect(() => {
    if (presenting !== undefined) {
      wasPresenting.current = true;
      return;
    }
    if (wasPresenting.current) {
      wasPresenting.current = false;
      const active = globalThis.document.activeElement;
      if (active === null || active === globalThis.document.body) {
        canvas.current?.focus({ visible: true });
      }
    }
  }, [presenting]);
  // Undefined until the user flips it, so the grid follows the theme (and the document's override of it) until then.
  const [gridChoice, setGridChoice] = useState<boolean | undefined>(undefined);
  const gridOn = gridChoice ?? applyDocumentOverrides(theme, document.theme).grid.visible;
  const toggleGrid = useCallback(() => {
    setGridChoice(!gridOn);
  }, [gridOn]);
  // A palette-placed node is added as it is, except a pool, which arrives with its lanes as one step (model/swimlanes.ts).
  const addPlaced = useCallback(
    (node: DocNode) => {
      if (isPool(node)) {
        addNodeGroup(poolWithLanes(document, node));
      } else {
        addNode(node);
      }
    },
    [document, addNode, addNodeGroup],
  );
  // The single place a template becomes a node on the canvas: the rail's "Add shape" and every shape panel glyph
  // (click) go through this, so they can never place differently.
  const placeShape = useCallback(
    (template: NodeTemplate) => {
      const handle = canvas.current;
      if (!handle) {
        return;
      }
      if (derived) {
        // A lifeline goes at the right-hand end and asks for its name: there is no point on the canvas to place it at.
        const participant = createParticipant(document, template);
        addNode(participant);
        handle.select(new Set([participant.id]));
        handle.edit(participant.id);
        return;
      }
      const node = createNode(document, handle.viewCentre(), theme.grid.size, undefined, template);
      addPlaced(node);
      handle.select(new Set([node.id]));
    },
    [document, theme, addNode, addPlaced, derived],
  );
  const addShape = useCallback(() => {
    placeShape(DEFAULT_NODE[document.type]);
  }, [placeShape, document.type]);
  // A shape chosen in the panel (click or keyboard) arms here instead of placing immediately; the next pointerdown
  // on the canvas surface (onCanvasPointerDownCapture below) places it at that point and disarms.
  const [armedShape, setArmedShape] = useState<NodeTemplate | undefined>(undefined);
  // A palette click arms a shape for the next canvas press; a sequence participant needs no point, so it is placed at once.
  const armShape = useCallback(
    (template: NodeTemplate) => {
      if (derived) {
        placeShape(template);
      } else {
        setArmedShape(template);
      }
    },
    [derived, placeShape],
  );
  const onCanvasPointerDownCapture = useCallback(
    (e: ReactPointerEvent<HTMLDivElement>) => {
      if (armedShape === undefined) {
        return;
      }
      if ((e.target as HTMLElement).closest('.viewer-zoom-cluster') !== null) {
        return;
      }
      const handle = canvas.current;
      if (!handle) {
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      const point = handle.pointToWorld({ x: e.clientX, y: e.clientY });
      const node = createNode(document, point, theme.grid.size, undefined, armedShape);
      addPlaced(node);
      handle.select(new Set([node.id]));
      setArmedShape(undefined);
    },
    [armedShape, document, theme, addPlaced],
  );
  // One style property across the whole selection, as one undo step, from the styling panel.
  const onStyle = useCallback(
    (field: StylableField, value: string | number | boolean | undefined, target: 'node' | 'edge') => {
      if (target === 'node') {
        setNodeStyle(selection, field, value);
      } else {
        setEdgeStyle(selection, field as EdgeStyleField, value as EdgeStyle[EdgeStyleField] | undefined);
      }
    },
    [selection, setNodeStyle, setEdgeStyle],
  );
  const onClipboard = useCallback(
    (command: ClipboardCommand, selection: ReadonlySet<Id>, transfer?: DataTransfer) => {
      const step = effectiveTheme(theme, document).grid.size;
      const offset = { x: step, y: step };
      const selectPasted = (ids: ReadonlySet<Id>): void => {
        if (ids.size > 0) {
          canvas.current?.select(ids);
        }
      };
      const copySelection = (evenEmpty: boolean): void => {
        const payload = copy(document, selection);
        if (evenEmpty || payload.nodes.length > 0) {
          pasted.current = undefined;
          void sessionClipboard.write(payload, transfer);
        }
      };
      switch (command) {
        case 'copy':
          copySelection(false);
          break;
        case 'cut':
          copySelection(true);
          deleteElements(selection);
          canvas.current?.select(new Set<Id>());
          break;
        case 'paste':
          void sessionClipboard.read(transfer?.getData('text/plain')).then((payload) => {
            if (payload !== undefined) {
              const text = JSON.stringify(payload);
              const count = pasted.current?.text === text ? pasted.current.count + 1 : 1;
              pasted.current = { text, count };
              selectPasted(pasteElements(payload, { x: step * count, y: step * count }));
            }
          });
          break;
        case 'duplicate':
          selectPasted(duplicateElements(selection, offset));
          break;
      }
    },
    [theme, document, deleteElements, pasteElements, duplicateElements],
  );
  // The new edge is selected, as a new shape is, so Enter labels it and the properties panel shows its kind.
  const connectAndSelect = useCallback(
    (connection: Connection) => {
      const edgeId = connect(connection);
      canvas.current?.select(new Set([edgeId]));
    },
    [connect],
  );
  // A message made with the connector tool: inserted at the slot the release picked, selected and opened for its label.
  const insertMessageAndEdit = useCallback(
    ({ from, to, index }: { from: Id; to: Id; index: number }) => {
      const messageId = insertMessage(from, to, index);
      canvas.current?.select(new Set([messageId]));
      canvas.current?.edit(messageId);
    },
    [insertMessage],
  );
  const addLabel = `Add ${DEFAULT_NODE[document.type].label.toLowerCase()}`;
  const [shapePanelOpen, setShapePanelOpen] = useState(false);
  // Only Escape closes the panel from inside it, so focus goes back with the keyboard's ring.
  const closeShapePanel = useCallback(() => {
    setShapePanelOpen(false);
    setArmedShape(undefined);
    canvas.current?.focus({ visible: true });
  }, []);
  // F6 and Shift+F6 cycle the elements marked data-region, in page order, which is the atlas's order. They land on a
  // region's first control that can take focus (one in a hidden tab cannot), or on the region itself when there is none.
  // The canvas is its region's first control; focus arriving from elsewhere by key has already cleared its pointer
  // mark on blur, so it shows the ring.
  const cycleRegion = useCallback((step: 1 | -1) => {
    const regions = [...(root?.querySelectorAll<HTMLElement>('[data-region]') ?? [])];
    const active = globalThis.document.activeElement;
    const next = regions[regionStep(regions.findIndex((region) => active !== null && region.contains(active)), regions.length, step)];
    if (next !== undefined) {
      (focusableIn(next)[0] ?? next).focus();
    }
  }, [root]);
  // A shape dragged from the panel lands where the pointer released it, tracked as a plain pointer gesture rather
  // than native HTML5 drag-and-drop (M2 final Bug B): a real dragstart depends on the browser's own drag-gesture
  // heuristic, which two independently-built automation tools both found never fired reliably for a fast or
  // synthetic press-drag (4 of 5 drops placed nothing). A glyph's pointerdown (ShapePanel.tsx) instead calls
  // dragShapeStart, and this pointer is tracked with no capture: pointermove/pointerup naturally retarget to
  // whichever element the pointer is currently over, so releasing over the canvas surface fires the handlers below,
  // exactly as it would for any other uncaptured pointer. A release anywhere else (still over the panel, or off both)
  // is cleared by the window fallback with nothing placed.
  const [dragShape, setDragShape] = useState<{ template: NodeTemplate; pointerId: number } | undefined>(undefined);
  const dragShapeStart = useCallback(
    (template: NodeTemplate, pointerId: number) => {
      // A derived layout (sequence) has no point to place at, but the drop's x still says which lifelines it lands between;
      // a plain click on the glyph releases over the panel, never the canvas, and stays the append-at-the-end click.
      setDragShape({ template, pointerId });
    },
    [],
  );
  // Image placement: the picture, not a template, decides the node's size, so the glyph, a drop, a paste and "Replace
  // image" all open the one file picker and create or re-point an image node from what it returns. `target` names the
  // node a replacement re-points; `point` is where a new node lands (the view centre when absent).
  const openImagePicker = useCallback((point: Point | undefined, target?: Id) => {
    imageRequest.current = { point, target };
    const input = imageInput.current;
    if (input) {
      input.value = '';
      input.click();
    }
  }, []);
  const replaceImage = useCallback(
    (nodeId: Id, assetId: Id, fileName: string) => {
      const node = document.nodes.find((candidate) => candidate.id === nodeId);
      if (node === undefined) {
        return;
      }
      setNodeProps(nodeId, { ...(node.props ?? {}), assetId, fileName });
    },
    [document, setNodeProps],
  );
  const placeUploadedImage = useCallback(
    (file: File | Blob, fileName: string, point: Point | undefined, target: Id | undefined) => {
      void (async () => {
        try {
          const [asset, dimensions] = await Promise.all([uploadAsset(file, fileName), imageDimensions(file)]);
          if (target !== undefined) {
            replaceImage(target, asset.id, fileName);
            return;
          }
          const handle = canvas.current;
          const centre = point ?? handle?.viewCentre() ?? { x: 0, y: 0 };
          const node = createImageNode(document, centre, theme.grid.size, asset.id, fitImageSize(dimensions.w, dimensions.h), fileName);
          addNode(node);
          handle?.select(new Set([node.id]));
        } catch (error) {
          setNotice({ text: error instanceof Error ? error.message : 'The image could not be added.' });
        }
      })();
    },
    [document, theme, addNode, replaceImage],
  );
  const onImageInputChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      const request = imageRequest.current;
      imageRequest.current = undefined;
      if (file === undefined) {
        return;
      }
      placeUploadedImage(file, file.name, request?.point, request?.target);
    },
    [placeUploadedImage],
  );
  const onImageDragOver = useCallback((e: ReactDragEvent<HTMLDivElement>) => {
    if (Array.from(e.dataTransfer.types).includes('Files')) {
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
    }
  }, []);
  const onImageDrop = useCallback(
    (e: ReactDragEvent<HTMLDivElement>) => {
      const file = Array.from(e.dataTransfer.files).find((candidate) => candidate.type.startsWith('image/'));
      if (file === undefined) {
        return;
      }
      e.preventDefault();
      const point = canvas.current?.pointToWorld({ x: e.clientX, y: e.clientY });
      placeUploadedImage(file, file.name, point, undefined);
    },
    [placeUploadedImage],
  );
  // Pasting a picture creates an image node at the view centre. The canvas's own paste handler leaves the selection
  // alone and reads text/plain, which an image paste has none of, so the two never contend for the same paste.
  useEffect(() => {
    if (!editable) {
      return;
    }
    const onPaste = (e: ClipboardEvent): void => {
      if (isTextField(e.target)) {
        return;
      }
      const item = Array.from(e.clipboardData?.items ?? []).find((candidate) => candidate.kind === 'file' && candidate.type.startsWith('image/'));
      const file = item?.getAsFile();
      if (file === null || file === undefined) {
        return;
      }
      e.preventDefault();
      placeUploadedImage(file, file.name.length > 0 ? file.name : 'pasted image', undefined, undefined);
    };
    window.addEventListener('paste', onPaste);
    return () => {
      window.removeEventListener('paste', onPaste);
    };
  }, [editable, placeUploadedImage]);
  const onCanvasDragPointerMove = useCallback(
    (e: ReactPointerEvent<HTMLDivElement>) => {
      if (dragShape === undefined) {
        return;
      }
      if (dragShape.pointerId === e.pointerId) {
        e.preventDefault();
      }
    },
    [dragShape],
  );
  const onCanvasDragPointerUp = useCallback(
    (e: ReactPointerEvent<HTMLDivElement>) => {
      if (dragShape === undefined) {
        return;
      }
      if (dragShape.pointerId !== e.pointerId) {
        return;
      }
      const handle = canvas.current;
      if (!handle) {
        return;
      }
      e.preventDefault();
      const point = handle.pointToWorld({ x: e.clientX, y: e.clientY });
      setDragShape(undefined);
      if (dragShape.template.shape === 'image') {
        // Dropping the Image glyph opens the picker at that point rather than placing an empty image node.
        openImagePicker(point);
        return;
      }
      if (derived) {
        // A lifeline has no point of its own: it takes the slot among the others that the drop falls in, and asks for its name.
        const participant = createParticipant(document, dragShape.template);
        addParticipant(participant, point.x);
        handle.select(new Set([participant.id]));
        handle.edit(participant.id);
        return;
      }
      const node = createNode(document, point, theme.grid.size, undefined, dragShape.template);
      addPlaced(node);
      handle.select(new Set([node.id]));
    },
    [dragShape, document, theme, addPlaced, addParticipant, derived, openImagePicker],
  );
  // A release the canvas never saw (still over the panel, or off both) never places anything, but must still clear
  // the armed pointer, or a later, unrelated press with the same reused pointerId (mice keep one for the session)
  // would place a stale shape. A second pointer arriving mid-drag cancels it too, favouring the canvas's own
  // pinch/pen gestures (cd8e731's same rule for its pointer drags).
  useEffect(() => {
    if (dragShape === undefined) {
      return;
    }
    const clearIfMatching = (e: PointerEvent): void => {
      if (e.pointerId === dragShape.pointerId) {
        setDragShape(undefined);
      }
    };
    const cancelOnOtherPointer = (e: PointerEvent): void => {
      if (e.pointerId !== dragShape.pointerId) {
        setDragShape(undefined);
      }
    };
    window.addEventListener('pointerup', clearIfMatching);
    window.addEventListener('pointercancel', clearIfMatching);
    window.addEventListener('pointerdown', cancelOnOtherPointer);
    return () => {
      window.removeEventListener('pointerup', clearIfMatching);
      window.removeEventListener('pointercancel', clearIfMatching);
      window.removeEventListener('pointerdown', cancelOnOtherPointer);
    };
  }, [dragShape]);

  const padding = containerPadding(theme.grid.size);
  // A notice belongs to the document it was raised on, so the next edit clears it. It is raised unbound and bound to the
  // document of the render that shows it, which already includes any write made alongside it.
  if (notice !== undefined && notice.on === undefined) {
    setNotice({ ...notice, on: document });
  }
  // Another writer's change that merged cleanly is announced like any other brief notice; a clash gets its own band.
  // Every merge hands out a new report, so comparing with the last one seen raises the notice once per merge.
  const [announcedRemote, setAnnouncedRemote] = useState(remote);
  if (remote !== announcedRemote) {
    setAnnouncedRemote(remote);
    if (remote?.conflicts.length === 0) {
      setNotice({ text: appliedNotice(remote.by) });
    }
  }
  useEffect(() => {
    if (notice === undefined) {
      return;
    }
    const timer = setTimeout(() => {
      setNotice(undefined);
    }, NOTICE_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [notice]);
  // Group and ungroup return the selection to show next: the new container, or the children it released.
  const onGroup = useCallback(
    (ids: ReadonlySet<Id>, command: GroupCommand): ReadonlySet<Id> | undefined => {
      if (command === 'group') {
        const containerId = group(ids, padding);
        return containerId === undefined ? undefined : new Set([containerId]);
      }
      const result = ungroup(ids);
      if (result === undefined) {
        return undefined;
      }
      if (result.refused.length > 0) {
        const labels = new Map(document.nodes.map((node) => [node.id, node.label]));
        setNotice({ text: hasConnectionsNotice(result.refused.map((id) => labels.get(id) ?? id)) });
      }
      return result.released.size > 0 || result.refused.length === 0 ? new Set([...result.released, ...result.refused]) : undefined;
    },
    [group, ungroup, padding, document],
  );
  const groupFromPanel = useCallback(
    (command: GroupCommand) => {
      const after = onGroup(selection, command);
      if (after !== undefined) {
        canvas.current?.select(after);
      }
    },
    [onGroup, selection],
  );
  const containerState = useMemo(() => {
    const selectedNodes = document.nodes.filter((node) => selection.has(node.id));
    const [only] = selectedNodes;
    const childCount = selectedNodes.length === 1 && only !== undefined ? document.nodes.filter((node) => node.parent === only.id && node.id !== only.id).length : 0;
    return {
      canGroup: selectedNodes.some((node) => node.geometry !== undefined),
      canUngroup: selectedNodes.some((node) => node.kind === GROUP_TEMPLATE.kind),
      canFit: canFitContents(document, selection),
      childCount: only !== undefined && selectedNodes.length === 1 && (only.kind === GROUP_TEMPLATE.kind || childCount > 0) ? childCount : undefined,
    };
  }, [document, selection]);

  const swimlaneState = useMemo(() => {
    const [only] = selectedNodes;
    const pool = selectedNodes.length === 1 && only?.kind === POOL_KIND ? only : undefined;
    const lane = selectedNodes.length === 1 && only?.kind === LANE_KIND ? only : undefined;
    const siblings = lane === undefined ? [] : document.nodes.filter((node) => node.parent === lane.parent && node.kind === LANE_KIND);
    const position = lane === undefined ? -1 : siblings.findIndex((node) => node.id === lane.id);
    return {
      pool,
      lane,
      orientation: pool === undefined ? 'horizontal' : poolOrientation(pool),
      canMoveEarlier: position > 0,
      canMoveLater: position >= 0 && position < siblings.length - 1,
    };
  }, [document, selectedNodes]);
  // The swimlane commands keep the selection unchanged: the lane appears (or leaves) where it belongs, and selecting
  // it would mean reading the canvas ref while building the action records, which the hooks lint forbids.

  // Export re-renders the document through the same DiagramSvg the canvas shows (editor/exportDiagram.tsx), cropped
  // to its content on the export surface, so a download carries the active theme and never the editor's grid or
  // selection. PNG rasterises that SVG; a rasterisation failure is surfaced as a notice rather than a download.
  const exportDiagram = useCallback(
    (format: 'svg' | 'png') => {
      void (async () => {
        // Images export as inlined data URIs so the file is self-contained (model/assets.ts); an asset that cannot be
        // read exports as the placeholder frame rather than failing the whole download.
        const dataUrls = await preloadAssetDataUrls(document);
        const { svg, width, height } = documentToSvg(document, theme, { measure, breakLines, assetHref: (assetId) => dataUrls.get(assetId) });
        if (format === 'svg') {
          downloadBlob(exportFileName(document.title, 'svg'), new Blob([svg], { type: 'image/svg+xml;charset=utf-8' }));
          return;
        }
        const blob = await svgToPngBlob(svg, width, height);
        downloadBlob(exportFileName(document.title, 'png'), blob);
      })().catch(() => {
        setNotice({ text: 'Couldn’t export the diagram.' });
      });
    },
    [document, theme, measure, breakLines],
  );

  // Browser print (editor/printExport.ts): the export SVG, fonts embedded, on a sheet laid out for `options`, handed to the browser's
  // print dialog. The window it prints from is opened before the first await, while the click still counts as a user gesture;
  // a refused window falls back to a frame in this page.
  const printDiagram = useCallback(
    (options: PdfExportOptions): Promise<void> => {
      const target = openPrintTarget();
      return (async () => {
        const dataUrls = await preloadAssetDataUrls(document);
        const { svg, width, height } = documentToSvg(document, theme, { measure, breakLines, assetHref: (assetId) => dataUrls.get(assetId) });
        await target.print(printPageHtml(await withEmbeddedFonts(svg), width, height, options, document.title));
      })().catch(() => {
        target.dispose();
        throw new Error('The print view could not be opened.');
      });
    },
    [document, theme, measure, breakLines],
  );

  // The standalone HTML viewer of the tour (editor/exportFlythrough.tsx): one file that plays with no server.
  const exportFlythrough = useCallback(() => {
    const tour = document.flythrough;
    if (!hasKeyframes(tour)) {
      return;
    }
    void (async () => {
      const dataUrls = await preloadAssetDataUrls(document);
      const html = flythroughToHtml(document, tour, theme, { measure, breakLines, assetHref: (assetId) => dataUrls.get(assetId) });
      downloadBlob(exportFileName(`${document.title} fly-through`, 'html'), new Blob([html], { type: 'text/html;charset=utf-8' }));
    })().catch(() => {
      setNotice({ text: 'Couldn’t export the fly-through.' });
    });
  }, [document, theme, measure, breakLines]);

  // This view's own actions (editor/actions.ts); the editor-only ones exist only while editing.
  const viewerActions = useMemo<readonly EditorAction[]>(() => {
    const grid: EditorAction = {
      id: 'viewer.grid',
      label: 'Toggle grid',
      group: 'View',
      keywords: 'snap',
      shortcut: { label: "Cmd/Ctrl+'", matches: isGridToggle },
      enabled: () => true,
      run: toggleGrid,
    };
    const regions = ([1, -1] as const).map(
      (step): EditorAction => ({
        id: step === 1 ? 'viewer.region.next' : 'viewer.region.previous',
        label: step === 1 ? 'Focus next region' : 'Focus previous region',
        group: 'View',
        keywords: 'panel toolbar keyboard',
        shortcut: { label: step === 1 ? 'F6' : 'Shift+F6', matches: (e) => regionShortcut(e) === step },
        enabled: () => true,
        run: () => {
          cycleRegion(step);
        },
      }),
    );
    // Export is a read-only view of what is already drawn, so it is offered in a preview too, before the editor-only
    // actions below are built.
    const hasContent = document.nodes.length > 0 || document.edges.length > 0;
    const exportActions: EditorAction[] = [
      { id: 'viewer.exportPng', label: 'Export PNG', group: 'Document', keywords: 'download image save', enabled: () => hasContent, run: () => { exportDiagram('png'); } },
      { id: 'viewer.exportSvg', label: 'Export SVG', group: 'Document', keywords: 'download image save', enabled: () => hasContent, run: () => { exportDiagram('svg'); } },
      { id: 'viewer.exportPdf', label: 'Export PDF…', group: 'Document', keywords: 'export pdf download print save page', enabled: () => hasContent, run: () => { setPdfDialogOpen(true); } },
      { id: 'viewer.exportMermaid', label: 'Export Mermaid', group: 'Document', keywords: 'download file mmd readme markdown text github', enabled: () => hasContent, run: downloadMermaid },
      { id: 'viewer.copyMermaid', label: 'Copy as Mermaid', group: 'Document', keywords: 'clipboard readme markdown text github export', enabled: () => hasContent, run: copyAsMermaid },
      { id: 'viewer.exportDsl', label: 'Export DSL', group: 'Document', keywords: 'download file text dsl source', enabled: () => hasContent, run: downloadDsl },
      // Offered only for a document that has a tour, as Export SQL is only offered for one that holds tables.
      ...(hasKeyframes(document.flythrough)
        ? [{ id: 'viewer.exportFlythrough', label: 'Export fly-through (HTML)', group: 'Document' as const, keywords: 'presentation tour standalone download play camera', enabled: () => hasContent, run: exportFlythrough }]
        : []),
      // Offered only for a diagram that holds tables, like Export PDF is only offered when it can run.
      ...(hasEntities(document)
        ? SQL_DIALECTS.filter((option) => option.value !== 'auto').map(
            (option): EditorAction => ({
              id: `viewer.exportSql.${option.value}`,
              label: `Export SQL: ${option.label}`,
              group: 'Document',
              keywords: 'download ddl create table database schema',
              enabled: () => true,
              run: () => { downloadSql(option.value as Exclude<SqlDialectChoice, 'auto'>); },
            }),
          )
        : []),
    ];
    // Present needs only a tour, so a read-only preview of a document that has one can play it.
    const presentActions: EditorAction[] = [
      {
        id: 'viewer.present',
        label: 'Present fly-through',
        group: 'View',
        keywords: 'presentation tour slideshow camera play keyframes',
        shortcut: { label: 'Cmd/Ctrl+Enter', matches: isPresent },
        enabled: () => hasKeyframes(document.flythrough),
        run: () => { present(0); },
      },
    ];
    if (!editable) {
      // A preview with no tour has nothing to play, so it is not offered there (an editor lists it, disabled, until there is one).
      return [grid, ...regions, ...(hasKeyframes(document.flythrough) ? presentActions : []), ...exportActions];
    }
    const tools = (['select', 'connector'] as const).map(
      (option): EditorAction => ({
        id: `viewer.tool.${option}`,
        label: `${TOOL_NAMES[option].name} tool`,
        group: 'Tools',
        shortcut: { label: TOOL_NAMES[option].key, matches: (e) => toolShortcut(e) === option },
        enabled: () => true,
        run: () => {
          setTool(option);
          setShapePanelOpen(false);
        },
      }),
    );
    // The keyboard way to connect: the selected shapes in selection order, on the ports that face each other (QA S5). A
    // derived-geometry document (sequence) has no ports; its messages come from the connector tool and the panel.
    const connectSelectedAction: EditorAction[] = derived ? [] : [
      {
        id: 'viewer.connectSelected',
        label: 'Connect selected shapes',
        group: 'Edit',
        keywords: 'link join connector edge arrow keyboard in order',
        shortcut: { label: 'L', matches: isConnectSelected },
        enabled: () => nodeCount >= 2,
        run: () => {
          const edgeIds = connectSelected(selection);
          if (edgeIds.length > 0) {
            canvas.current?.select(new Set(edgeIds));
          }
        },
      },
    ];
    const shapes: EditorAction = {
      id: 'viewer.tool.shapes',
      label: 'Shapes panel',
      group: 'Tools',
      shortcut: { label: 'S', matches: isShapePanelToggle },
      enabled: () => true,
      run: () => {
        setShapePanelOpen((open) => !open);
      },
    };
    // Geometry commands do not exist for a derived layout (typeCapabilities.ts); an action is absent, not disabled.
    const aligns = derived ? [] : ALIGN_ACTIONS.map(
      ({ command, label }): EditorAction => ({
        id: `viewer.align.${command}`,
        label,
        group: 'Arrange',
        enabled: () => nodeCount >= MIN_ALIGN_NODES,
        run: () => {
          align(selection, command);
        },
      }),
    );
    const distributes = derived ? [] : DISTRIBUTE_ACTIONS.map(
      ({ command, label }): EditorAction => ({
        id: `viewer.distribute.${command}`,
        label,
        group: 'Arrange',
        enabled: () => nodeCount >= MIN_DISTRIBUTE_NODES,
        run: () => {
          distribute(selection, command);
        },
      }),
    );
    // Copy, cut and paste have no live keyboard match here: the browser's own copy, cut and paste events already run
    // onClipboard with the event's DataTransfer (DiagramCanvas.tsx), and a keydown fallback covers a browser that
    // fires none. A palette run is that same fallback: onClipboard with no transfer, so it reads and writes the
    // system clipboard through the async Clipboard API with the in-memory fallback (editor/systemClipboard.ts).
    const clipboardActions: EditorAction[] = derived ? [] : [
      {
        id: 'viewer.copy',
        label: 'Copy',
        group: 'Edit',
        keywords: 'clipboard',
        shortcut: { label: 'Cmd/Ctrl+C', matches: () => false },
        enabled: () => selection.size > 0,
        run: () => {
          onClipboard('copy', selection);
        },
      },
      {
        id: 'viewer.cut',
        label: 'Cut',
        group: 'Edit',
        keywords: 'clipboard remove',
        shortcut: { label: 'Cmd/Ctrl+X', matches: () => false },
        enabled: () => selection.size > 0,
        run: () => {
          onClipboard('cut', selection);
        },
      },
      {
        id: 'viewer.paste',
        label: 'Paste',
        group: 'Edit',
        keywords: 'clipboard',
        shortcut: { label: 'Cmd/Ctrl+V', matches: () => false },
        enabled: () => true,
        run: () => {
          onClipboard('paste', selection);
        },
      },
    ];
    // Every panel action is also a palette command (design/atlas/properties-panel.md, Principles): the Container
    // section's Fit to contents button and the Connector section's kind picker are registered here since they own no
    // keyboard shortcut of their own, unlike Group and Ungroup which the canvas already registers.
    const fitAction: EditorAction = {
      id: 'viewer.fitToContents',
      label: 'Fit to contents',
      group: 'Arrange',
      keywords: 'container resize',
      enabled: () => containerState.canFit,
      run: () => {
        fitToContents(selection, padding);
      },
    };
    // Swimlane commands (model/swimlanes.ts): a selected pool gets Add lane and an orientation toggle; a selected lane
    // gets Delete, Move earlier and Move later. Every one is a single undo step and a palette row (checklist item 28).
    const swimlaneActions: EditorAction[] = [];
    if (swimlaneState.pool !== undefined) {
      const pool = swimlaneState.pool;
      swimlaneActions.push({
        id: 'viewer.addLane',
        label: 'Add lane',
        group: 'Insert',
        keywords: 'swimlane pool row column band',
        enabled: () => true,
        run: () => {
          addLane(pool.id);
        },
      });
      const target: SwimlaneOrientation = swimlaneState.orientation === 'vertical' ? 'horizontal' : 'vertical';
      swimlaneActions.push({
        id: 'viewer.poolOrientation',
        label: target === 'vertical' ? 'Arrange lanes vertically' : 'Arrange lanes horizontally',
        group: 'Arrange',
        keywords: 'swimlane pool orientation rows columns horizontal vertical',
        enabled: () => true,
        run: () => {
          setPoolOrientation(pool.id, target);
        },
      });
    }
    if (swimlaneState.lane !== undefined) {
      const lane = swimlaneState.lane;
      swimlaneActions.push({
        id: 'viewer.removeLane',
        label: 'Delete lane',
        group: 'Edit',
        keywords: 'swimlane lane remove delete',
        enabled: () => true,
        run: () => {
          removeLane(lane.id);
        },
      });
      swimlaneActions.push({
        id: 'viewer.moveLaneEarlier',
        label: 'Move lane earlier',
        group: 'Arrange',
        keywords: 'swimlane lane reorder up left earlier',
        enabled: () => swimlaneState.canMoveEarlier,
        run: () => {
          moveLane(lane.id, -1);
        },
      });
      swimlaneActions.push({
        id: 'viewer.moveLaneLater',
        label: 'Move lane later',
        group: 'Arrange',
        keywords: 'swimlane lane reorder down right later',
        enabled: () => swimlaneState.canMoveLater,
        run: () => {
          moveLane(lane.id, 1);
        },
      });
    }
    // A single selected image node offers Replace image: the picker returns a new asset and the node re-points at it,
    // keeping its box and everything else about it. Registered here so the menu and palette both list it (item 28).
    const selectedImage = selectedNodes.length === 1 && selectedNodes[0]?.shape === 'image' ? selectedNodes[0] : undefined;
    const replaceImageAction: EditorAction[] =
      selectedImage === undefined
        ? []
        : [
            {
              id: 'viewer.replaceImage',
              label: 'Replace image',
              group: 'Insert',
              keywords: 'image asset upload picture replace',
              enabled: () => true,
              run: () => {
                openImagePicker(undefined, selectedImage.id);
              },
            },
          ];
    const kindActions: EditorAction[] =
      selectedEdge === undefined
        ? []
        : edgeKindOptions(document.type, Object.keys(effectiveTheme(theme, document).edges).filter((kind) => kind !== 'default'), selectedEdge.kind).map(
            (option): EditorAction => ({
              id: `viewer.connectorKind.${option.kind}`,
              label: `Set connector kind to ${option.kind}`,
              group: 'Edit',
              keywords: 'connector edge kind',
              enabled: () => selectedEdge.kind !== option.kind,
              run: () => {
                setEdgeKind(selectedEdge.id, option.kind);
              },
            }),
          );
    const ROUTING_MODES = [
      { mode: 'orthogonal', label: 'Orthogonal' },
      { mode: 'straight', label: 'Straight' },
      { mode: 'curved', label: 'Curved' },
    ] as const;
    const routingModeActions: EditorAction[] =
      selectedEdge === undefined || derived
        ? []
        : ROUTING_MODES.map(
            ({ mode, label }): EditorAction => ({
              id: `viewer.connectorRouting.${mode}`,
              label: `Set connector routing to ${label}`,
              group: 'Edit',
              keywords: 'connector edge routing route',
              enabled: () => (selectedEdge.routing?.mode ?? 'orthogonal') !== mode,
              run: () => {
                setEdgeRoutingMode(selectedEdge.id, mode);
              },
            }),
          );
    const resetRouteAction: EditorAction[] =
      selectedEdge === undefined || derived || (selectedEdge.routing?.waypoints?.length ?? 0) === 0
        ? []
        : [
            {
              id: 'viewer.resetRoute',
              label: 'Reset route',
              group: 'Arrange',
              keywords: 'connector edge routing waypoints derived',
              enabled: () => (selectedEdge.routing?.waypoints?.length ?? 0) > 0,
              run: () => {
                resetRoute(selectedEdge.id);
              },
            },
          ];
    // Tidy lays the whole document out on the server (model/layoutClient.ts) as one undo step. The three algorithms
    // are offered explicitly so a tree (org chart) layout is reachable from the Arrange menu and the palette, not
    // only from the API.
    const tidyActions: EditorAction[] = derived ? [] : LAYOUT_ALGORITHMS.map(
      ({ algorithm, label }): EditorAction => ({
        id: `viewer.tidy.${algorithm}`,
        label: `Tidy layout (${label})`,
        group: 'Arrange',
        keywords: 'layout arranger auto arrange elk tree org chart',
        enabled: () => document.nodes.length > 0,
        run: () => {
          void tidy(algorithm)
            .then((warnings) => {
              setNotice(warnings.length > 0 ? { text: warnings.join('; ') } : undefined);
            })
            .catch((error: unknown) => {
              setNotice({ text: error instanceof Error ? error.message : 'The layout engine could not lay the diagram out.' });
            });
        },
      }),
    );
    // Participant is the type's Add shape action; Actor is its other kind, so the menu and palette reach both (item 28).
    const addActorAction: EditorAction[] = derived
      ? [{ id: 'viewer.addActor', label: 'Add actor', group: 'Insert', keywords: 'participant lifeline person sequence', enabled: () => true, run: () => { placeShape({ kind: 'actor', shape: 'rounded', label: 'Actor' }); } }]
      : [];
    // Milestone 2 of a sequence diagram (design/atlas/sequence-fragments.md). Each is enabled on the same pure answer the
    // command runs on, so a row that is enabled cannot then do nothing.
    const onlySelected = selection.size === 1 ? [...selection][0] : undefined;
    const sequenceActions: EditorAction[] = derived
      ? [
          ...sequenceFragments.WRAP_OPERATORS.map(
            (operator): EditorAction => ({
              id: `viewer.wrapFragment.${operator}`,
              label: `Wrap in fragment: ${operator}`,
              group: 'Insert',
              keywords: 'sequence combined fragment frame alt opt loop par wrap messages',
              enabled: () => sequenceFragments.wrapInFragment(document, selection, operator) !== undefined,
              run: () => {
                const wrapped = wrapInFragment(selection, operator);
                if (wrapped !== undefined) {
                  canvas.current?.select(new Set([wrapped.fragment]));
                  canvas.current?.editGuard(wrapped.fragment, wrapped.operand);
                }
              },
            }),
          ),
          {
            id: 'viewer.addFragmentOperand',
            label: 'Add operand to fragment',
            group: 'Insert',
            keywords: 'sequence fragment else branch alt par operand guard',
            enabled: () => onlySelected !== undefined && sequenceFragments.addOperand(document, onlySelected) !== undefined,
            run: () => {
              const added = onlySelected === undefined ? undefined : addFragmentOperand(onlySelected);
              if (added !== undefined) {
                canvas.current?.select(new Set([added.fragment]));
                canvas.current?.editGuard(added.fragment, added.operand);
              }
            },
          },
          {
            id: 'viewer.addNote',
            label: 'Add note',
            group: 'Insert',
            keywords: 'sequence note comment row text after message',
            enabled: () => document.nodes.some((node) => node.kind !== 'fragment'),
            run: () => {
              const noteId = addNote(onlySelected !== undefined && document.edges.some((edge) => edge.id === onlySelected) ? onlySelected : undefined);
              if (noteId !== undefined) {
                canvas.current?.select(new Set([noteId]));
                canvas.current?.edit(noteId);
              }
            },
          },
          {
            id: 'viewer.addInteractionUse',
            label: 'Add interaction use (ref)',
            group: 'Insert',
            keywords: 'sequence interaction use ref reference frame other diagram row after message',
            enabled: () => document.nodes.some((node) => node.kind !== 'fragment'),
            run: () => {
              const useId = addNote(onlySelected !== undefined && document.edges.some((edge) => edge.id === onlySelected) ? onlySelected : undefined, 'interaction-use');
              if (useId !== undefined) {
                canvas.current?.select(new Set([useId]));
                canvas.current?.edit(useId);
              }
            },
          },
          {
            id: 'viewer.toggleActivation',
            label: 'Toggle activation',
            group: 'Edit',
            keywords: 'sequence activation bar activate deactivate message receiver',
            enabled: () => onlySelected !== undefined && sequenceFragments.toggleActivation(document, onlySelected) !== undefined,
            run: () => {
              if (onlySelected !== undefined) {
                toggleActivation(onlySelected);
              }
            },
          },
          ...sequenceFragments.WRAP_OPERATORS.map(
            (operator): EditorAction => ({
              id: `viewer.fragmentOperator.${operator}`,
              label: `Set fragment operator: ${operator}`,
              group: 'Edit',
              keywords: 'sequence fragment operator tab alt opt loop par',
              enabled: () => onlySelected !== undefined && sequenceFragments.setOperator(document, onlySelected, operator) !== undefined,
              run: () => {
                if (onlySelected !== undefined) {
                  setFragmentOperator(onlySelected, operator);
                }
              },
            }),
          ),
        ]
      : [];
    return [
      ...presentActions,
      { id: 'viewer.tour.toggle', label: 'Toggle Tour panel', group: 'View', keywords: 'flythrough fly-through keyframes presentation camera', enabled: () => true, run: () => { setPanelView((view) => (view === 'tour' ? 'properties' : 'tour')); } },
      { id: 'viewer.history.toggle', label: 'Toggle History panel', group: 'View', keywords: 'versions restore compare checkpoint snapshot undo agent', enabled: () => true, run: () => { setPanelView((view) => (view === 'history' ? 'properties' : 'history')); } },
      { id: 'viewer.review.toggle', label: 'Toggle Review panel', group: 'View', keywords: 'critique check lint problems structure findings', enabled: () => true, run: () => { setPanelView((view) => (view === 'review' ? 'properties' : 'review')); } },
      { id: 'viewer.tour.addKeyframe', label: 'Add fly-through keyframe', group: 'Insert', keywords: 'capture view camera tour presentation flythrough', enabled: () => true, run: addKeyframeFromView },
      { id: 'viewer.text', label: 'Toggle Text (DSL)', group: 'View', keywords: 'text pane dsl mermaid source code edit apply', enabled: () => true, run: toggleText },
      { id: 'viewer.addShape', label: addLabel, group: 'Insert', keywords: 'shape node create new', enabled: () => true, run: addShape },
      ...tools,
      shapes,
      ...clipboardActions,
      ...kindActions,
      ...routingModeActions,
      ...resetRouteAction,
      ...tidyActions,
      ...connectSelectedAction,
      ...aligns,
      ...distributes,
      ...(derived ? [] : [fitAction]),
      ...swimlaneActions,
      ...addActorAction,
      ...sequenceActions,
      ...replaceImageAction,
      grid,
      ...regions,
      { id: 'viewer.save', label: 'Save', group: 'Document', enabled: () => saveStatus !== 'saving' && saveStatus !== 'conflict', run: () => void save() },
      { id: 'viewer.renameTitle', label: 'Rename diagram', group: 'Document', keywords: 'title name', enabled: () => !editingTitle, run: beginTitleEdit },
      ...DIAGRAM_TYPES.map(
        (option): EditorAction => ({
          id: `viewer.new.${option.type}`,
          label: `New ${option.label.toLowerCase()} diagram`,
          group: 'Document',
          keywords: 'new create diagram',
          enabled: () => true,
          // Asked before the new document is created, so a Cancel leaves no empty diagram behind; createAndOpen's own
          // navigation is then forced, having been let through here.
          run: () => {
            guardedLeave(() => {
              void createAndOpen(option.type);
            });
          },
        }),
      ),
      ...DIAGRAM_TEMPLATES.map(
        (template): EditorAction => ({
          id: `viewer.new.${template.id}`,
          label: `New ${template.label}`,
          group: 'Document',
          keywords: `new create diagram template ${template.hint.toLowerCase()}`,
          enabled: () => true,
          run: () => {
            guardedLeave(() => {
              void createAndOpen(template.type, { template: template.id });
            });
          },
        }),
      ),
      { id: 'viewer.importSql', label: 'Import SQL…', group: 'Document', keywords: 'ddl create table database schema paste tables', enabled: () => true, run: () => { setSqlImportOpen(true); } },
      { id: 'viewer.importMermaid', label: 'Import Mermaid…', group: 'Document', keywords: 'flowchart sequence diagram paste text', enabled: () => true, run: () => { setMermaidImportOpen(true); } },
      { id: 'viewer.importDrawio', label: 'Import draw.io…', group: 'Document', keywords: 'diagrams.net drawio xml file open existing diagram', enabled: () => true, run: () => { setDrawioImportOpen(true); } },
      { id: 'viewer.importVisio', label: 'Import Visio…', group: 'Document', keywords: 'vsdx microsoft visio lucidchart lucid export file open existing diagram', enabled: () => true, run: () => { setVisioImportOpen(true); } },
      { id: 'viewer.home', label: 'Back to documents', group: 'Document', keywords: 'home list documents', enabled: () => true, run: () => { navigate('/'); } },
      ...exportActions,
    ];
  }, [editable, derived, wrapInFragment, addFragmentOperand, addNote, toggleActivation, setFragmentOperator, placeShape, toggleGrid, cycleRegion, addLabel, addShape, nodeCount, selection, connectSelected, align, distribute, saveStatus, save, editingTitle, beginTitleEdit, onClipboard, containerState.canFit, fitToContents, addLane, removeLane, moveLane, setPoolOrientation, swimlaneState, padding, selectedEdge, document, theme, setEdgeKind, setEdgeRoutingMode, resetRoute, tidy, exportDiagram, exportFlythrough, copyAsMermaid, downloadMermaid, downloadDsl, downloadSql, openImagePicker, selectedNodes, present, addKeyframeFromView, toggleText]);

  // The shape panel's sequence commands: the same registry records as the menu and palette (so enabled and run cannot
  // disagree), under panel wording that says what to select first.
  const panelCommands = useMemo<readonly PanelCommand[]>(() => {
    if (document.type !== 'sequence' || !editable) {
      return [];
    }
    const panel: [id: string, label: string, hint: string][] = [
      ['viewer.wrapFragment.alt', 'Fragment: alt (if / else)', 'Select the messages to wrap'],
      ['viewer.wrapFragment.opt', 'Fragment: opt (optional)', 'Select the messages to wrap'],
      ['viewer.wrapFragment.loop', 'Fragment: loop (repeat)', 'Select the messages to wrap'],
      ['viewer.addFragmentOperand', 'Add operand (else branch)', 'Select an alt, or a message inside one'],
      ['viewer.addNote', 'Note', 'Adds a note after the selected message'],
      ['viewer.addInteractionUse', 'Interaction use (ref)', 'Adds a ref frame after the selected message'],
    ];
    return panel.flatMap(([id, label, hint]) => {
      const action = viewerActions.find((candidate) => candidate.id === id);
      return action === undefined ? [] : [{ id, label, hint, enabled: action.enabled(), run: action.run }];
    });
  }, [document.type, editable, viewerActions]);

  // Help's own entries (design/atlas/help-about-status.md): in the registry so the palette lists them and `?` runs the
  // sheet, but the menu shows them under Help rather than View (see `menu` below).
  const helpActions = useMemo<readonly EditorAction[]>(
    () => [
      { id: 'viewer.shortcuts', label: 'Keyboard shortcuts', group: 'View', shortcut: { label: '?', matches: isShortcutSheet }, keywords: 'keys help cheat sheet hotkeys', enabled: () => true, run: () => { openShortcuts.current(); } },
      ...(docsUrl !== undefined
        ? [{ id: 'viewer.docs', label: 'Documentation', group: 'View' as const, keywords: 'help manual guide docs', enabled: () => true, run: () => { window.open(docsUrl, '_blank', 'noopener,noreferrer'); } }]
        : []),
      { id: 'viewer.about', label: 'About', group: 'View', keywords: 'version support help', enabled: () => true, run: () => { navigate('/about'); } },
    ],
    [docsUrl],
  );
  // The whole registry, in palette order: what the palette and the menu both list.
  const allActions = useCallback(
    () => orderActions([...(editable ? editorActions : []), ...(canvas.current?.actions ?? []), ...viewerActions, ...helpActions]),
    [editable, editorActions, viewerActions, helpActions],
  );
  useEffect(() => {
    openShortcuts.current = () => {
      setShortcutsSheet(allActions());
    };
  }, [allActions]);
  const [palette, setPalette] = useState<readonly EditorAction[] | undefined>(undefined);
  const openPalette = useCallback(() => {
    setPalette((open) => open ?? allActions());
  }, [allActions]);
  const closePalette = useCallback(() => {
    setPalette(undefined);
    canvas.current?.focus({ visible: true });
  }, []);
  const runFromPalette = useCallback(
    (action: EditorAction) => {
      closePalette();
      action.run();
    },
    [closePalette],
  );
  // The menu's Help entry: not a registry action (the palette does not list itself), but the same shortcut's effect.
  const menu = useCallback(
    () =>
      menuSections(allActions().filter((action) => !helpActions.includes(action)), [
        { id: 'viewer.palette', label: 'Command palette', group: 'View', shortcut: { label: 'Cmd/Ctrl+K', matches: isCommandPalette }, enabled: () => true, run: openPalette },
        ...helpActions,
      ]),
    [allActions, helpActions, openPalette],
  );
  const runFromMenu = useCallback((action: EditorAction, keyboard: boolean) => {
    canvas.current?.focus(keyboard ? { visible: true } : undefined);
    action.run();
  }, []);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      if (isCommandPalette(e)) {
        e.preventDefault();
        openPalette();
        return;
      }
      // The palette keeps focus to itself: no region key or Escape of the page's may take it out from under it.
      if (palette !== undefined || shortcutsOpen) {
        return;
      }
      // Present owns every key while it is open (editor/PresentationStage.tsx stops them before they get here).
      if (presenting !== undefined) {
        return;
      }
      // Escape from anywhere in the shell but the canvas hands focus back to it; one a control used for itself (the shape
      // panel's close, a gesture's abandon on the canvas) never gets here.
      const inShell = e.target instanceof Element && e.target.closest('[data-region]') !== null && e.target.closest('.diagram-canvas') === null;
      if (e.key === 'Escape' && !e.defaultPrevented && inShell) {
        e.preventDefault();
        canvas.current?.focus({ visible: true });
        return;
      }
      // F6 works from a text field too, so the shape panel's search is not a dead end.
      dispatchShortcut(isTextField(e.target) ? viewerActions.filter((action) => action.id.startsWith('viewer.region.')) : [...viewerActions, ...helpActions], e);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [openPalette, viewerActions, helpActions, palette, shortcutsOpen, presenting]);

  const toolButton = (option: CanvasTool): ReactNode => (
    <button
      type="button"
      aria-pressed={tool === option}
      aria-label={TOOL_NAMES[option].name}
      title={`${TOOL_NAMES[option].name} (${TOOL_NAMES[option].key})`}
      onMouseDown={focusCanvas}
      onClick={() => {
        setTool(option);
        setShapePanelOpen(false);
      }}
    >
      <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        {TOOL_ICONS[option]}
      </svg>
    </button>
  );

  const historyButton = (command: 'undo' | 'redo'): ReactNode => {
    const action = editorActions.find((candidate) => candidate.id === `document.${command}`);
    if (action === undefined) {
      return null;
    }
    const enabled = action.enabled();
    return (
      <button
        type="button"
        aria-label={action.label}
        title={`${action.label} (${platformShortcut(action.shortcut?.label ?? '')})`}
        aria-disabled={!enabled}
        onMouseDown={focusCanvas}
        onClick={() => {
          if (action.enabled()) {
            action.run();
          }
        }}
      >
        <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          {HISTORY_ICONS[command]}
        </svg>
      </button>
    );
  };

  return (
    <div ref={setRoot} className="viewer">
      <div className="viewer-bar" data-region="bar">
        <EditorMenu sections={menu} onRun={runFromMenu} />
        {editable && editingTitle ? (
          <input
            ref={titleInput}
            className="viewer-title-input"
            aria-label="Diagram title"
            value={titleDraft}
            disabled={titleSaving}
            onChange={(e) => { setTitleDraft(e.target.value); }}
            onKeyDown={onTitleKeyDown}
            onBlur={commitTitleEdit}
          />
        ) : (
          <h1 className="viewer-title">
            {editable ? (
              <button type="button" className="viewer-title-button" onClick={beginTitleEdit}>
                {document.title}
              </button>
            ) : (
              document.title
            )}
          </h1>
        )}
        <span className="viewer-meta">{document.type}</span>
        {id !== undefined && (
          <span className="viewer-notice" role="status">
            {notice?.on === document ? notice.text : undefined}
          </span>
        )}
        {id !== undefined && <span className="viewer-save-status">{statusLabel(saveStatus, retrying)}</span>}
        {id !== undefined && compactLayout && (
          <button
            type="button"
            className="viewer-panel-toggle"
            aria-expanded={!panelHidden}
            aria-controls="viewer-panel"
            onClick={() => { setPanelOpen((open) => !open); }}
          >
            Panel
          </button>
        )}
        {id !== undefined && (
          <button type="button" className="viewer-text-toggle" aria-pressed={textOpen} aria-label="Text pane" aria-controls="viewer-panel" title="Text pane: the diagram as DSL or Mermaid" onClick={toggleText}>
            {/* Lucide code-xml (ISC licence) */}
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="m18 16 4-4-4-4" />
              <path d="m6 8-4 4 4 4" />
              <path d="m14.5 4-5 16" />
            </svg>
          </button>
        )}
        {id !== undefined && (
          <button type="button" disabled={saveStatus === 'saving' || saveStatus === 'conflict'} onClick={() => void save()}>
            Save
          </button>
        )}
        <label className="viewer-control">
          Theme
          <select
            value={orgTheme === undefined && !isThemeName(themeKey) ? defaultDiagramThemeName : themeKey}
            onChange={(e) => {
              if (isThemeName(e.target.value) || e.target.value.startsWith(ORG_THEME_PREFIX)) {
                setThemeChoice(e.target.value);
              }
            }}
          >
            {diagramThemeNames.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
            {orgThemes?.themes.map((t) => (
              <option key={t.id} value={orgThemeKey(t.id)}>
                {t.name} (organisation)
              </option>
            ))}
          </select>
        </label>
      </div>
      {titleError !== undefined && (
        <div className="viewer-error" role="alert">
          {titleError}
        </div>
      )}
      {saveError !== undefined && saveStatus !== 'conflict' && (
        <div className="viewer-error" role="alert">
          {saveError}
        </div>
      )}
      {saveStatus === 'conflict' && stale !== undefined && (
        <StaleSaveBar stale={stale} onReloadTheirs={() => void reloadTheirs()} onKeepMine={() => void keepMine()} onSaveMineAsNew={storeMineAsNew} />
      )}
      {saveStatus !== 'conflict' && newestCopy !== undefined && (
        <LocalCopyBar
          key={newestCopy.copyId}
          copy={newestCopy}
          others={localCopies.length - 1}
          busy={copyBusy}
          error={copyError}
          onSaveAsNew={() => { storeCopyAsNew(newestCopy.copyId); }}
          onDiscard={() => { discardLocalCopy(newestCopy.copyId); }}
        />
      )}
      {savedCopyId !== undefined && (
        <SavedCopyNotice
          onOpen={() => {
            navigate(`/d/${encodeURIComponent(savedCopyId)}`);
          }}
          onDismiss={() => {
            setSavedCopyId(undefined);
          }}
        />
      )}
      {remote !== undefined && remote.conflicts.length > 0 && (
        <div className="viewer-conflict" role="status">
          <span>{conflictNotice(remote.conflicts, document, remote.by)}</span>
          <button type="button" onClick={keepLocal}>
            Keep my version
          </button>
          <button type="button" onClick={takeTheirs}>
            Use their version
          </button>
        </div>
      )}
      <div className="viewer-body">
        {id !== undefined && (
          <div className="viewer-rail" role="toolbar" aria-label="Tools" aria-orientation="vertical" onKeyDown={railKeys} data-region="rail">
            {toolButton('select')}
            <button type="button" onMouseDown={focusCanvas} onClick={addShape} aria-label={addLabel} title={`${addLabel} at the centre of the view`}>
              {/* Lucide square-plus */}
              <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect width="18" height="18" x="3" y="3" rx="2" />
                <path d="M8 12h8" />
                <path d="M12 8v8" />
              </svg>
            </button>
            <button
              type="button"
              aria-pressed={shapePanelOpen}
              aria-label="Shapes"
              title="Shapes (S)"
              onMouseDown={focusCanvas}
              onClick={() => {
                setShapePanelOpen((open) => !open);
              }}
            >
              {/* Two overlapping primitives standing in for the shape library, not a Lucide icon. */}
              <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect x="3" y="3" width="11" height="11" rx="2" />
                <circle cx="16.5" cy="16.5" r="4.5" />
              </svg>
            </button>
            {toolButton('connector')}
            <div className="viewer-rail-history">
              {historyButton('undo')}
              {historyButton('redo')}
            </div>
          </div>
        )}
        {shapePanelOpen && <ShapePanel commands={panelCommands} documentType={document.type} theme={effectiveTheme(theme, document)} onPlace={armShape} onDragStart={dragShapeStart} onPlaceImage={() => { openImagePicker(undefined); }} onClose={closeShapePanel} />}
        <div
          className="viewer-canvas"
          onPointerDownCapture={onCanvasPointerDownCapture}
          onPointerMove={onCanvasDragPointerMove}
          onPointerUp={onCanvasDragPointerUp}
          onDragOver={onImageDragOver}
          onDrop={onImageDrop}
          data-region="canvas"
          data-armed-shape={armedShape !== undefined || undefined}
        >
          <DiagramCanvas
            ref={canvas}
            document={document}
            theme={theme}
            measure={measure}
            breakLines={breakLines}
            onViewportChange={onViewportChange}
            readOnly={!canvasEditable}
            onMoveNodes={canvasEditable ? moveNodes : undefined}
            onMoveEnd={canvasEditable ? moveEnd : undefined}
            onMoveCancel={canvasEditable ? moveCancel : undefined}
            onReparentNodes={canvasEditable && !derived ? reparentNodes : undefined}
            onGroup={canvasEditable && !derived ? onGroup : undefined}
            onSelectionChange={setSelection}
            onDeleteElements={canvasEditable ? deleteElements : undefined}
            onArrange={canvasEditable ? arrange : undefined}
            onClipboard={canvasEditable && !derived ? onClipboard : undefined}
            onLabelChange={canvasEditable ? setLabel : undefined}
            onMembersChange={canvasEditable && !derived ? setClassMembers : undefined}
            tool={canvasEditable ? tool : 'select'}
            onConnect={canvasEditable ? connectAndSelect : undefined}
            onReconnect={canvasEditable && !derived ? reconnect : undefined}
            onInsertMessage={canvasEditable && derived ? insertMessageAndEdit : undefined}
            onGuardChange={canvasEditable && derived ? setFragmentGuard : undefined}
            onReorder={canvasEditable && derived ? reorderElement : undefined}
            grid={gridOn}
            fitKey={initial}
            compareMarks={compareMarks}
          />
          {canvasEditable && <EmptyCanvasHint hasNodes={document.nodes.length > 0} />}
          <div className="viewer-zoom-cluster" role="group" aria-label="Zoom">
            <button type="button" onMouseDown={focusCanvas} onClick={() => canvas.current?.fit()} title={`Fit to screen (${platformShortcut('Cmd/Ctrl+0')})`}>
              Fit
            </button>
            <button type="button" onMouseDown={focusCanvas} onClick={() => canvas.current?.zoomOut()} aria-label="Zoom out" title={`Zoom out (${platformShortcut('Cmd/Ctrl+-')})`}>
              −
            </button>
            <button
              type="button"
              className="viewer-zoom"
              onMouseDown={focusCanvas}
              onClick={() => canvas.current?.zoomTo100()}
              title={`Zoom to 100% (${platformShortcut('Cmd/Ctrl+1')})`}
            >
              {percent(zoom)}
            </button>
            <button type="button" onMouseDown={focusCanvas} onClick={() => canvas.current?.zoomIn()} aria-label="Zoom in" title={`Zoom in (${platformShortcut('Cmd/Ctrl+=')})`}>
              +
            </button>
            <button type="button" onMouseDown={focusCanvas} onClick={toggleGrid} aria-pressed={gridOn} aria-label="Grid" title={`Grid and snap to grid (${platformShortcut("Cmd/Ctrl+'")})`}>
              {/* Lucide grid-3x3 */}
              <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect width="18" height="18" x="3" y="3" rx="2" />
                <path d="M3 9h18" />
                <path d="M3 15h18" />
                <path d="M9 3v18" />
                <path d="M15 3v18" />
              </svg>
            </button>
            <span className="visually-hidden" role="status">
              {announced}
            </span>
          </div>
        </div>
        {palette !== undefined && <CommandPalette actions={palette} onRun={runFromPalette} onClose={closePalette} />}
        {shortcutsSheet !== undefined && (
          <ShortcutSheet
            actions={shortcutsSheet}
            onClose={() => {
              setShortcutsSheet(undefined);
              canvas.current?.focus({ visible: true });
            }}
          />
        )}
        {id !== undefined && (
          <section id="viewer-panel" className={textOpen ? 'viewer-panel viewer-panel-wide' : 'viewer-panel'} aria-label="Properties" hidden={panelHidden} tabIndex={-1} onKeyDown={panelRowKeys} data-region="properties">
            <div className="dsl-controls" aria-label="Panel view">
              <button type="button" aria-pressed={panelView === 'properties'} onClick={() => { setPanelView('properties'); }}>Properties</button>
              <button type="button" aria-pressed={textOpen} onClick={() => { setPanelView('text'); }}>Text</button>
              {(aiAvailable || currentUser?.role === 'Admin') && <button type="button" aria-pressed={aiOpen} onClick={() => { setPanelView('ai'); }}>AI</button>}
              <button type="button" aria-pressed={tourOpen} onClick={() => { setPanelView('tour'); }}>Tour</button>
              <button type="button" aria-pressed={historyOpen} onClick={() => { setPanelView('history'); }}>History</button>
              <button type="button" aria-pressed={reviewOpen} onClick={() => { setPanelView('review'); }}>Review</button>
            </div>
            <div hidden={!textOpen}>
              {textVisited && (
                <DslPanel
                  document={document}
                  active={textOpen}
                  readOnly={!editable}
                  selectedIds={selection}
                  onSelect={(ids) => { canvas.current?.select(ids); }}
                  autoFocus={!restoredText}
                  onApply={applyTextDocument}
                  onMerge={(next, base) => mergeTextDocument(next, base).conflicts}
                  onTakeMine={takeTextConflicts}
                  onEscape={() => { canvas.current?.focus({ visible: true }); }}
                />
              )}
            </div>
            <div hidden={!aiOpen}>
              {aiOpen && !aiAvailable && (
                <p className="panel-hint" data-testid="ai-off">
                  AI is off. Add a provider in <a href="/admin/ai" onClick={(e) => { e.preventDefault(); navigate('/admin/ai'); }}>AI settings</a> (under the Admin menu) to describe diagrams in words, or paste DSL or Mermaid in the Text tab.
                </p>
              )}
              {aiVisited && aiAvailable && <AiPanel document={document} active={aiOpen} onApply={applyTextDocument} onRevealNode={(nodeId) => { canvas.current?.reveal(nodeId); }} onEscape={() => { canvas.current?.focus({ visible: true }); }} />}
            </div>
            <div hidden={!tourOpen}>
              {tourVisited && (
                <FlythroughPanel
                  flythrough={document.flythrough}
                  capture={() => canvas.current?.viewRect()}
                  onShow={(view) => { canvas.current?.showView(view); }}
                  canGenerate={canGenerateTour}
                  generate={generateTour}
                  onEdit={editFlythrough}
                  onPresent={present}
                />
              )}
            </div>
            <div hidden={!historyOpen}>
              {historyVisited && (
                <HistoryPanel
                  document={document}
                  active={historyOpen}
                  flush={flush}
                  onRestore={restoreVersion}
                  onMarks={setCompareMarks}
                  onReveal={revealElement}
                  onEscape={() => { canvas.current?.focus({ visible: true }); }}
                />
              )}
            </div>
            <div hidden={!reviewOpen}>
              {reviewVisited && <ReviewPanel document={document} active={reviewOpen} onSelect={selectElements} onEscape={() => { canvas.current?.focus({ visible: true }); }} />}
            </div>
            <div hidden={panelView !== 'properties'}>
            <ArrangePanel
              nodeCount={nodeCount}
              elementCount={elementCount}
              onAlign={(command) => {
                align(selection, command);
              }}
              onDistribute={(command) => {
                distribute(selection, command);
              }}
              onArrange={(command) => {
                arrange(selection, command);
              }}
              onMouseDown={focusCanvas}
              derivedGeometry={derived}
            />
            {elementCount === 0 && <p className="panel-hint">Select a node or connector to edit it.</p>}
            {selectedEdge !== undefined && (
              <ConnectorPanel
                edge={selectedEdge}
                document={document}
                theme={effectiveTheme(theme, document)}
                onKindChange={(kind) => {
                  setEdgeKind(selectedEdge.id, kind);
                }}
                onEndsChange={(arrowStart, arrowEnd) => {
                  setEdgeEnds(selectedEdge.id, arrowStart, arrowEnd);
                }}
                onRoutingModeChange={(mode) => {
                  setEdgeRoutingMode(selectedEdge.id, mode);
                }}
                onResetRoute={() => {
                  resetRoute(selectedEdge.id);
                }}
              />
            )}
            {selectedNodes.length > 0 && (
              <StylePanel target="node" nodes={selectedNodes} edges={[]} theme={effectiveTheme(theme, document)} onStyle={onStyle} onMouseDown={focusCanvas} />
            )}
            {selectedNodes.length === 0 && selectedEdges.length > 0 && (
              <StylePanel target="edge" nodes={[]} edges={selectedEdges} theme={effectiveTheme(theme, document)} onStyle={onStyle} onMouseDown={focusCanvas} />
            )}
            {!derived && <ContainerPanel
              {...containerState}
              onGroup={() => {
                groupFromPanel('group');
              }}
              onUngroup={() => {
                groupFromPanel('ungroup');
              }}
              onFit={() => {
                fitToContents(selection, padding);
              }}
              onMouseDown={focusCanvas}
            />}
            </div>
          </section>
        )}
      </div>
      {/* The one file picker the Image glyph, a drop, a paste and Replace image all drive. Hidden from view but
          reachable by script; the accepted types mirror the server's sniffing (model/assets.ts). */}
      <input
        ref={imageInput}
        type="file"
        accept={ACCEPTED_IMAGE_TYPES}
        className="visually-hidden"
        tabIndex={-1}
        aria-hidden="true"
        onChange={onImageInputChange}
      />
      {mermaidImportOpen && (
        <ImportMermaidDialog
          onClose={() => {
            setMermaidImportOpen(false);
            canvas.current?.focus({ visible: true });
          }}
          // Asked before the import creates anything (a Cancel leaves no orphan document and a usable dialog); the
          // navigation after it is forced, having been let through.
          confirmLeave={guardedLeave}
          onCreated={(created) => {
            navigate(`/d/${encodeURIComponent(created)}`, { force: true });
          }}
        />
      )}
      {drawioImportOpen && (
        <ImportDrawioDialog
          onClose={() => {
            setDrawioImportOpen(false);
            canvas.current?.focus({ visible: true });
          }}
          // As Import Mermaid: asked before anything is created, the navigation after it forced.
          confirmLeave={guardedLeave}
          onCreated={(created) => {
            navigate(`/d/${encodeURIComponent(created)}`, { force: true });
          }}
        />
      )}
      {visioImportOpen && (
        <ImportVisioDialog
          onClose={() => {
            setVisioImportOpen(false);
            canvas.current?.focus({ visible: true });
          }}
          // As Import draw.io: asked before anything is created, the navigation after it forced.
          confirmLeave={guardedLeave}
          onCreated={(created) => {
            navigate(`/d/${encodeURIComponent(created)}`, { force: true });
          }}
        />
      )}
      {mermaidText !== undefined && (
        <MermaidTextDialog
          text={mermaidText.text}
          notes={mermaidText.notes}
          {...(mermaidText.intro === undefined ? {} : { intro: mermaidText.intro })}
          onClose={() => {
            setMermaidText(undefined);
            canvas.current?.focus({ visible: true });
          }}
        />
      )}
      {pdfDialogOpen && (
        <ExportPdfDialog
          available={pdfAvailable === true}
          hint={pdfHint}
          problem={pdfProblem}
          documentId={id}
          onPdf={downloadPdf}
          onPrint={printDiagram}
          onClose={() => {
            setPdfDialogOpen(false);
            canvas.current?.focus({ visible: true });
          }}
        />
      )}
      {sqlImportOpen && (
        <ImportSqlDialog
          onClose={() => {
            setSqlImportOpen(false);
            canvas.current?.focus({ visible: true });
          }}
          // Asked before the import creates anything (a Cancel leaves no orphan document and a usable dialog); the
          // navigation after it is forced, having been let through.
          confirmLeave={guardedLeave}
          onCreated={(created) => {
            navigate(`/d/${encodeURIComponent(created)}`, { force: true });
          }}
        />
      )}
      {presenting !== undefined && (
        <PresentationStage
          document={presenting.document}
          flythrough={presenting.flythrough}
          theme={theme}
          measure={measure}
          breakLines={breakLines}
          startIndex={presenting.startIndex}
          onClose={closePresentation}
        />
      )}
      {/* Last, so it sits over an import dialog whose Create is what asked to leave. */}
      {leaveGuard.pending && (
        <LeaveDialog saving={leaveGuard.saving} error={saveStatus === 'error' || saveStatus === 'conflict' ? saveError : undefined} onSave={leaveGuard.save} onDiscard={leaveGuard.discard} onCancel={leaveGuard.cancel} />
      )}
    </div>
  );
}
