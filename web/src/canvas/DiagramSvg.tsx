/**
 * The read-only SVG render of a Document: one `<g data-id>` per node and edge, virtualised to the viewport. This is
 * a pure function of (document, theme, viewport, size, surface); interaction lives in DiagramCanvas. Everything the
 * tree needs (colours, fonts, markers, clips) is inside the `<svg>` and resolved from the document's diagram theme,
 * never the chrome. The `surface` picks which theme background is painted (the canvas one on screen, the export one
 * when export re-renders the document for serialisation) and with it the colour of text drawn straight on that
 * background: edge labels, and transparent-fill nodes that sit on it rather than inside a container.
 *
 * Checklist item 29 (2,000 nodes and 2,500 edges at 60 fps): the node and edge tree is memoised on everything but the
 * viewport, and the visible set is kept while it still covers the view (virtualise.ts `visibleSetMemo`), so a pan or
 * zoom frame that mounts nothing new changes only the world transform and the overlays, with no reconciliation of the
 * elements. On the canvas surface, labels below a legible size in device pixels are not mounted (`labelsLegible`), and
 * below FULL_DETAIL_ZOOM nodes are drawn as plain boxes and edges as plain lines (`fullDetail`, NodeView's and
 * EdgeView's `simplified`); hit-testing is unchanged, since every element keeps its `<g data-id>` and an edge its hit
 * stroke. The export surface always draws full detail.
 */

import { useMemo, useState, type ReactNode } from 'react';
import { ConnectorOverlay, type ConnectorOverlayState } from './ConnectorOverlay.tsx';
import type { EdgeEndHandles } from './connectHandles.ts';
import { CompareOverlay } from './CompareOverlay.tsx';
import { DropTargetOverlay } from './DropTargetOverlay.tsx';
import type { CompareMarks } from '../model/compare.ts';
import type { Document, Id } from '../model/types.ts';
import { assetIdOf, assetUrl } from '../model/assets.ts';
import type { DiagramTheme, EdgeKindStyle, NodeKindStyle, Surface } from '../theme/diagramTheme.ts';
import { effectiveTheme, resolveEdgeStyle, resolveNodeStyle, surfaceBackground, surfaceTextColor } from '../theme/resolveStyle.ts';
import { indexDocument, isContainer, type DocumentIndex, type PlacedNode } from './documentIndex.ts';
import { withRoutes } from './edgeRoutes.ts';
import { EdgeView } from './EdgeView.tsx';
import type { Rect } from './geometry.ts';
import { MarqueeBox, SnapGuides } from './GestureOverlay.tsx';
import { edgeLabelAt, measureLabelBox } from './labelEditing.ts';
import { placeLabels, type LabelBox, type LabelInput } from './labelLayout.ts';
import { markerKey, type MarkerKey } from './markerIds.ts';
import { ArrowMarker } from './markers.tsx';
import { NodeView } from './NodeView.tsx';
import { SequenceLayer } from './SequenceLayer.tsx';
import { edgeLabelStyle, sitsOnFilledContainer, withSurfaceText } from './renderedStyle.ts';
import type { OrthogonalRoute } from './routing.ts';
import type { ShapePart } from '../model/memberParts.ts';
import { SelectionOverlay } from './SelectionOverlay.tsx';
import type { Guide } from './snapping.ts';
import { estimateTextWidth, type LineBreaker, type TextMeasurer } from './text.ts';
import { fullDetail, labelsLegible, visibleSetMemo, VIEWPORT_MARGIN, type VisibleSet } from './virtualise.ts';
import { viewportTransform, visibleWorldRect, type Size, type Viewport } from './viewport.ts';

export type { Surface } from '../theme/diagramTheme.ts';

export interface DiagramSvgProps {
  document: Document;
  /** The workspace diagram theme; the document's own token overrides are applied on top. */
  theme: DiagramTheme;
  viewport: Viewport;
  size: Size;
  /** Defaults to the on-screen canvas; export passes `export`. */
  surface?: Surface | undefined;
  /** A prebuilt index of `document`, when the caller already has one; otherwise built and memoised here. */
  index?: DocumentIndex | undefined;
  measure?: TextMeasurer | undefined;
  /** Breaks node labels into lines as the label editor does (NodeView.tsx); undefined wraps them against `measure`. */
  breakLines?: LineBreaker | undefined;
  /** Prefix for every id the SVG defines (markers, clips) so several diagrams can share a page. */
  idPrefix?: string | undefined;
  /** The selected element ids to draw handles and an outline around; the view state of the editor. */
  selection?: ReadonlySet<Id> | undefined;
  /** The node id and quantised angle of a rotate drag in progress (checklist item 4); view state of the editor. */
  rotating?: { id: Id; rotation: number } | undefined;
  /** The end handles of each selected edge (connectHandles.ts `selectedEdgeEnds`), for an editor that can re-attach it; none by default. */
  edgeEndpoints?: EdgeEndHandles | undefined;
  /** Nodes mid-drag, lifted out of their container's clip into a layer above the drawing; view state, not the model. */
  draggedNodes?: ReadonlySet<Id> | undefined;
  /** Containers a drag would drop into if released now, outlined as drop targets; view state, not the model. */
  dropTargets?: ReadonlySet<Id> | undefined;
  /** What a version comparison marks (History panel): added, changed and removed elements; view state, never part of the model or an export. */
  compareMarks?: CompareMarks | undefined;
  /** The node or edge whose label a label editor is drawing over; its own label is left out so the two never overlap. */
  editingLabel?: Id | undefined;
  /** Which part of the edited UML class or ER entity the editor covers (its name band or a member compartment); the rest stays drawn. */
  editingPart?: ShapePart | undefined;
  /** Sequence diagrams: the fragment guard being edited in place, whose text is left out of the frame. */
  editingGuard?: { fragment: Id; operand: number } | undefined;
  /** Router output for orthogonal edges (edgeRoutes.ts), drawn in place of the index's path; derived, never stored. */
  routes?: ReadonlyMap<Id, OrthogonalRoute> | undefined;
  /** The connector tool's port markers and preview line; view state of the editor. */
  connector?: ConnectorOverlayState | undefined;
  /** The smart guides of a snapped drag or resize in progress; view state of the editor. */
  guides?: readonly Guide[] | undefined;
  /** The marquee being dragged, in world units; view state of the editor. */
  marquee?: Rect | undefined;
  /** The editor's grid toggle; left undefined, the theme's `grid.visible` decides. */
  showGrid?: boolean | undefined;
  /** Device pixels per CSS pixel (`window.devicePixelRatio`), for the canvas's label legibility floor; 1 by default. */
  pixelRatio?: number | undefined;
  /** Fraction of the viewport added on each side before culling. */
  margin?: number | undefined;
  /**
   * Resolves an image node's asset id to the href its `<image>` draws: the served `/api/assets/{id}` URL on the
   * canvas, or an inlined data URI for a self-contained export (editor/exportDiagram.tsx). Returning undefined
   * leaves the node's placeholder frame.
   */
  assetHref?: ((assetId: string) => string | undefined) | undefined;
  className?: string | undefined;
}

/** The canvas default: the asset endpoint serves the picture (the export surface passes a data-URI resolver instead). */
function defaultAssetHref(assetId: string): string {
  return assetUrl(assetId);
}

/** Hide the grid below this many screen pixels per cell; finer than that it is moiré, not a grid. */
const MIN_GRID_PIXELS = 8;

interface ResolvedStyles {
  nodes: Map<string, NodeKindStyle>;
  edges: Map<string, EdgeKindStyle>;
}

/**
 * Every element's resolved style, computed once per (effective theme, document, surface) so the memoised node and
 * edge bodies see stable props across pan and zoom frames. Only a label the surface itself backs takes the surface
 * text colour; see `labelSitsOnSurface` in renderedStyle.ts.
 */
function resolveStyles(theme: DiagramTheme, workspace: DiagramTheme, document: Document, index: DocumentIndex, surfaceText: string | undefined): ResolvedStyles {
  const nodes = new Map<string, NodeKindStyle>();
  for (const node of document.nodes) {
    const style = resolveNodeStyle(theme, node);
    // A node still waiting for layout is drawn dashed so a provisional position can never pass for a settled one.
    const resolved = index.unplaced.has(node.id) ? { ...style, dash: 'dashed' as const } : style;
    nodes.set(node.id, withSurfaceText(workspace, theme, node, resolved, sitsOnFilledContainer(theme, node, index.nodesById), surfaceText));
  }
  const edges = new Map<string, EdgeKindStyle>();
  for (const edge of document.edges) {
    edges.set(edge.id, resolveEdgeStyle(theme, edge));
  }
  return { nodes, edges };
}

export function DiagramSvg({
  document,
  theme,
  viewport,
  size,
  surface = 'canvas',
  index: providedIndex,
  measure,
  breakLines,
  idPrefix = '',
  margin = VIEWPORT_MARGIN,
  className,
  selection,
  rotating,
  edgeEndpoints,
  draggedNodes,
  dropTargets,
  compareMarks,
  editingLabel,
  editingPart,
  editingGuard,
  routes,
  connector,
  guides,
  marquee,
  showGrid: gridToggle,
  pixelRatio = 1,
  assetHref = defaultAssetHref,
}: DiagramSvgProps) {
  const baseIndex = useMemo(() => draggedNodes?.size ? indexDocument(document, draggedNodes) : providedIndex ?? indexDocument(document), [providedIndex, document, draggedNodes]);
  const index = useMemo(() => (routes === undefined ? baseIndex : withRoutes(baseIndex, routes)), [baseIndex, routes]);
  const resolved = useMemo(() => effectiveTheme(theme, document), [theme, document]);
  const [visibleIn] = useState(visibleSetMemo);
  const visible = useMemo(() => visibleIn(index, visibleWorldRect(viewport, size), margin), [visibleIn, index, viewport, size, margin]);
  const background = surfaceBackground(resolved, surface);
  const surfaceText = surfaceTextColor(resolved, surface);
  const styles = useMemo(() => resolveStyles(resolved, theme, document, index, surfaceText), [resolved, theme, document, index, surfaceText]);
  const labelStyle = useMemo(() => edgeLabelStyle(resolved, surface), [resolved, surface]);
  // The canvas's far-zoom level of detail; export, like its labels, is always drawn in full.
  const simplified = surface === 'canvas' && !fullDetail(viewport.zoom);
  // Export is paper at whatever scale the reader chooses, so it always carries its labels.
  const showLabels = !simplified && (surface !== 'canvas' || labelsLegible(viewport.zoom, pixelRatio));

  // Everything below depends on the viewport only through `visible`, `showLabels` and `simplified`, so the tree is
  // rebuilt only when one of them changes, never for a frame that just moves or scales the world.
  const drawn = useMemo(() => {
    const markers = new Map<string, MarkerKey>();
    const edges: ReactNode[] = [];
    // Two labels on different connectors must never overlap (Finding 4), and a label must never land inside a node
    // that is not its own connector's endpoint (M2 final-check Finding C): measure every visible label's plate up
    // front, at its default midpoint, and nudge later ones clear of both before any label is drawn. Node obstacles
    // are only the visible ones (like the labels themselves), so this stays proportional to what is on screen.
    const labelInputs: LabelInput[] = [];
    // A sequence message's label sits over its own row, so the scene has already placed it (model/sequenceScene.ts).
    if (showLabels && index.sequence === undefined) {
      for (const edge of document.edges) {
        if (!visible.edges.has(edge.id) || edge.id === editingLabel || edge.label === undefined || edge.label.length === 0) {
          continue;
        }
        const path = index.edgePaths.get(edge.id);
        if (!path) {
          continue;
        }
        const { width, height } = measureLabelBox(edge.label, labelStyle.font, measure ?? estimateTextWidth);
        const base = edgeLabelAt(path.points, edge.routing?.mode === 'curved');
        labelInputs.push({ id: edge.id, base, points: path.points, width, height, excludeNodeIds: [edge.from.node, edge.to.node] });
      }
    }
    const nodeBoxes = new Map<Id, LabelBox>();
    if (showLabels) {
      for (const nodeId of visible.nodes) {
        const bounds = index.nodeBounds.get(nodeId);
        if (bounds !== undefined) {
          nodeBoxes.set(nodeId, bounds);
        }
      }
    }
    const labelPositions = index.sequence === undefined ? placeLabels(labelInputs, nodeBoxes) : new Map(index.sequence.messages.flatMap((message) => (message.labelAt === undefined ? [] : [[message.id, message.labelAt] as const])));
    // A note is drawn by the sequence layer as a box, not as a line, so it is left out of the edge loop.
    const noteIds = new Set(index.sequence?.messages.filter((message) => message.note !== undefined).map((message) => message.id));
    for (const edge of document.edges) {
      if (!visible.edges.has(edge.id) || noteIds.has(edge.id)) {
        continue;
      }
      const path = index.edgePaths.get(edge.id);
      const style = styles.edges.get(edge.id);
      if (!path || !style) {
        continue;
      }
      for (const arrow of simplified ? [] : [style.arrowStart, style.arrowEnd]) {
        if (arrow !== 'none') {
          markers.set(markerKey(arrow, style.stroke), { arrow, colour: style.stroke });
        }
      }
      edges.push(
        <EdgeView
          key={edge.id}
          edge={edge}
          points={path.points}
          style={style}
          font={labelStyle.font}
          labelBackground={labelStyle.background}
          labelColor={labelStyle.colour}
          idPrefix={idPrefix}
          interactive={surface === 'canvas'}
          measure={measure}
          routeStatus={routes?.get(edge.id)?.status}
          hideLabel={!showLabels || edge.id === editingLabel}
          labelAt={labelPositions.get(edge.id)}
          simplified={simplified}
        />,
      );
    }
    const hideLabel = (id: Id): boolean => !showLabels || id === editingLabel;
    const partOf = (id: Id): ShapePart | undefined => (id === editingLabel ? editingPart ?? 'name' : undefined);
    return {
      sequence: index.sequence === undefined ? undefined : <SequenceLayer scene={index.sequence} styles={styles.nodes} edgeStyles={styles.edges} edgeOf={(id) => index.edgesById.get(id)} paint={{ font: labelStyle.font, background: labelStyle.background, colour: labelStyle.colour }} interactive={surface === 'canvas'} editing={editingLabel} editingGuard={editingGuard} />,
      markers: [...markers.values()].map((key) => <ArrowMarker key={markerKey(key.arrow, key.colour)} prefix={idPrefix} arrow={key.arrow} colour={key.colour} />),
      nodes: <g className="nodes">{renderNodes(index.rootNodes.filter((node) => !draggedNodes?.has(node.id)), index, styles.nodes, visible, { measure, breakLines }, idPrefix, hideLabel, partOf, simplified, assetHref)}</g>,
      edges: <g className="edges">{edges}</g>,
      dragLayer: draggedNodes !== undefined && draggedNodes.size > 0 ? <g className="drag-layer">{renderNodes(index.rootNodes.filter((node) => draggedNodes.has(node.id)), index, styles.nodes, visible, { measure, breakLines }, idPrefix, hideLabel, partOf, simplified, assetHref)}</g> : undefined,
    };
  }, [document.edges, visible, index, styles, labelStyle, idPrefix, surface, measure, breakLines, routes, showLabels, simplified, editingLabel, editingPart, editingGuard, draggedNodes, assetHref]);

  const gridCell = resolved.grid.size * viewport.zoom;
  // The export surface is paper: a client deck carries the drawing, never the editor's grid.
  const showGrid = surface === 'canvas' && (gridToggle ?? resolved.grid.visible) && gridCell >= MIN_GRID_PIXELS;
  const gridId = `${idPrefix}grid`;

  return (
    <svg
      className={className}
      xmlns="http://www.w3.org/2000/svg"
      width={size.width}
      height={size.height}
      viewBox={`0 0 ${String(size.width)} ${String(size.height)}`}
      data-diagram-theme={resolved.name}
      data-surface={surface}
      data-document-id={document.id}
      fontFamily={resolved.fonts.label}
      style={{ display: 'block' }}
    >
      <defs>
        {drawn.markers}
        {showGrid && (
          <pattern id={gridId} width={resolved.grid.size} height={resolved.grid.size} patternUnits="userSpaceOnUse" patternTransform={viewportTransform(viewport)}>
            <path d={`M ${String(resolved.grid.size)} 0 L 0 0 0 ${String(resolved.grid.size)}`} fill="none" stroke={resolved.grid.color} strokeWidth={1} vectorEffect="non-scaling-stroke" />
          </pattern>
        )}
      </defs>
      <rect className="canvas-background" width={size.width} height={size.height} fill={background} />
      {showGrid && <rect className="canvas-grid" width={size.width} height={size.height} fill={`url(#${gridId})`} />}
      <g className="world" transform={viewportTransform(viewport)}>
        {drawn.sequence}
        {drawn.nodes}
        {drawn.edges}
        {drawn.dragLayer}
      </g>
      {dropTargets !== undefined && dropTargets.size > 0 && <DropTargetOverlay targets={dropTargets} index={index} viewport={viewport} />}
      {compareMarks !== undefined && <CompareOverlay marks={compareMarks} index={index} viewport={viewport} />}
      {selection !== undefined && <SelectionOverlay selection={selection} index={index} viewport={viewport} rotating={rotating} edgeEndpoints={edgeEndpoints} />}
      {connector !== undefined && <ConnectorOverlay state={connector} index={index} viewport={viewport} />}
      {guides !== undefined && <SnapGuides guides={guides} viewport={viewport} />}
      {marquee !== undefined && <MarqueeBox rect={marquee} viewport={viewport} />}
    </svg>
  );
}

/** How node labels are measured and broken into lines. */
interface LabelText {
  measure: TextMeasurer | undefined;
  breakLines: LineBreaker | undefined;
}

function renderNodes(
  nodes: readonly PlacedNode[],
  index: DocumentIndex,
  styles: ReadonlyMap<string, NodeKindStyle>,
  visible: VisibleSet,
  text: LabelText,
  idPrefix: string,
  hideLabel: (id: Id) => boolean,
  partOf: (id: Id) => ShapePart | undefined,
  simplified: boolean,
  assetHref: (assetId: string) => string | undefined,
): ReactNode[] {
  const out: ReactNode[] = [];
  for (const node of nodes) {
    const style = styles.get(node.id);
    if (!visible.nodes.has(node.id) || !style) {
      continue;
    }
    const assetId = node.shape === 'image' ? assetIdOf(node) : undefined;
    const imageHref = assetId === undefined ? undefined : assetHref(assetId);
    const children = index.childrenOf.get(node.id);
    const container = isContainer(index, node);
    const rendered = children ? renderNodes(children, index, styles, visible, text, idPrefix, hideLabel, partOf, simplified, assetHref) : [];
    out.push(
      <NodeView key={node.id} node={node} style={style} container={container} measure={text.measure} breakLines={text.breakLines} idPrefix={idPrefix} unplaced={index.unplaced.has(node.id)} hideLabel={hideLabel(node.id)} editingPart={partOf(node.id)} simplified={simplified} imageHref={imageHref}>
        {rendered.length > 0 ? rendered : undefined}
      </NodeView>,
    );
  }
  return out;
}
